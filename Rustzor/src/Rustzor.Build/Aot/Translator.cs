namespace Rustzor.Build.Aot;

/// <summary>Translates one method's IL into a WebAssembly function body.</summary>
internal sealed class Translator
{
    private const int FaultIndex = 1;
    private const int FaultDivideByZero = 2;
    private const int FaultOverflow = 3;

    private readonly Unit _unit;
    private readonly MethodModel _m;
    private readonly Dictionary<int, int> _indexOf = new();
    private readonly Dictionary<int, K[]> _stackAt = new();
    private readonly List<WasmType> _locals = new();
    private readonly Dictionary<(int Depth, K Kind), uint> _slots = new();
    private uint _pc;
    private uint _tmpI32;
    private uint _tmpI32b;
    private int _firstLocal;
    private WasmCode _code = new();

    public Translator(Unit unit, MethodModel m)
    {
        _unit = unit;
        _m = m;
    }

    public (List<WasmType>, byte[]) Translate()
    {
        for (var i = 0; i < _m.Code.Count; i++) _indexOf[_m.Code[i].Offset] = i;
        TypeStack();

        // Locals: IL locals, then the block index and scratch, then stack slots.
        _firstLocal = _m.Params.Length;
        foreach (var l in _m.Locals) _locals.Add(Kinds.Wasm(Kinds.Stack(l)));
        _pc = NewLocal(WasmType.I32);
        _tmpI32 = NewLocal(WasmType.I32);
        _tmpI32b = NewLocal(WasmType.I32);

        // Basic blocks start at 0, at every branch target, and after every
        // instruction that does not fall through.
        var leaders = new SortedSet<int> { 0 };
        foreach (var ins in _m.Code)
        {
            foreach (var t in ins.Targets) leaders.Add(t);
            if (ins.Targets.Length > 0 || ins.Op == 0x2A)
                if (ins.Next < _m.Il.Length) leaders.Add(ins.Next);
        }
        var blocks = leaders.ToList();
        var blockOf = new Dictionary<int, int>();
        for (var b = 0; b < blocks.Count; b++) blockOf[blocks[b]] = b;
        var n = blocks.Count;

        _code.BlockVoid(Op.Loop);
        for (var b = n - 1; b >= 0; b--) _code.BlockVoid(Op.Block);
        _code.Get(_pc);
        _code.BrTable(Enumerable.Range(0, n).Select(x => (uint)x).ToList(), 0);
        for (var b = 0; b < n; b++)
        {
            _code.Emit(Op.End); // closes block b: its code follows
            var start = blocks[b];
            var stop = b + 1 < n ? blocks[b + 1] : _m.Il.Length;
            var fallsThrough = true;
            foreach (var ins in _m.Code.Where(i => i.Offset >= start && i.Offset < stop))
            {
                if (!_stackAt.ContainsKey(ins.Offset)) { fallsThrough = false; break; } // unreachable
                fallsThrough = Emit(ins, b, n, blockOf);
            }
            if (fallsThrough && b + 1 < n && stop >= _m.Il.Length)
                throw new DeclineException("falls off the end");
        }
        _code.Emit(Op.End); // loop
        _code.Emit(Op.Unreachable);
        _code.Emit(Op.End); // function
        return (_locals, _code.ToArray());
    }

    private uint NewLocal(WasmType t)
    {
        _locals.Add(t);
        return (uint)(_m.Params.Length + _locals.Count - 1);
    }

    private uint Slot(int depth, K kind)
    {
        if (!_slots.TryGetValue((depth, kind), out var local))
        {
            local = NewLocal(Kinds.Wasm(kind));
            _slots[(depth, kind)] = local;
        }
        return local;
    }

    // ── stack typing ───────────────────────────────────────────────────────

