using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Rustzor.Build.Aot;

/// <summary>
/// Rustzor's ahead-of-time compiler: IL to WebAssembly, for the methods it can
/// take, with everything else left to the interpreter.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it compiles.</b> Static methods whose parameters, locals and result
/// are primitives (<c>bool</c>, <c>char</c>, integers, <c>float</c>,
/// <c>double</c>) or one-dimensional arrays of <c>int</c>, <c>long</c>,
/// <c>float</c>, <c>double</c> and <c>byte</c>; whose bodies use arithmetic,
/// comparisons, conversions, branches, <c>switch</c>, array element access and
/// calls to other compiled methods or to <c>System.Math</c>; and which have no
/// exception handlers. That is the shape of the code that is worth compiling:
/// physics steps, image filters, numeric kernels, game logic.
/// </para>
/// <para>
/// <b>What it declines</b>, and says so in <c>aot-report.txt</c>: anything
/// touching objects, strings, fields, allocation or exceptions. A declined
/// method runs on the interpreter exactly as before, so AOT can only change
/// speed, never results — the same contract RustNetRuntime's JIT keeps.
/// </para>
/// <para>
/// <b>How.</b> Each IL evaluation-stack slot, at each depth and type, becomes a
/// WebAssembly local, so the WebAssembly operand stack is empty between IL
/// instructions and at every block boundary. Basic blocks are laid out inside
/// nested <c>block</c>s under one <c>loop</c>: a forward branch is a direct
/// <c>br</c>, a backward branch sets a block index and re-enters the loop
/// through a <c>br_table</c>. Faults (index out of range, division by zero,
/// overflow) are written to address 0 and become the managed exception the
/// interpreter would have thrown.
/// </para>
/// </remarks>
public static class AotCompiler
{
    /// <summary>Export every compiled method, not only the worthwhile ones (testing).</summary>
    public static bool ExportAll = Environment.GetEnvironmentVariable("RUSTZOR_AOT_EXPORT_ALL") == "1";

