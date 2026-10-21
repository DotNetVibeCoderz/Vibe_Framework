using System.Buffers.Binary;
using RustNet.Deploy;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The ELF→RNPC converter, on real modules: runtime/npc-sample-mcu linked by
/// `rustnet native build` for an M0+ (RP2040) and an M7 (Meadow F7), debug
/// info stripped. The loader side is runtime/rustnet-npc; the pair is checked
/// end to end on an emulated Cortex-M by runtime/rustnet-npc/tools/emulate.py.
/// </summary>
public class NpcModuleTests
{
    private static byte[] Fixture(string target) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", $"npc-sample-{target}.elf"));

    private static readonly string[] Procedures = ["rustnet_crc32", "rustnet_fail", "rustnet_reverse", "rustnet_sum_u32"];

    [Theory]
    [InlineData("thumbv6m-none-eabi", NpcModule.Arch.ThumbV6M)]
    [InlineData("thumbv7em-none-eabihf", NpcModule.Arch.ThumbV7EM)]
    public void ConvertsAModuleTheLoaderCanPlace(string target, NpcModule.Arch arch)
    {
        var m = NpcModule.FromElf(Fixture(target), arch, Procedures);
        Assert.Equal(Procedures, m.Exports.Select(e => e.Name));
        // Thumb entry points, inside the image.
        Assert.All(m.Exports, e => Assert.True((e.Value & 1) == 1 && (e.Value & ~1u) < m.ImageLength, $"{e.Name} at {e.Value}"));
        Assert.NotEmpty(m.Relocations);

        // The header the device parses.
        var b = m.Rnpc;
        Assert.Equal("RNPC"u8.ToArray(), b[..4]);
        Assert.Equal((ushort)arch, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)));
        Assert.Equal((uint)m.ImageLength, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));

        // Every plain relocation is an aligned word holding an address inside the module.
        var image = b[^m.ImageLength..];
        foreach (uint r in m.Relocations.Where(r => (r & 0x8000_0000) == 0))
        {
            Assert.Equal(0u, r % 4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)r));
            Assert.InRange(value, 0u, (uint)(m.ImageLength + m.BssLength));
        }
    }

    [Fact]
    public void CortexM7ModulesCarryMovwMovtPairs()
    {
        // The precompiled core for thumbv7em builds addresses with movw/movt,
        // so the converter must record them as pairs, not refuse the module.
        var m = NpcModule.FromElf(Fixture("thumbv7em-none-eabihf"), NpcModule.Arch.ThumbV7EM, Procedures);
        Assert.Contains(m.Relocations, r => (r & 0x8000_0000) != 0);
    }

    [Fact]
    public void RefusesWhatIsNotAnArmModule()
    {
        Assert.Throws<InvalidDataException>(() => NpcModule.FromElf(new byte[64], NpcModule.Arch.ThumbV6M));
        Assert.Throws<InvalidDataException>(() =>
            NpcModule.FromElf(Fixture("thumbv6m-none-eabi"), NpcModule.Arch.ThumbV6M, ["no_such_procedure"]));
    }
}