    private void TypeStack()
    {
        var work = new Stack<(int Index, K[] Stack)>();
        work.Push((0, Array.Empty<K>()));
        while (work.Count > 0)
        {
            var (index, entry) = work.Pop();
            if (index >= _m.Code.Count) throw new DeclineException("branch past the end");
            var ins = _m.Code[index];
            if (_stackAt.TryGetValue(ins.Offset, out var known))
            {
                if (!known.SequenceEqual(entry)) throw new DeclineException($"inconsistent stack at IL_{ins.Offset:X4}");
                continue;
            }
            _stackAt[ins.Offset] = entry;
            var after = Effect(ins, entry.ToList()).ToArray();
            foreach (var t in ins.Targets)
            {
                if (!_indexOf.TryGetValue(t, out var ti)) throw new DeclineException("branch into an instruction");
                work.Push((ti, after));
            }
            var fall = ins.Op != 0x2A && ins.Op != 0x2B && ins.Op != 0x38;
            if (fall) work.Push((index + 1, after));
        }
    }

    private static K Pop(List<K> s)
    {
        if (s.Count == 0) throw new DeclineException("stack underflow");
        var k = s[^1];
        s.RemoveAt(s.Count - 1);
        return k;
    }

    private static K Numeric(K a, K b)
    {
        if (a != b || Kinds.IsArray(a)) throw new DeclineException("mixed operand types");
        return a;
    }

    private List<K> Effect(Instr ins, List<K> s)
    {
        var op = ins.Op;
        switch (op)
        {
            case 0x00 or 0x01: break;
            case >= 0x02 and <= 0x05: s.Add(Kinds.Stack(ArgType(op - 0x02))); break;
            case 0x0E: s.Add(Kinds.Stack(ArgType((int)ins.I))); break;
            case >= 0x06 and <= 0x09: s.Add(Kinds.Stack(LocalType(op - 0x06))); break;
            case 0x11: s.Add(Kinds.Stack(LocalType((int)ins.I))); break;
            case >= 0x0A and <= 0x0D: StoreCheck(Pop(s), LocalType(op - 0x0A)); break;
            case 0x13: StoreCheck(Pop(s), LocalType((int)ins.I)); break;
            case 0x10: StoreCheck(Pop(s), ArgType((int)ins.I)); break;
            case >= 0x15 and <= 0x20: s.Add(K.I32); break;
            case 0x21: s.Add(K.I64); break;
            case 0x22 or 0x23: s.Add(K.F64); break;
            case 0x25: { var k = Pop(s); s.Add(k); s.Add(k); break; }
            case 0x26: Pop(s); break;
            case 0x28:
                {
                    var (parms, result) = CallShape((int)ins.I);
                    for (var i = parms.Length - 1; i >= 0; i--)
                        if (Pop(s) != Kinds.Stack(parms[i])) throw new DeclineException("call argument type mismatch");
                    if (result is not null) s.Add(Kinds.Stack(result.Value));
                    break;
                }
            case 0x2A:
                if (_m.Result is not null)
                {
                    if (Pop(s) != Kinds.Stack(_m.Result.Value)) throw new DeclineException("return type mismatch");
                }
                if (s.Count != 0) throw new DeclineException("stack not empty at return");
                break;
            case 0x2B or 0x38: break;
            case 0x2C or 0x2D or 0x39 or 0x3A:
                {
                    var k = Pop(s);
                    if (k != K.I32 && k != K.I64) throw new DeclineException("branch on a non-integer");
                    break;
                }
            case >= 0x2E and <= 0x37:
            case >= 0x3B and <= 0x44:
                Numeric(Pop(s), Pop(s));
                break;
            case 0x45:
                if (Pop(s) != K.I32) throw new DeclineException("switch on a non-int");
                break;
            case 0x58 or 0x59 or 0x5A or 0x5B or 0x5D:
                {
                    var b = Pop(s);
                    var a = Pop(s);
                    var k = Numeric(a, b);
                    if (op == 0x5D && k == K.F64) throw new DeclineException("floating-point remainder");
                    s.Add(k);
                    break;
                }
            case 0x5C or 0x5E or 0x5F or 0x60 or 0x61:
                {
                    var k = Numeric(Pop(s), Pop(s));
                    if (k == K.F64) throw new DeclineException("integer operation on a double");
                    s.Add(k);
                    break;
                }
            case 0x62 or 0x63 or 0x64:
                {
                    if (Pop(s) != K.I32) throw new DeclineException("shift by a non-int");
                    var k = Pop(s);
                    if (k != K.I32 && k != K.I64) throw new DeclineException("shift of a non-integer");
                    s.Add(k);
                    break;
                }
            case 0x65: { var k = Pop(s); if (Kinds.IsArray(k)) throw new DeclineException("neg"); s.Add(k); break; }
            case 0x66: { var k = Pop(s); if (k != K.I32 && k != K.I64) throw new DeclineException("not"); s.Add(k); break; }
            case 0x67 or 0x68 or 0x69 or 0x6D or 0xD1 or 0xD2: ConvFrom(Pop(s)); s.Add(K.I32); break;
            // conv.i / conv.ovf.i: a native int, which on wasm32 is an int32.
            // It only ever feeds an array index here; anything else declines
            // at the type check of whatever consumes it.
            case 0xD3 or 0xD4:
                {
                    var k = Pop(s);
                    if (k != K.I32 && k != K.I64) throw new DeclineException("native int from a non-integer");
                    s.Add(K.I32);
                    break;
                }
            case 0x6A or 0x6E: ConvFrom(Pop(s)); s.Add(K.I64); break;
            case 0x6B or 0x6C or 0x76: ConvFrom(Pop(s)); s.Add(K.F64); break;
            case 0x8E:
                if (!Kinds.IsArray(Pop(s))) throw new DeclineException("ldlen on a non-array");
                s.Add(K.I32);
                break;
            case >= 0x90 and <= 0x99:
                {
                    if (Pop(s) != K.I32) throw new DeclineException("array index is not an int");
                    var array = Pop(s);
                    var (kind, _, _) = Element(op);
                    if (array != kind) throw new DeclineException("array element type mismatch");
                    s.Add(ElementStack(kind));
                    break;
                }
            case >= 0x9C and <= 0xA1:
                {
                    var value = Pop(s);
                    if (Pop(s) != K.I32) throw new DeclineException("array index is not an int");
                    var array = Pop(s);
                    var (kind, _, _) = Element(op);
                    if (array != kind || value != ElementStack(kind)) throw new DeclineException("array element type mismatch");
                    break;
                }
            case 0xFE01 or 0xFE02 or 0xFE03 or 0xFE04 or 0xFE05:
                Numeric(Pop(s), Pop(s));
                s.Add(K.I32);
                break;
            default:
                throw new DeclineException($"IL opcode 0x{op:X2}");
        }
        return s;
    }