    public static int Run(string assemblyDir, string entry, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var units = new List<Unit>();
        foreach (var path in Directory.GetFiles(assemblyDir, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // The framework libraries are interop and UI: nothing here to gain.
            if (name.StartsWith("Rustzor.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal))
                continue;
            try
            {
                units.Add(new Unit(path));
            }
            catch (BadImageFormatException)
            {
            }
        }

        var module = new WasmModule();
        var manifest = new StringBuilder();
        manifest.AppendLine("# Rustzor AOT manifest: assembly|token|index|params|result");
        var report = new StringBuilder();
        var compiled = 0;
        var exported = 0;
        var considered = 0;

        foreach (var unit in units) unit.Analyse();

        // Translate as a dry run until the compiled set is stable: a method
        // that fails translation is declined, which can decline its callers.
        bool changed;
        do
        {
            changed = false;
            foreach (var unit in units)
            {
                foreach (var m in unit.Methods.Where(m => m.Decline is null).ToList())
                {
                    try
                    {
                        new Translator(unit, m).Translate();
                    }
                    catch (DeclineException e)
                    {
                        m.Decline = e.Message;
                        changed = true;
                    }
                }
                if (unit.DeclineCallersOfDeclined()) changed = true;
            }
        } while (changed);

        // Declare first, so calls between compiled methods can name each other.
        foreach (var unit in units)
        {
            foreach (var m in unit.Methods)
            {
                considered++;
                if (m.Decline is not null) continue;
                m.Function = module.Declare(module.TypeOf(m.WasmParams(), m.WasmResults()));
            }
        }

        foreach (var unit in units)
        {
            foreach (var m in unit.Methods.Where(m => m.Decline is null))
            {
                var (locals, body) = new Translator(unit, m).Translate();
                module.Define(m.Function, locals, body);
                compiled++;
            }
        }

        var index = 0;
        foreach (var unit in units)
        {
            foreach (var m in unit.Methods)
            {
                if (m.Decline is not null)
                {
                    report.AppendLine($"  interpreted  {unit.Name}::{m.Name}  ({m.Decline})");
                    continue;
                }
                if (!m.WorthExporting)
                {
                    report.AppendLine($"  compiled     {unit.Name}::{m.Name}  (called from compiled code only)");
                    continue;
                }
                var trampoline = BuildTrampoline(module, m);
                module.ExportFunction("t" + index, trampoline);
                manifest.AppendLine($"{unit.Name}|0x{m.Token:X8}|{index}|{m.Shape()}|{Shape(m.Result)}");
                report.AppendLine($"  compiled     {unit.Name}::{m.Name}  -> t{index}");
                index++;
                exported++;
            }
        }

        File.WriteAllBytes(Path.Combine(outputDir, "app.aot.wasm"), module.ToBytes());
        File.WriteAllText(Path.Combine(outputDir, "app.aot.txt"), manifest.ToString());
        File.WriteAllText(Path.Combine(outputDir, "aot-report.txt"),
            $"Rustzor AOT: {compiled} of {considered} methods compiled to WebAssembly, {exported} callable from the interpreter.{Environment.NewLine}{report}");
        Console.WriteLine($"Rustzor AOT: {compiled} of {considered} methods compiled to WebAssembly ({exported} entry points). Report: {Path.Combine(outputDir, "aot-report.txt")}");
        return 0;
    }

    private static int BuildTrampoline(WasmModule module, MethodModel m)
    {
        // t(argbase) -> i64: reads each parameter from its 64-bit slot.
        var type = module.TypeOf(new[] { WasmType.I32 }, new[] { WasmType.I64 });
        var index = module.Declare(type);
        var code = new WasmCode();
        for (var i = 0; i < m.Params.Length; i++)
        {
            code.Get(0);
            var offset = (uint)(i * 8);
            switch (m.Params[i])
            {
                case Prim.I8 or Prim.U8:
                    code.Mem(Op.I64Load, 3, offset);
                    break;
                case Prim.R8:
                    code.Mem(Op.F64Load, 3, offset);
                    break;
                case Prim.R4:
                    code.Mem(Op.F32Load, 2, offset).Emit(Op.F64PromoteF32);
                    break;
                default:
                    code.Mem(Op.I32Load, 2, offset);
                    break;
            }
        }
        code.Emit(Op.Call, (uint)m.Function);
        switch (m.Result)
        {
            case null:
                code.I64(0);
                break;
            case Prim.I8 or Prim.U8:
                break;
            case Prim.R8:
                code.Emit(Op.I64ReinterpretF64);
                break;
            case Prim.R4:
                code.Emit(Op.F32DemoteF64).Emit(Op.I32ReinterpretF32).Emit(Op.I64ExtendI32U);
                break;
            default:
                code.Emit(Op.I64ExtendI32S);
                break;
        }
        code.Emit(Op.End);
        module.Define(index, new List<WasmType>(), code.ToArray());
        return index;
    }

    internal static void EmitDefault(WasmCode code, Prim? result)
    {
        switch (result.HasValue ? Kinds.Stack(result.Value) : (K?)null)
        {
            case null: break;
            case K.I64: code.I64(0); break;
            case K.F64: code.F64(0); break;
            default: code.I32(0); break;
        }
    }

    internal static string Shape(Prim? p) => p switch
    {
        null => "v",
        Prim.I8 or Prim.U8 => "l",
        Prim.R4 => "f",
        Prim.R8 => "d",
        Prim.ArrI4 => "I",
        Prim.ArrI8 => "L",
        Prim.ArrR4 => "F",
        Prim.ArrR8 => "D",
        Prim.ArrU1 => "Y",
        _ => "i",
    };
}

internal sealed class DeclineException : Exception
{
    public DeclineException(string reason) : base(reason)
    {
    }
}

/// <summary>Storage types the compiler accepts.</summary>
internal enum Prim
{
    Bool, Char, I1, U1, I2, U2, I4, U4, I8, U8, R4, R8,
    ArrI4, ArrI8, ArrR4, ArrR8, ArrU1,
}

/// <summary>Evaluation-stack types.</summary>
internal enum K
{
    I32, I64, F64, ArrI4, ArrI8, ArrR4, ArrR8, ArrU1,
}

internal static class Kinds
{
    public static K Stack(Prim p) => p switch
    {
        Prim.I8 or Prim.U8 => K.I64,
        Prim.R4 or Prim.R8 => K.F64,
        Prim.ArrI4 => K.ArrI4,
        Prim.ArrI8 => K.ArrI8,
        Prim.ArrR4 => K.ArrR4,
        Prim.ArrR8 => K.ArrR8,
        Prim.ArrU1 => K.ArrU1,
        _ => K.I32,
    };

    public static WasmType Wasm(K k) => k switch
    {
        K.I64 => WasmType.I64,
        K.F64 => WasmType.F64,
        _ => WasmType.I32,
    };

    public static bool IsArray(K k) => k >= K.ArrI4;
}

internal sealed class MethodModel
{
    public required string Name;
    public required int Token;
    public required Prim[] Params;
    public required Prim? Result;
    public Prim[] Locals = Array.Empty<Prim>();
    public byte[] Il = Array.Empty<byte>();
    public List<Instr> Code = new();
    public string? Decline;
    public int Function;
    public bool HasLoop;
    public readonly List<int> Callees = new();

    public bool WorthExporting => HasLoop || Callees.Count > 0 || Il.Length >= 48 || AotCompiler.ExportAll;

    public WasmType[] WasmParams() => Params.Select(p => Kinds.Wasm(Kinds.Stack(p))).ToArray();

    public WasmType[] WasmResults() => Result is null ? Array.Empty<WasmType>() : new[] { Kinds.Wasm(Kinds.Stack(Result.Value)) };

    public string Shape() => string.Concat(Params.Select(p => AotCompiler.Shape(p)));
}

internal sealed class Instr
{
    public int Offset;
    public int Next;
    public ushort Op;
    public long I;
    public double F;
    public int[] Targets = Array.Empty<int>();
}

/// <summary>One assembly: its methods, read and checked.</summary>
internal sealed class Unit
{
    private readonly PEReader _pe;
    public readonly MetadataReader Md;
    public readonly string Name;
    public readonly List<MethodModel> Methods = new();
    public readonly Dictionary<int, MethodModel> ByToken = new();

    public Unit(string path)
    {
        _pe = new PEReader(File.OpenRead(path));
        if (!_pe.HasMetadata) throw new BadImageFormatException();
        Md = _pe.GetMetadataReader();
        Name = Md.GetString(Md.GetAssemblyDefinition().Name);
    }

    public void Analyse()
    {
        foreach (var handle in Md.MethodDefinitions)
        {
            var def = Md.GetMethodDefinition(handle);
            var token = MetadataTokens.GetToken(handle);
            var type = Md.GetTypeDefinition(def.GetDeclaringType());
            var name = Md.GetString(type.Name) + "." + Md.GetString(def.Name);
            MethodModel m;
            try
            {
                var (parms, result) = Signatures.Method(Md.GetBlobReader(def.Signature));
                m = new MethodModel { Name = name, Token = token, Params = parms, Result = result };
            }
            catch (DeclineException e)
            {
                m = new MethodModel { Name = name, Token = token, Params = Array.Empty<Prim>(), Result = null, Decline = e.Message };
                Methods.Add(m);
                continue;
            }
            Methods.Add(m);
            ByToken[token] = m;
            try
            {
                Load(def, m);
            }
            catch (DeclineException e)
            {
                m.Decline = e.Message;
            }
        }

        DeclineCallersOfDeclined();
    }

    /// <summary>
    /// A compiled method may only call compiled methods: declines callers of
    /// anything declined until nothing changes. Returns whether anything did.
    /// </summary>
    public bool DeclineCallersOfDeclined()
    {
        var any = false;
        bool changed;
        do
        {
            changed = false;
            foreach (var m in Methods.Where(m => m.Decline is null))
            {
                foreach (var callee in m.Callees)
                {
                    if (ByToken.TryGetValue(callee, out var c) && c.Decline is null) continue;
                    m.Decline = "calls a method that is not compiled";
                    changed = true;
                    any = true;
                    break;
                }
            }
        } while (changed);
        return any;
    }

    private void Load(MethodDefinition def, MethodModel m)
    {
        if ((def.Attributes & System.Reflection.MethodAttributes.Static) == 0) throw new DeclineException("instance method");
        if (def.RelativeVirtualAddress == 0) throw new DeclineException("no IL body");
        if (def.GetGenericParameters().Count > 0) throw new DeclineException("generic method");
        var body = _pe.GetMethodBody(def.RelativeVirtualAddress);
        if (body.ExceptionRegions.Length > 0) throw new DeclineException("exception handlers");
        m.Il = body.GetILBytes() ?? Array.Empty<byte>();
        if (m.Il.Length > 16384) throw new DeclineException("too large");
        if (!body.LocalSignature.IsNil)
        {
            var sig = Md.GetStandaloneSignature(body.LocalSignature);
            m.Locals = Signatures.Locals(Md.GetBlobReader(sig.Signature));
        }
        m.Code = IlDecoder.Decode(m.Il);
        foreach (var ins in m.Code)
        {
            if (ins.Op == 0x28)
            {
                var token = (int)ins.I;
                if ((token >> 24) == 0x06) m.Callees.Add(token);
                else if (Intrinsics.Lookup(Md, token) is null) throw new DeclineException("calls " + Describe(token));
            }
            foreach (var t in ins.Targets)
                if (t <= ins.Offset) m.HasLoop = true;
        }
    }

    public string Describe(int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MemberReference)
            {
                var mr = Md.GetMemberReference((MemberReferenceHandle)handle);
                var parent = mr.Parent.Kind == HandleKind.TypeReference
                    ? Md.GetString(Md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name)
                    : "?";
                return parent + "." + Md.GetString(mr.Name);
            }
        }
        catch
        {
        }
        return $"0x{token:X8}";
    }
}

internal static class Signatures
{
    public static (Prim[] Params, Prim? Result) Method(BlobReader blob)
    {
        var header = blob.ReadByte();
        if ((header & 0x20) != 0) throw new DeclineException("instance method");
        if ((header & 0x10) != 0) throw new DeclineException("generic method");
        var count = blob.ReadCompressedInteger();
        var result = TypeOrVoid(ref blob);
        var parms = new Prim[count];
        for (var i = 0; i < count; i++) parms[i] = Type(ref blob);
        return (parms, result);
    }

