namespace RustNet.MetadataProcessor;

internal enum OperandKind
{
    None,
    ByteVar,      // 1 byte (short var index / short i4 / unaligned arg)
    ShortBranch,  // 1 byte signed
    Var2,         // 2 bytes
    Int32,        // 4 bytes literal
    Int64,        // 8 bytes literal
    Float32,      // 4 bytes
    Float64,      // 8 bytes
    Branch32,     // 4 bytes signed
    Switch,       // 4-byte count + n * 4
    TokenMethod,
    TokenField,
    TokenType,
    TokenString,
    TokenAny,     // ldtoken
    TokenSig,     // calli
}

internal static class OpcodeTable
{
    /// <summary>Operand kind for a 1-byte opcode; null = unknown/unsupported.</summary>
    public static OperandKind? Lookup(byte op) => op switch
    {
        0x00 or 0x01 => OperandKind.None,
        >= 0x02 and <= 0x0D => OperandKind.None,
        >= 0x0E and <= 0x13 => OperandKind.ByteVar,
        0x14 => OperandKind.None,
        >= 0x15 and <= 0x1E => OperandKind.None,
        0x1F => OperandKind.ByteVar,
        0x20 => OperandKind.Int32,
        0x21 => OperandKind.Int64,
        0x22 => OperandKind.Float32,
        0x23 => OperandKind.Float64,
        0x25 or 0x26 => OperandKind.None,
        0x27 => OperandKind.TokenMethod, // jmp
        0x28 => OperandKind.TokenMethod, // call
        0x29 => OperandKind.TokenSig,    // calli
        0x2A => OperandKind.None,        // ret
        >= 0x2B and <= 0x37 => OperandKind.ShortBranch,
        >= 0x38 and <= 0x44 => OperandKind.Branch32,
        0x45 => OperandKind.Switch,
        >= 0x46 and <= 0x57 => OperandKind.None, // ldind/stind
        >= 0x58 and <= 0x66 => OperandKind.None, // arith
        >= 0x67 and <= 0x6E => OperandKind.None, // conv
        0x6F => OperandKind.TokenMethod, // callvirt
        0x70 or 0x71 => OperandKind.TokenType, // cpobj/ldobj
        0x72 => OperandKind.TokenString, // ldstr
        0x73 => OperandKind.TokenMethod, // newobj
        0x74 or 0x75 => OperandKind.TokenType, // castclass/isinst
        0x76 => OperandKind.None,
        0x79 => OperandKind.TokenType, // unbox
        0x7A => OperandKind.None,      // throw
        >= 0x7B and <= 0x80 => OperandKind.TokenField,
        0x81 => OperandKind.TokenType, // stobj
        >= 0x82 and <= 0x8A => OperandKind.None, // conv.ovf.*.un
        0x8C => OperandKind.TokenType, // box
        0x8D => OperandKind.TokenType, // newarr
        0x8E => OperandKind.None,      // ldlen
        0x8F => OperandKind.TokenType, // ldelema
        >= 0x90 and <= 0xA2 => OperandKind.None, // ldelem/stelem typed
        0xA3 or 0xA4 => OperandKind.TokenType,   // ldelem/stelem generic
        0xA5 => OperandKind.TokenType, // unbox.any
        >= 0xB3 and <= 0xBA => OperandKind.None, // conv.ovf
        0xC3 => OperandKind.None,      // ckfinite
        0xD0 => OperandKind.TokenAny,  // ldtoken
        >= 0xD1 and <= 0xD5 => OperandKind.None,
        >= 0xD6 and <= 0xDB => OperandKind.None, // arith.ovf
        0xDC => OperandKind.None,      // endfinally
        0xDD => OperandKind.Branch32,  // leave
        0xDE => OperandKind.ShortBranch, // leave.s
        0xDF => OperandKind.None,      // stind.i
        0xE0 => OperandKind.None,      // conv.u
        _ => null,
    };

    /// <summary>Operand kind for an 0xFE-prefixed opcode.</summary>
    public static OperandKind? LookupPrefixed(byte op2) => op2 switch
    {
        >= 0x01 and <= 0x05 => OperandKind.None, // ceq/cgt/clt
        0x06 or 0x07 => OperandKind.TokenMethod, // ldftn/ldvirtftn
        >= 0x09 and <= 0x0E => OperandKind.Var2,
        0x0F => OperandKind.None,  // localloc
        0x11 => OperandKind.None,  // endfilter
        0x12 => OperandKind.ByteVar, // unaligned.
        0x13 or 0x14 => OperandKind.None, // volatile. / tail.
        0x15 => OperandKind.TokenType, // initobj
        0x16 => OperandKind.TokenType, // constrained.
        0x17 or 0x18 => OperandKind.None, // cpblk/initblk
        0x1A => OperandKind.None,  // rethrow
        0x1C => OperandKind.TokenType, // sizeof
        0x1D => OperandKind.None,  // refanytype
        0x1E => OperandKind.None,  // readonly.
        _ => null,
    };
}