    private static void ConvFrom(K k)
    {
        if (Kinds.IsArray(k)) throw new DeclineException("conversion of an array");
    }

    private static void StoreCheck(K value, Prim target)
    {
        if (value != Kinds.Stack(target)) throw new DeclineException("store of a mismatched type");
    }

    private static K ElementStack(K array) => array switch
    {
        K.ArrI8 => K.I64,
        K.ArrR4 or K.ArrR8 => K.F64,
        _ => K.I32,
    };

    /// <summary>Array kind, element size, and whether the element is signed, by ldelem/stelem opcode.</summary>
    private static (K Kind, int Size, bool Signed) Element(ushort op) => op switch
    {
        0x91 or 0x9C => (K.ArrU1, 1, false), // ldelem.u1 / stelem.i1 (byte[])
        0x94 or 0x95 or 0x9E => (K.ArrI4, 4, true),
        0x96 or 0x9F => (K.ArrI8, 8, true),
        0x98 or 0xA0 => (K.ArrR4, 4, true),
        0x99 or 0xA1 => (K.ArrR8, 8, true),
        _ => throw new DeclineException($"array access opcode 0x{op:X2}"),
    };

    private Prim ArgType(int i) => i < _m.Params.Length ? _m.Params[i] : throw new DeclineException("argument index");

    private Prim LocalType(int i) => i < _m.Locals.Length ? _m.Locals[i] : throw new DeclineException("local index");