    public static Prim[] Locals(BlobReader blob)
    {
        if (blob.ReadByte() != 0x07) throw new DeclineException("unexpected local signature");
        var count = blob.ReadCompressedInteger();
        var locals = new Prim[count];
        for (var i = 0; i < count; i++) locals[i] = Type(ref blob);
        return locals;
    }

    private static Prim? TypeOrVoid(ref BlobReader blob)
    {
        if (blob.RemainingBytes > 0 && blob.ReadByte() == 0x01) return null;
        blob.Offset -= 1;
        return Type(ref blob);
    }

    private static Prim Type(ref BlobReader blob)
    {
        var b = blob.ReadByte();
        switch (b)
        {
            case 0x02: return Prim.Bool;
            case 0x03: return Prim.Char;
            case 0x04: return Prim.I1;
            case 0x05: return Prim.U1;
            case 0x06: return Prim.I2;
            case 0x07: return Prim.U2;
            case 0x08: return Prim.I4;
            case 0x09: return Prim.U4;
            case 0x0A: return Prim.I8;
            case 0x0B: return Prim.U8;
            case 0x0C: return Prim.R4;
            case 0x0D: return Prim.R8;
            case 0x1D:
                var element = blob.ReadByte();
                return element switch
                {
                    0x08 => Prim.ArrI4,
                    0x0A => Prim.ArrI8,
                    0x0C => Prim.ArrR4,
                    0x0D => Prim.ArrR8,
                    0x05 => Prim.ArrU1,
                    _ => throw new DeclineException("array of an unsupported element type"),
                };
            default:
                throw new DeclineException("uses a non-primitive type");
        }
    }
}

/// <summary><c>System.Math</c> members with a direct WebAssembly equivalent.</summary>
internal static class Intrinsics
{
    public static string? Lookup(MetadataReader md, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference) return null;
        var mr = md.GetMemberReference((MemberReferenceHandle)handle);
        if (mr.Parent.Kind != HandleKind.TypeReference) return null;
        var type = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
        if (md.GetString(type.Namespace) != "System" || md.GetString(type.Name) != "Math") return null;
        Prim[] parms;
        Prim? result;
        try
        {
            (parms, result) = Signatures.Method(md.GetBlobReader(mr.Signature));
        }
        catch (DeclineException)
        {
            return null;
        }
        var shape = md.GetString(mr.Name) + "(" + string.Join(",", parms) + ")";
        return shape switch
        {
            "Sqrt(R8)" or "Abs(R8)" or "Floor(R8)" or "Ceiling(R8)" or "Truncate(R8)" or "Round(R8)" => shape,
            "Min(R8,R8)" or "Max(R8,R8)" or "Min(I4,I4)" or "Max(I4,I4)" or "Min(I8,I8)" or "Max(I8,I8)" => shape,
            "Abs(I4)" or "Abs(I8)" => shape,
            _ => null,
        };
    }
}

