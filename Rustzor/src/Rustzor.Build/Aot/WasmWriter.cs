namespace Rustzor.Build.Aot;

/// <summary>WebAssembly value types.</summary>
public enum WasmType : byte
{
    I32 = 0x7F,
    I64 = 0x7E,
    F32 = 0x7D,
    F64 = 0x7C,
}

/// <summary>Opcodes the AOT compiler emits.</summary>
public static class Op
{
    public const byte Unreachable = 0x00, Nop = 0x01, Block = 0x02, Loop = 0x03, If = 0x04, Else = 0x05, End = 0x0B;
    public const byte Br = 0x0C, BrIf = 0x0D, BrTable = 0x0E, Return = 0x0F, Call = 0x10;
    public const byte Drop = 0x1A, Select = 0x1B;
    public const byte LocalGet = 0x20, LocalSet = 0x21, LocalTee = 0x22;
    public const byte I32Load = 0x28, I64Load = 0x29, F32Load = 0x2A, F64Load = 0x2B;
    public const byte I32Load8S = 0x2C, I32Load8U = 0x2D, I32Load16S = 0x2E, I32Load16U = 0x2F;
    public const byte I32Store = 0x36, I64Store = 0x37, F32Store = 0x38, F64Store = 0x39, I32Store8 = 0x3A, I32Store16 = 0x3B;
    public const byte I32Const = 0x41, I64Const = 0x42, F32Const = 0x43, F64Const = 0x44;
    public const byte I32Eqz = 0x45, I32Eq = 0x46, I32Ne = 0x47, I32LtS = 0x48, I32LtU = 0x49, I32GtS = 0x4A, I32GtU = 0x4B, I32LeS = 0x4C, I32LeU = 0x4D, I32GeS = 0x4E, I32GeU = 0x4F;
    public const byte I64Eqz = 0x50, I64Eq = 0x51, I64Ne = 0x52, I64LtS = 0x53, I64LtU = 0x54, I64GtS = 0x55, I64GtU = 0x56, I64LeS = 0x57, I64LeU = 0x58, I64GeS = 0x59, I64GeU = 0x5A;
    public const byte F64Eq = 0x61, F64Ne = 0x62, F64Lt = 0x63, F64Gt = 0x64, F64Le = 0x65, F64Ge = 0x66;
    public const byte I32Add = 0x6A, I32Sub = 0x6B, I32Mul = 0x6C, I32DivS = 0x6D, I32DivU = 0x6E, I32RemS = 0x6F, I32RemU = 0x70;
    public const byte I32And = 0x71, I32Or = 0x72, I32Xor = 0x73, I32Shl = 0x74, I32ShrS = 0x75, I32ShrU = 0x76;
    public const byte I64Add = 0x7C, I64Sub = 0x7D, I64Mul = 0x7E, I64DivS = 0x7F, I64DivU = 0x80, I64RemS = 0x81, I64RemU = 0x82;
    public const byte I64And = 0x83, I64Or = 0x84, I64Xor = 0x85, I64Shl = 0x86, I64ShrS = 0x87, I64ShrU = 0x88;
    public const byte F64Abs = 0x99, F64Neg = 0x9A, F64Ceil = 0x9B, F64Floor = 0x9C, F64Trunc = 0x9D, F64Nearest = 0x9E, F64Sqrt = 0x9F;
    public const byte F64Add = 0xA0, F64Sub = 0xA1, F64Mul = 0xA2, F64Div = 0xA3, F64Min = 0xA4, F64Max = 0xA5;
    public const byte I32WrapI64 = 0xA7, I64ExtendI32S = 0xAC, I64ExtendI32U = 0xAD;
    public const byte F32DemoteF64 = 0xB6, F64ConvertI32S = 0xB7, F64ConvertI32U = 0xB8, F64ConvertI64S = 0xB9, F64ConvertI64U = 0xBA, F64PromoteF32 = 0xBB;
    public const byte I32ReinterpretF32 = 0xBC, I64ReinterpretF64 = 0xBD, F32ReinterpretI32 = 0xBE, F64ReinterpretI64 = 0xBF;
    public const byte I32Extend8S = 0xC0, I32Extend16S = 0xC1;
    public const byte Prefix = 0xFC;
    // 0xFC sub-opcodes: saturating truncation.
    public const byte I32TruncSatF64S = 2, I32TruncSatF64U = 3, I64TruncSatF64S = 6, I64TruncSatF64U = 7;
}

/// <summary>A function body under construction.</summary>
public sealed class WasmCode
{
    private readonly List<byte> _bytes = new();

    public WasmCode Emit(byte b) { _bytes.Add(b); return this; }

    public WasmCode Emit(byte op, uint immediate) { _bytes.Add(op); Leb(immediate); return this; }

    public WasmCode I32(int v) { _bytes.Add(Op.I32Const); SLeb(v); return this; }

    public WasmCode I64(long v) { _bytes.Add(Op.I64Const); SLeb(v); return this; }