    private (Prim[] Params, Prim? Result) CallShape(int token)
    {
        if ((token >> 24) == 0x06)
        {
            if (!_unit.ByToken.TryGetValue(token, out var callee) || callee.Decline is not null)
                throw new DeclineException("calls a method that is not compiled");
            return (callee.Params, callee.Result);
        }
        return Intrinsics.Lookup(_unit.Md, token) switch
        {
            "Sqrt(R8)" or "Abs(R8)" or "Floor(R8)" or "Ceiling(R8)" or "Truncate(R8)" or "Round(R8)" => (new[] { Prim.R8 }, Prim.R8),
            "Min(R8,R8)" or "Max(R8,R8)" => (new[] { Prim.R8, Prim.R8 }, Prim.R8),
            "Min(I4,I4)" or "Max(I4,I4)" => (new[] { Prim.I4, Prim.I4 }, Prim.I4),
            "Min(I8,I8)" or "Max(I8,I8)" => (new[] { Prim.I8, Prim.I8 }, Prim.I8),
            "Abs(I4)" => (new[] { Prim.I4 }, Prim.I4),
            "Abs(I8)" => (new[] { Prim.I8 }, Prim.I8),
            _ => throw new DeclineException("unsupported call"),
        };
    }

    // ── emission ───────────────────────────────────────────────────────────

    private uint ArgLocal(int i) => (uint)i;

    private uint IlLocal(int i) => (uint)(_firstLocal + i);

