using System.Buffers.Binary;
using System.Text;

namespace RustNet.Deploy;

/// <summary>
/// Converts an ARM ELF — a native module linked at address 0 with
/// <c>--emit-relocs</c> — into the RNPC format microcontroller firmware loads
/// (see <c>runtime/rustnet-npc</c>, which reads exactly what this writes).
/// The module's allocated sections become one image; every absolute
/// relocation — an <c>R_ARM_ABS32</c> word, or a Thumb-2 <c>movw</c>/<c>movt</c>
/// pair (the precompiled <c>core</c> for Cortex-M4/M7 builds addresses that
/// way) — becomes an entry the loader rebases; PC-relative relocations need nothing,
/// because the image moves as a whole. Anything else is refused by name, so a
/// module that would not run where it is loaded is never produced.
/// </summary>
public static class NpcModule
{
    public enum Arch : ushort
    {
        ThumbV6M = 1,
        ThumbV7EM = 2,
    }

    private const int R_ARM_ABS32 = 2;
    private const int R_ARM_TARGET1 = 38;
    private const int R_ARM_THM_MOVW_ABS_NC = 47;
    private const int R_ARM_THM_MOVT_ABS = 48;
    private const uint RelocPair = 0x8000_0000;

    /// <summary>Relocation types that are PC-relative within the image, so moving it changes nothing.</summary>
    private static readonly HashSet<int> PcRelative =
    [
        3,   // R_ARM_REL32
        10,  // R_ARM_THM_CALL
        11,  // R_ARM_THM_PC8
        30,  // R_ARM_THM_JUMP24
        42,  // R_ARM_PREL31
        102, // R_ARM_THM_JUMP11
        103, // R_ARM_THM_JUMP8
        104, // R_ARM_THM_JUMP6 / ALU_PREL
        105, // R_ARM_THM_ALU_PREL_11_0
        106, // R_ARM_THM_PC12
        51,  // R_ARM_THM_JUMP19
    ];

    public sealed record Converted(byte[] Rnpc, int ImageLength, int BssLength, IReadOnlyList<(string Name, uint Value)> Exports, IReadOnlyList<uint> Relocations);