    public WasmCode F64(double v) { _bytes.Add(Op.F64Const); _bytes.AddRange(BitConverter.GetBytes(v)); return this; }

    public WasmCode Get(uint local) => Emit(Op.LocalGet, local);

    public WasmCode Set(uint local) => Emit(Op.LocalSet, local);

    /// <summary>A memory access: alignment (log2) and offset.</summary>
    public WasmCode Mem(byte op, uint align, uint offset)
    {
        _bytes.Add(op);
        Leb(align);
        Leb(offset);
        return this;
    }

    public WasmCode Sat(byte sub)
    {
        _bytes.Add(Op.Prefix);
        Leb(sub);
        return this;
    }

    public WasmCode BlockVoid(byte op) { _bytes.Add(op); _bytes.Add(0x40); return this; }

    public WasmCode BrTable(IReadOnlyList<uint> targets, uint fallback)
    {
        _bytes.Add(Op.BrTable);
        Leb((uint)targets.Count);
        foreach (var t in targets) Leb(t);
        Leb(fallback);
        return this;
    }

    public void Leb(uint v)
    {
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            _bytes.Add(b);
        } while (v != 0);
    }

    public void SLeb(long v)
    {
        var more = true;
        while (more)
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if ((v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0)) more = false;
            else b |= 0x80;
            _bytes.Add(b);
        }
    }

    public byte[] ToArray() => _bytes.ToArray();
    public int Length => _bytes.Count;
}

/// <summary>Writes a WebAssembly module: types, functions, one memory, exports.</summary>
public sealed class WasmModule
{
    private readonly List<(WasmType[] Params, WasmType[] Results)> _types = new();
    private readonly List<(int Type, List<WasmType> Locals, byte[] Body)> _functions = new();
    private readonly List<(string Name, byte Kind, uint Index)> _exports = new();

    public int TypeOf(WasmType[] parameters, WasmType[] results)
    {
        for (var i = 0; i < _types.Count; i++)
            if (_types[i].Params.SequenceEqual(parameters) && _types[i].Results.SequenceEqual(results)) return i;
        _types.Add((parameters, results));
        return _types.Count - 1;
    }

    /// <summary>Reserves a function index before its body is known, for calls.</summary>
    public int Declare(int type)
    {
        _functions.Add((type, new List<WasmType>(), Array.Empty<byte>()));
        return _functions.Count - 1;
    }

    public void Define(int index, List<WasmType> locals, byte[] body)
    {
        var f = _functions[index];
        _functions[index] = (f.Type, locals, body);
    }

    public void ExportFunction(string name, int index) => _exports.Add((name, 0x00, (uint)index));

    public byte[] ToBytes()
    {
        var w = new WasmCode();
        var output = new List<byte> { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 };

        void Section(byte id, WasmCode content)
        {
            output.Add(id);
            var body = content.ToArray();
            var len = new WasmCode();
            len.Leb((uint)body.Length);
            output.AddRange(len.ToArray());
            output.AddRange(body);
        }

        var types = new WasmCode();
        types.Leb((uint)_types.Count);
        foreach (var (p, r) in _types)
        {
            types.Emit(0x60);
            types.Leb((uint)p.Length);
            foreach (var t in p) types.Emit((byte)t);
            types.Leb((uint)r.Length);
            foreach (var t in r) types.Emit((byte)t);
        }
        Section(1, types);

        var funcs = new WasmCode();
        funcs.Leb((uint)_functions.Count);
        foreach (var f in _functions) funcs.Leb((uint)f.Type);
        Section(3, funcs);

        // One memory, at least one page; the runtime grows it for arrays.
        var memory = new WasmCode();
        memory.Leb(1);
        memory.Emit(0x00);
        memory.Leb(1);
        Section(5, memory);

        var exports = new WasmCode();
        exports.Leb((uint)(_exports.Count + 1));
        foreach (var (name, kind, index) in _exports.Prepend(("memory", (byte)0x02, 0u)))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            exports.Leb((uint)bytes.Length);
            foreach (var b in bytes) exports.Emit(b);
            exports.Emit(kind);
            exports.Leb(index);
        }
        Section(7, exports);

        var code = new WasmCode();
        code.Leb((uint)_functions.Count);
        foreach (var f in _functions)
        {
            var fn = new WasmCode();
            // Locals, run-length encoded by type.
            var runs = new List<(uint Count, WasmType Type)>();
            foreach (var t in f.Locals)
            {
                if (runs.Count > 0 && runs[^1].Type == t) runs[^1] = (runs[^1].Count + 1, t);
                else runs.Add((1, t));
            }
            fn.Leb((uint)runs.Count);
            foreach (var (count, type) in runs)
            {
                fn.Leb(count);
                fn.Emit((byte)type);
            }
            foreach (var b in f.Body) fn.Emit(b);
            code.Leb((uint)fn.Length);
            foreach (var b in fn.ToArray()) code.Emit(b);
        }
        Section(10, code);
        _ = w;
        return output.ToArray();
    }
}