    /// <summary>Emits one instruction. Returns whether control falls through.</summary>
    private bool Emit(Instr ins, int block, int blocks, Dictionary<int, int> blockOf)
    {
        var s = _stackAt[ins.Offset];
        var d = s.Length;
        uint Top(int fromTop) => Slot(d - 1 - fromTop, s[d - 1 - fromTop]);
        var op = ins.Op;
        var c = _code;

        void Push(K k) => c.Set(Slot(d, k));
        void Branch(int target)
        {
            var j = blockOf[target];
            if (j > block) c.Emit(Op.Br, (uint)(j - block - 1));
            else c.I32(j).Set(_pc).Emit(Op.Br, (uint)(blocks - 1 - block));
        }

        switch (op)
        {
            case 0x00 or 0x01:
                return true;
            case >= 0x02 and <= 0x05:
                c.Get(ArgLocal(op - 0x02)); Push(s.Length > 0 ? Kinds.Stack(ArgType(op - 0x02)) : Kinds.Stack(ArgType(op - 0x02)));
                return true;
            case 0x0E:
                c.Get(ArgLocal((int)ins.I)); Push(Kinds.Stack(ArgType((int)ins.I)));
                return true;
            case >= 0x06 and <= 0x09:
                c.Get(IlLocal(op - 0x06)); Push(Kinds.Stack(LocalType(op - 0x06)));
                return true;
            case 0x11:
                c.Get(IlLocal((int)ins.I)); Push(Kinds.Stack(LocalType((int)ins.I)));
                return true;
            case >= 0x0A and <= 0x0D:
                c.Get(Top(0)); Narrow(LocalType(op - 0x0A)); c.Set(IlLocal(op - 0x0A));
                return true;
            case 0x13:
                c.Get(Top(0)); Narrow(LocalType((int)ins.I)); c.Set(IlLocal((int)ins.I));
                return true;
            case 0x10:
                c.Get(Top(0)); Narrow(ArgType((int)ins.I)); c.Set(ArgLocal((int)ins.I));
                return true;
            case 0x15: c.I32(-1); Push(K.I32); return true;
            case >= 0x16 and <= 0x1E: c.I32(op - 0x16); Push(K.I32); return true;
            case 0x1F or 0x20: c.I32((int)ins.I); Push(K.I32); return true;
            case 0x21: c.I64(ins.I); Push(K.I64); return true;
            case 0x22: c.F64((float)ins.F); Push(K.F64); return true;
            case 0x23: c.F64(ins.F); Push(K.F64); return true;
            case 0x25: c.Get(Top(0)); c.Set(Slot(d, s[d - 1])); return true;
            case 0x26: return true;
            case 0x28: EmitCall((int)ins.I, s); return true;
            case 0x2A:
                if (_m.Result is not null)
                {
                    c.Get(Top(0));
                    Narrow(_m.Result.Value);
                }
                c.Emit(Op.Return);
                return false;
            case 0x2B or 0x38:
                Branch(ins.Targets[0]);
                return false;
            case 0x2C or 0x39 or 0x2D or 0x3A:
                {
                    c.Get(Top(0));
                    if (s[d - 1] == K.I64) c.Emit(Op.I64Eqz).Emit(Op.I32Eqz); // to i32 truth
                    if (op is 0x2C or 0x39) c.Emit(Op.I32Eqz);
                    c.BlockVoid(Op.If);
                    // Inside `if`, every label is one deeper.
                    BranchNested(ins.Targets[0], block, blocks, blockOf);
                    c.Emit(Op.End);
                    return true;
                }
            case >= 0x2E and <= 0x37:
            case >= 0x3B and <= 0x44:
                {
                    var kind = s[d - 1];
                    c.Get(Top(1)).Get(Top(0));
                    var cond = op >= 0x3B ? op - 0x3B : op - 0x2E; // beq bge bgt ble blt bne.un bge.un bgt.un ble.un blt.un
                    Compare(kind, cond);
                    c.BlockVoid(Op.If);
                    BranchNested(ins.Targets[0], block, blocks, blockOf);
                    c.Emit(Op.End);
                    return true;
                }
            case 0x45:
                {
                    // switch: one `if` per case; out of range falls through.
                    for (var k = 0; k < ins.Targets.Length; k++)
                    {
                        c.Get(Top(0)).I32(k).Emit(Op.I32Eq);
                        c.BlockVoid(Op.If);
                        BranchNested(ins.Targets[k], block, blocks, blockOf);
                        c.Emit(Op.End);
                    }
                    return true;
                }
            case 0x58 or 0x59 or 0x5A:
                {
                    var k = s[d - 1];
                    c.Get(Top(1)).Get(Top(0));
                    c.Emit(k switch
                    {
                        K.I32 => op == 0x58 ? Op.I32Add : op == 0x59 ? Op.I32Sub : Op.I32Mul,
                        K.I64 => op == 0x58 ? Op.I64Add : op == 0x59 ? Op.I64Sub : Op.I64Mul,
                        _ => op == 0x58 ? Op.F64Add : op == 0x59 ? Op.F64Sub : Op.F64Mul,
                    });
                    c.Set(Slot(d - 2, k));
                    return true;
                }
            case 0x5B or 0x5C or 0x5D or 0x5E:
                {
                    var k = s[d - 1];
                    if (k == K.F64)
                    {
                        c.Get(Top(1)).Get(Top(0)).Emit(Op.F64Div).Set(Slot(d - 2, k));
                        return true;
                    }
                    var signed = op is 0x5B or 0x5D;
                    var rem = op is 0x5D or 0x5E;
                    // Divisor zero: DivideByZeroException.
                    c.Get(Top(0));
                    if (k == K.I64) c.Emit(Op.I64Eqz); else c.Emit(Op.I32Eqz);
                    c.BlockVoid(Op.If);
                    Fault(FaultDivideByZero);
                    c.Emit(Op.End);
                    if (signed)
                    {
                        // MinValue / -1 overflows; wasm traps where .NET throws.
                        c.Get(Top(0));
                        if (k == K.I64) c.I64(-1).Emit(Op.I64Eq); else c.I32(-1).Emit(Op.I32Eq);
                        c.Get(Top(1));
                        if (k == K.I64) c.I64(long.MinValue).Emit(Op.I64Eq); else c.I32(int.MinValue).Emit(Op.I32Eq);
                        c.Emit(Op.I32And);
                        c.BlockVoid(Op.If);
                        Fault(FaultOverflow);
                        c.Emit(Op.End);
                    }
                    c.Get(Top(1)).Get(Top(0));
                    c.Emit(k == K.I64
                        ? (rem ? (signed ? Op.I64RemS : Op.I64RemU) : (signed ? Op.I64DivS : Op.I64DivU))
                        : (rem ? (signed ? Op.I32RemS : Op.I32RemU) : (signed ? Op.I32DivS : Op.I32DivU)));
                    c.Set(Slot(d - 2, k));
                    return true;
                }
            case 0x5F or 0x60 or 0x61:
                {
                    var k = s[d - 1];
                    c.Get(Top(1)).Get(Top(0));
                    c.Emit(k == K.I64
                        ? (op == 0x5F ? Op.I64And : op == 0x60 ? Op.I64Or : Op.I64Xor)
                        : (op == 0x5F ? Op.I32And : op == 0x60 ? Op.I32Or : Op.I32Xor));
                    c.Set(Slot(d - 2, k));
                    return true;
                }
            case 0x62 or 0x63 or 0x64:
                {
                    var k = s[d - 2];
                    c.Get(Top(1)).Get(Top(0));
                    if (k == K.I64) c.Emit(Op.I64ExtendI32U);
                    c.Emit(k == K.I64
                        ? (op == 0x62 ? Op.I64Shl : op == 0x63 ? Op.I64ShrS : Op.I64ShrU)
                        : (op == 0x62 ? Op.I32Shl : op == 0x63 ? Op.I32ShrS : Op.I32ShrU));
                    c.Set(Slot(d - 2, k));
                    return true;
                }
            case 0x65:
                {
                    var k = s[d - 1];
                    if (k == K.F64) c.Get(Top(0)).Emit(Op.F64Neg);
                    else if (k == K.I64) c.I64(0).Get(Top(0)).Emit(Op.I64Sub);
                    else c.I32(0).Get(Top(0)).Emit(Op.I32Sub);
                    c.Set(Slot(d - 1, k));
                    return true;
                }
            case 0x66:
                {
                    var k = s[d - 1];
                    c.Get(Top(0));
                    if (k == K.I64) c.I64(-1).Emit(Op.I64Xor); else c.I32(-1).Emit(Op.I32Xor);
                    c.Set(Slot(d - 1, k));
                    return true;
                }
            case 0x67 or 0x68 or 0x69 or 0x6D or 0xD1 or 0xD2:
                {
                    ToI32(s[d - 1], Top(0), op == 0x6D);
                    switch (op)
                    {
                        case 0x67: c.Emit(Op.I32Extend8S); break;
                        case 0x68: c.Emit(Op.I32Extend16S); break;
                        case 0xD2: c.I32(0xFF).Emit(Op.I32And); break;
                        case 0xD1: c.I32(0xFFFF).Emit(Op.I32And); break;
                    }
                    c.Set(Slot(d - 1, K.I32));
                    return true;
                }
            case 0xD3 or 0xD4:
                {
                    var k = s[d - 1];
                    if (k == K.I64)
                    {
                        if (op == 0xD4)
                        {
                            // Checked: the long must fit an int32 index.
                            c.Get(Top(0)).Get(Top(0)).Emit(Op.I32WrapI64).Emit(Op.I64ExtendI32S).Emit(Op.I64Ne);
                            c.BlockVoid(Op.If);
                            Fault(FaultOverflow);
                            c.Emit(Op.End);
                        }
                        c.Get(Top(0)).Emit(Op.I32WrapI64).Set(Slot(d - 1, K.I32));
                    }
                    return true;
                }
            case 0x6A or 0x6E:
                {
                    var k = s[d - 1];
                    c.Get(Top(0));
                    if (k == K.I32) c.Emit(op == 0x6A ? Op.I64ExtendI32S : Op.I64ExtendI32U);
                    else if (k == K.F64) c.Sat(op == 0x6A ? Op.I64TruncSatF64S : Op.I64TruncSatF64U);
                    c.Set(Slot(d - 1, K.I64));
                    return true;
                }
            case 0x6B or 0x6C or 0x76:
                {
                    var k = s[d - 1];
                    c.Get(Top(0));
                    var unsigned = op == 0x76;
                    if (k == K.I32) c.Emit(unsigned ? Op.F64ConvertI32U : Op.F64ConvertI32S);
                    else if (k == K.I64) c.Emit(unsigned ? Op.F64ConvertI64U : Op.F64ConvertI64S);
                    if (op == 0x6B) c.Emit(Op.F32DemoteF64).Emit(Op.F64PromoteF32); // round to float
                    c.Set(Slot(d - 1, K.F64));
                    return true;
                }
            case 0x8E:
                c.Get(Top(0)).Mem(Op.I32Load, 2, 0);
                c.Set(Slot(d - 1, K.I32));
                return true;
            case >= 0x90 and <= 0x99:
                {
                    var (kind, size, _) = Element(op);
                    BoundsAndAddress(Top(1), Top(0), size);
                    switch (kind)
                    {
                        case K.ArrU1: c.Mem(Op.I32Load8U, 0, 8); break;
                        case K.ArrI4: c.Mem(Op.I32Load, 2, 8); break;
                        case K.ArrI8: c.Mem(Op.I64Load, 3, 8); break;
                        case K.ArrR4: c.Mem(Op.F32Load, 2, 8).Emit(Op.F64PromoteF32); break;
                        default: c.Mem(Op.F64Load, 3, 8); break;
                    }
                    c.Set(Slot(d - 2, ElementStack(kind)));
                    return true;
                }
            case >= 0x9C and <= 0xA1:
                {
                    var (kind, size, _) = Element(op);
                    BoundsAndAddress(Top(2), Top(1), size);
                    c.Get(Top(0));
                    switch (kind)
                    {
                        case K.ArrU1: c.Mem(Op.I32Store8, 0, 8); break;
                        case K.ArrI4: c.Mem(Op.I32Store, 2, 8); break;
                        case K.ArrI8: c.Mem(Op.I64Store, 3, 8); break;
                        case K.ArrR4: c.Emit(Op.F32DemoteF64).Mem(Op.F32Store, 2, 8); break;
                        default: c.Mem(Op.F64Store, 3, 8); break;
                    }
                    return true;
                }
            case 0xFE01 or 0xFE02 or 0xFE03 or 0xFE04 or 0xFE05:
                {
                    var kind = s[d - 1];
                    c.Get(Top(1)).Get(Top(0));
                    // ceq cgt cgt.un clt clt.un, in the beq.. condition numbering.
                    Compare(kind, op switch { 0xFE01 => 0, 0xFE02 => 2, 0xFE03 => 7, 0xFE04 => 4, _ => 9 });
                    c.Set(Slot(d - 2, K.I32));
                    return true;
                }
        }
        throw new DeclineException($"IL opcode 0x{op:X2}");
    }