internal static class IlDecoder
{
    public static List<Instr> Decode(byte[] il)
    {
        var list = new List<Instr>();
        var r = new BlobReader();
        var i = 0;
        while (i < il.Length)
        {
            var ins = new Instr { Offset = i };
            ushort op = il[i++];
            if (op == 0xFE)
            {
                op = (ushort)(0xFE00 | il[i++]);
            }
            ins.Op = op;
            switch (op)
            {
                // No operand.
                case <= 0x0D:
                case 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E:
                case 0x25 or 0x26 or 0x2A:
                case >= 0x58 and <= 0x6E:
                case 0x76 or 0x8E:
                case >= 0x90 and <= 0xA2 when op != 0x97 && op != 0x9A && op != 0x9B && op != 0xA2:
                case 0xD1 or 0xD2 or 0xD3 or 0xD4:
                case 0xFE01 or 0xFE02 or 0xFE03 or 0xFE04 or 0xFE05:
                    break;
                case 0x0E or 0x10 or 0x11 or 0x13:
                    ins.I = il[i++];
                    break;
                case 0x1F:
                    ins.I = (sbyte)il[i++];
                    break;
                case 0x20:
                    ins.I = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case 0x21:
                    ins.I = BitConverter.ToInt64(il, i);
                    i += 8;
                    break;
                case 0x22:
                    ins.F = BitConverter.ToSingle(il, i);
                    i += 4;
                    break;
                case 0x23:
                    ins.F = BitConverter.ToDouble(il, i);
                    i += 8;
                    break;
                case 0x28:
                    ins.I = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case >= 0x2B and <= 0x37:
                    var s = (sbyte)il[i++];
                    ins.Targets = new[] { i + s };
                    break;
                case >= 0x38 and <= 0x44:
                    var l = BitConverter.ToInt32(il, i);
                    i += 4;
                    ins.Targets = new[] { i + l };
                    break;
                case 0x45:
                    var n = BitConverter.ToInt32(il, i);
                    i += 4;
                    var end = i + n * 4;
                    var targets = new int[n];
                    for (var k = 0; k < n; k++) targets[k] = end + BitConverter.ToInt32(il, i + k * 4);
                    i = end;
                    ins.Targets = targets;
                    break;
                default:
                    throw new DeclineException($"IL opcode 0x{op:X2}");
            }
            ins.Next = i;
            list.Add(ins);
        }
        _ = r;
        return list;
    }
}