    /// <summary>Convert a linked ELF to RNPC, exporting <paramref name="exports"/> (all global functions when null).</summary>
    public static Converted FromElf(byte[] elf, Arch arch, IReadOnlyCollection<string>? exports = null)
    {
        var e = new Elf32(elf);
        // The image: every allocated section, laid out by address from 0.
        var alloc = e.Sections.Where(s => (s.Flags & 0x2) != 0 && s.Size > 0).OrderBy(s => s.Addr).ToList();
        var progbits = alloc.Where(s => s.Type != 8 /* NOBITS */).ToList();
        var nobits = alloc.Where(s => s.Type == 8).ToList();
        uint imageEnd = progbits.Count == 0 ? 0 : progbits.Max(s => s.Addr + s.Size);
        if (nobits.Any(b => b.Addr < imageEnd))
        {
            throw new InvalidDataException("a .bss section sits inside the image; link with the RNPC script so it comes last");
        }
        var image = new byte[imageEnd];
        foreach (var s in progbits)
        {
            Array.Copy(elf, s.Offset, image, s.Addr, s.Size);
        }
        uint bssEnd = nobits.Count == 0 ? imageEnd : nobits.Max(s => s.Addr + s.Size);
        int bss = (int)(bssEnd - imageEnd);

        // Relocations against allocated sections. In a linked (ET_EXEC) file
        // r_offset is already the address; in an object it is section-relative.
        var relocs = new SortedSet<uint>();
        var movw = new List<(uint Where, uint Sym)>();
        var movt = new List<(uint Where, uint Sym)>();
        foreach (var rel in e.Sections.Where(s => s.Type == 9 /* SHT_REL */))
        {
            var target = e.Sections[(int)rel.Info];
            if ((target.Flags & 0x2) == 0)
            {
                continue; // debug info
            }
            for (uint at = rel.Offset; at < rel.Offset + rel.Size; at += 8)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)at));
                uint info = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)at + 4));
                int type = (int)(info & 0xFF);
                uint sym = info >> 8;
                uint where = e.IsExecutable ? offset : target.Addr + offset;
                if (type == R_ARM_THM_MOVW_ABS_NC)
                {
                    movw.Add((where, sym));
                }
                else if (type == R_ARM_THM_MOVT_ABS)
                {
                    movt.Add((where, sym));
                }
                else if (type is R_ARM_ABS32 or R_ARM_TARGET1)
                {
                    if (where % 4 != 0 || where + 4 > imageEnd)
                    {
                        throw new InvalidDataException($"absolute relocation at 0x{where:X} is not a word inside the image");
                    }
                    relocs.Add(where);
                }
                else if (!PcRelative.Contains(type))
                {
                    throw new InvalidDataException(
                        $"relocation type {type} at 0x{where:X} cannot be rebased by the RNPC loader");
                }
            }
        }

        // movw/movt: pair each movw with a movt against the same symbol, in
        // address order, and check the 32-bit value they load lies in the module.
        var pairs = new List<(uint Movw, uint Movt)>();
        foreach (var group in movw.GroupBy(m => m.Sym))
        {
            var lows = group.OrderBy(m => m.Where).ToList();
            var highs = movt.Where(m => m.Sym == group.Key).OrderBy(m => m.Where).ToList();
            if (lows.Count != highs.Count)
            {
                throw new InvalidDataException($"unpaired movw/movt relocations against symbol {group.Key}");
            }
            for (int i = 0; i < lows.Count; i++)
            {
                uint value = (uint)(ThumbImm16(image, highs[i].Where) << 16 | ThumbImm16(image, lows[i].Where));
                if (value > bssEnd)
                {
                    throw new InvalidDataException($"movw/movt at 0x{lows[i].Where:X}/0x{highs[i].Where:X} loads 0x{value:X}, outside the module");
                }
                pairs.Add((lows[i].Where, highs[i].Where));
            }
        }
        if (movt.Count != pairs.Count)
        {
            throw new InvalidDataException("movt relocation without a matching movw");
        }

        // Exports: global functions, the Thumb bit set as a call needs it.
        var found = new List<(string, uint)>();
        foreach (var sym in e.Symbols)
        {
            if (sym.Bind != 1 || sym.Type != 2 || sym.Shndx == 0)
            {
                continue; // not a global, defined function
            }
            if (exports != null ? !exports.Contains(sym.Name) : sym.Name.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }
            found.Add((sym.Name, sym.Value | 1));
        }
        if (exports != null)
        {
            var missing = exports.Except(found.Select(f => f.Item1)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException("exported procedure(s) not found in the linked module: " + string.Join(", ", missing));
            }
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("RNPC"u8);
        w.Write((ushort)1);
        w.Write((ushort)arch);
        w.Write((uint)image.Length);
        w.Write((uint)bss);
        w.Write(4u);
        w.Write((uint)found.Count);
        w.Write((uint)(relocs.Count + pairs.Count * 2));
        foreach (var (name, value) in found)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            w.Write((ushort)n.Length);
            w.Write(n);
            w.Write(value);
        }
        foreach (uint r in relocs)
        {
            w.Write(r);
        }
        foreach (var (lo, hi) in pairs.OrderBy(p => p.Movw))
        {
            w.Write(lo | RelocPair);
            w.Write(hi);
        }
        w.Write(image);
        w.Flush();
        return new Converted(o.ToArray(), image.Length, bss, found, relocs.Concat(pairs.Select(p => p.Movw | RelocPair)).ToList());
    }

    /// <summary>The 16-bit immediate of a Thumb-2 MOVW/MOVT at <paramref name="at"/>.</summary>
    private static int ThumbImm16(byte[] image, uint at)
    {
        int hw1 = image[at] | image[at + 1] << 8;
        int hw2 = image[at + 2] | image[at + 3] << 8;
        return (hw1 & 0xF) << 12 | ((hw1 >> 10) & 1) << 11 | ((hw2 >> 12) & 7) << 8 | (hw2 & 0xFF);
    }

    /// <summary>Where the Xtensa linker script puts the data segment (RNPC v2).</summary>
    public const uint DataLinkBase = 0x1000_0000;

    /// <summary>
    /// An ESP32 (Xtensa) module → RNPC version 2, with code and data in separate
    /// segments: the code goes to instruction RAM, which takes only word
    /// accesses, and constant tables read a byte at a time must stay in data
    /// RAM. Absolute words are rewritten relative to the segment they point
    /// into; PC-relative operands must stay inside the code segment.
    /// </summary>
    public static Converted FromXtensaElf(byte[] elf, IReadOnlyCollection<string> exports)
    {
        var e = new Elf32(elf, machine: 94 /* EM_XTENSA */);
        var alloc = e.Sections.Where(s => (s.Flags & 0x2) != 0 && s.Size > 0).ToList();
        var textSecs = alloc.Where(s => s.Addr < DataLinkBase && s.Type != 8).ToList();
        var dataSecs = alloc.Where(s => s.Addr >= DataLinkBase && s.Type != 8).ToList();
        var bssSecs = alloc.Where(s => s.Type == 8).ToList();
        if (bssSecs.Any(b => b.Addr < DataLinkBase))
        {
            throw new InvalidDataException(".bss linked into the code segment");
        }
        uint textEnd = textSecs.Count == 0 ? 0 : textSecs.Max(s => s.Addr + s.Size);
        uint dataEnd = dataSecs.Count == 0 ? DataLinkBase : dataSecs.Max(s => s.Addr + s.Size);
        uint bssEnd = bssSecs.Count == 0 ? dataEnd : bssSecs.Max(s => s.Addr + s.Size);
        var text = new byte[textEnd];
        var data = new byte[dataEnd - DataLinkBase];
        foreach (var s in textSecs)
        {
            Array.Copy(elf, s.Offset, text, s.Addr, s.Size);
        }
        foreach (var s in dataSecs)
        {
            Array.Copy(elf, s.Offset, data, s.Addr - DataLinkBase, s.Size);
        }

        var relocs = new SortedSet<uint>();
        foreach (var rela in e.Sections.Where(s => s.Type == 4 /* SHT_RELA */))
        {
            var target = e.Sections[(int)rela.Info];
            if ((target.Flags & 0x2) == 0)
            {
                continue;
            }
            for (uint at = rela.Offset; at < rela.Offset + rela.Size; at += 12)
            {
                uint where = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)at));
                uint info = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)at + 4));
                int type = (int)(info & 0xFF);
                switch (type)
                {
                    case 0:  // R_XTENSA_NONE
                    case 11: // R_XTENSA_ASM_EXPAND: an assembler hint, nothing to patch
                        continue;
                    case 1:  // R_XTENSA_32: an absolute word
                    {
                        bool inData = where >= DataLinkBase;
                        byte[] seg = inData ? data : text;
                        uint off = inData ? where - DataLinkBase : where;
                        if (off % 4 != 0 || off + 4 > seg.Length)
                        {
                            throw new InvalidDataException($"absolute relocation at 0x{where:X} is not a word inside the module");
                        }
                        uint value = BinaryPrimitives.ReadUInt32LittleEndian(seg.AsSpan((int)off));
                        bool toData = value >= DataLinkBase;
                        if (toData ? value > bssEnd : value > textEnd)
                        {
                            throw new InvalidDataException($"word at 0x{where:X} holds 0x{value:X}, outside the module");
                        }
                        BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan((int)off), toData ? value - DataLinkBase : value);
                        relocs.Add(off | (inData ? 1u << 31 : 0) | (toData ? 1u << 30 : 0));
                        break;
                    }
                    case 20: // R_XTENSA_SLOT0_OP: L32R / CALLn / J — PC-relative
                    {
                        uint sym = info >> 8;
                        uint dest = sym < e.Symbols.Count ? e.Symbols[(int)sym].Value : 0;
                        if (where >= DataLinkBase || dest >= DataLinkBase)
                        {
                            throw new InvalidDataException(
                                $"PC-relative operand at 0x{where:X} reaches the data segment; literals must stay with the code (.literal in .text)");
                        }
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Xtensa relocation type {type} at 0x{where:X} cannot be handled by the RNPC loader");
                }
            }
        }

        var found = new List<(string, uint)>();
        foreach (var sym in e.Symbols)
        {
            if (sym.Bind == 1 && sym.Type == 2 && sym.Shndx != 0 && exports.Contains(sym.Name))
            {
                found.Add((sym.Name, sym.Value));
            }
        }
        var missing = exports.Except(found.Select(f => f.Item1)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException("exported procedure(s) not found in the linked module: " + string.Join(", ", missing));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("RNPC"u8);
        w.Write((ushort)2);
        w.Write((ushort)3); // XtensaEsp32
        w.Write((uint)text.Length);
        w.Write((uint)data.Length);
        w.Write(bssEnd - dataEnd);
        w.Write((uint)found.Count);
        w.Write((uint)relocs.Count);
        foreach (var (name, value) in found)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            w.Write((ushort)n.Length);
            w.Write(n);
            w.Write(value);
        }
        foreach (uint r in relocs)
        {
            w.Write(r);
        }
        w.Write(text);
        w.Write(data);
        w.Flush();
        return new Converted(o.ToArray(), text.Length + data.Length, (int)(bssEnd - dataEnd), found, relocs.ToList());
    }

    /// <summary>The ESP32 linker script: code (with its literal pools) at 0, data at <see cref="DataLinkBase"/>.</summary>
    public const string XtensaLinkerScript = """
        SECTIONS
        {
          . = 0;
          .text : ALIGN(4) { *(.literal .literal.* .text .text.*) }
          . = 0x10000000;
          .rodata : ALIGN(4) { *(.rodata .rodata.*) }
          .data : ALIGN(4) { *(.data .data.*) }
          .bss : ALIGN(4) { *(.bss .bss.* COMMON) }
          /DISCARD/ : { *(.comment) *(.xtensa.info) *(.xt.prop*) *(.xt.lit*) *(.init_array*) *(.fini_array*) }
        }
        """;

    /// <summary>The linker script that lays a module out the way RNPC expects.</summary>
    public const string LinkerScript = """
        SECTIONS
        {
          . = 0;
          .text : ALIGN(4) { *(.text .text.*) }
          .rodata : ALIGN(4) { *(.rodata .rodata.*) }
          .data : ALIGN(4) { *(.data .data.*) }
          .bss : ALIGN(4) { *(.bss .bss.* COMMON) }
          /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.ARM.attributes) *(.comment) *(.init_array*) *(.fini_array*) }
        }
        """;

    private sealed class Elf32
    {
        public sealed record Section(uint Name, uint Type, uint Flags, uint Addr, uint Offset, uint Size, uint Link, uint Info);
        public sealed record Symbol(string Name, uint Value, int Bind, int Type, ushort Shndx);

        public List<Section> Sections { get; } = [];

        /// <summary>A linked image (ET_EXEC), whose relocation offsets are addresses.</summary>
        public bool IsExecutable { get; }
        public List<Symbol> Symbols { get; } = [];

        public Elf32(byte[] b, int machine = 40 /* EM_ARM */)
        {
            if (b.Length < 52 || b[0] != 0x7F || b[1] != 'E' || b[2] != 'L' || b[3] != 'F')
            {
                throw new InvalidDataException("not an ELF file");
            }
            if (b[4] != 1 || b[5] != 1)
            {
                throw new InvalidDataException("not a 32-bit little-endian ELF");
            }
            if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18)) != machine)
            {
                throw new InvalidDataException(machine == 40 ? "not an ARM ELF" : $"not an ELF for machine {machine}");
            }
            IsExecutable = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16)) == 2;
            uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
            int shentsize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(46));
            int shnum = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(48));
            for (int i = 0; i < shnum; i++)
            {
                var s = b.AsSpan((int)(shoff + i * shentsize));
                Sections.Add(new Section(
                    BinaryPrimitives.ReadUInt32LittleEndian(s),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[28..])));
            }
            var symtab = Sections.FirstOrDefault(s => s.Type == 2);
            if (symtab == null)
            {
                return;
            }
            var strtab = Sections[(int)symtab.Link];
            for (uint at = symtab.Offset; at < symtab.Offset + symtab.Size; at += 16)
            {
                var s = b.AsSpan((int)at);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(s);
                int end = (int)(strtab.Offset + nameOff);
                int stop = Array.IndexOf(b, (byte)0, end);
                Symbols.Add(new Symbol(
                    Encoding.UTF8.GetString(b, end, stop - end),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                    s[12] >> 4,
                    s[12] & 0xF,
                    BinaryPrimitives.ReadUInt16LittleEndian(s[14..])));
            }
        }
    }
}