    private void BranchNested(int target, int block, int blocks, Dictionary<int, int> blockOf)
    {
        var j = blockOf[target];
        if (j > block) _code.Emit(Op.Br, (uint)(j - block));
        else _code.I32(j).Set(_pc).Emit(Op.Br, (uint)(blocks - block));
    }

    /// <summary>
    /// Pushes an i32 condition for two operands already on the operand stack.
    /// <paramref name="cond"/>: 0 eq, 1 ge, 2 gt, 3 le, 4 lt, 5 ne.un, 6 ge.un,
    /// 7 gt.un, 8 le.un, 9 lt.un.
    /// </summary>
    private void Compare(K kind, int cond)
    {
        var c = _code;
        if (kind == K.F64)
        {
            switch (cond)
            {
                case 0: c.Emit(Op.F64Eq); break;
                case 1: c.Emit(Op.F64Ge); break;
                case 2: c.Emit(Op.F64Gt); break;
                case 3: c.Emit(Op.F64Le); break;
                case 4: c.Emit(Op.F64Lt); break;
                case 5: c.Emit(Op.F64Ne); break;
                // Unordered forms are true when either side is NaN.
                case 6: c.Emit(Op.F64Lt).Emit(Op.I32Eqz); break;
                case 7: c.Emit(Op.F64Le).Emit(Op.I32Eqz); break;
                case 8: c.Emit(Op.F64Gt).Emit(Op.I32Eqz); break;
                default: c.Emit(Op.F64Ge).Emit(Op.I32Eqz); break;
            }
            return;
        }
        var wide = kind == K.I64;
        c.Emit(cond switch
        {
            0 => wide ? Op.I64Eq : Op.I32Eq,
            1 => wide ? Op.I64GeS : Op.I32GeS,
            2 => wide ? Op.I64GtS : Op.I32GtS,
            3 => wide ? Op.I64LeS : Op.I32LeS,
            4 => wide ? Op.I64LtS : Op.I32LtS,
            5 => wide ? Op.I64Ne : Op.I32Ne,
            6 => wide ? Op.I64GeU : Op.I32GeU,
            7 => wide ? Op.I64GtU : Op.I32GtU,
            8 => wide ? Op.I64LeU : Op.I32LeU,
            _ => wide ? Op.I64LtU : Op.I32LtU,
        });
    }

    private void ToI32(K k, uint local, bool unsigned)
    {
        _code.Get(local);
        if (k == K.I64) _code.Emit(Op.I32WrapI64);
        else if (k == K.F64) _code.Sat(unsigned ? Op.I32TruncSatF64U : Op.I32TruncSatF64S);
    }

    /// <summary>Checks the index and leaves <c>array + index * size</c> on the stack.</summary>
    private void BoundsAndAddress(uint array, uint index, int size)
    {
        var c = _code;
        c.Get(index).Get(array).Mem(Op.I32Load, 2, 0).Emit(Op.I32GeU);
        c.BlockVoid(Op.If);
        Fault(FaultIndex);
        c.Emit(Op.End);
        c.Get(array).Get(index).I32(size).Emit(Op.I32Mul).Emit(Op.I32Add);
    }

    private void Fault(int code)
    {
        _code.I32(0).I32(code).Mem(Op.I32Store, 2, 0);
        AotCompiler.EmitDefault(_code, _m.Result);
        _code.Emit(Op.Return);
    }

    /// <summary>What storing into a narrow local does to an int32 on the stack.</summary>
    private void Narrow(Prim target)
    {
        switch (target)
        {
            case Prim.I1: _code.Emit(Op.I32Extend8S); break;
            case Prim.U1: _code.I32(0xFF).Emit(Op.I32And); break;
            case Prim.I2: _code.Emit(Op.I32Extend16S); break;
            case Prim.U2 or Prim.Char: _code.I32(0xFFFF).Emit(Op.I32And); break;
            case Prim.R4: _code.Emit(Op.F32DemoteF64).Emit(Op.F64PromoteF32); break;
        }
    }

    private void EmitCall(int token, K[] s)
    {
        var c = _code;
        var (parms, result) = CallShape(token);
        var d = s.Length;
        var argBase = d - parms.Length;
        for (var i = 0; i < parms.Length; i++) c.Get(Slot(argBase + i, s[argBase + i]));
        if ((token >> 24) == 0x06)
        {
            var callee = _unit.ByToken[token];
            c.Emit(Op.Call, (uint)callee.Function);
            if (result is not null) c.Set(Slot(argBase, Kinds.Stack(result.Value)));
            // A fault in the callee: unwind with it.
            c.I32(0).Mem(Op.I32Load, 2, 0);
            c.BlockVoid(Op.If);
            AotCompiler.EmitDefault(c, _m.Result);
            c.Emit(Op.Return);
            c.Emit(Op.End);
            return;
        }
        switch (Intrinsics.Lookup(_unit.Md, token))
        {
            case "Sqrt(R8)": c.Emit(Op.F64Sqrt); break;
            case "Abs(R8)": c.Emit(Op.F64Abs); break;
            case "Floor(R8)": c.Emit(Op.F64Floor); break;
            case "Ceiling(R8)": c.Emit(Op.F64Ceil); break;
            case "Truncate(R8)": c.Emit(Op.F64Trunc); break;
            case "Round(R8)": c.Emit(Op.F64Nearest); break;
            case "Min(R8,R8)": c.Emit(Op.F64Min); break;
            case "Max(R8,R8)": c.Emit(Op.F64Max); break;
            case "Min(I4,I4)" or "Max(I4,I4)":
                {
                    var max = Intrinsics.Lookup(_unit.Md, token)!.StartsWith("Max");
                    c.Set(_tmpI32b).Set(_tmpI32);
                    c.Get(_tmpI32).Get(_tmpI32b).Get(_tmpI32).Get(_tmpI32b).Emit(max ? Op.I32GtS : Op.I32LtS).Emit(Op.Select);
                    break;
                }
            case "Abs(I4)":
                c.Set(_tmpI32);
                c.I32(0).Get(_tmpI32).Emit(Op.I32Sub).Get(_tmpI32).Get(_tmpI32).I32(0).Emit(Op.I32LtS).Emit(Op.Select);
                break;
            default:
                // Min/Max/Abs on long: rare in kernels; declined rather than emitted.
                throw new DeclineException("unsupported Math overload");
        }
        if (result is not null) c.Set(Slot(argBase, Kinds.Stack(result.Value)));
    }
}
