using RustNet.Deploy;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The board recipes are data, and data that is only ever exercised by
/// plugging a board in is data nobody has checked. These cover the parts that
/// can be wrong without any hardware present: where an artifact lands, and
/// what the flash plan actually asks to run.
/// </summary>
public class BoardCatalogTests
{
    [Fact]
    public void EveryBoardHasADistinctIdAndAKnownChip()
    {
        var ids = BoardCatalog.All.Select(b => b.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (BoardRecipe board in BoardCatalog.All)
        {
            // A recipe naming a chip the signer does not know would let someone
            // build firmware for a board and then be unable to sign anything
            // for it.
            _ = Signing.ParseChip(board.Chip);
            Assert.False(string.IsNullOrWhiteSpace(board.Requires));
            Assert.False(string.IsNullOrWhiteSpace(board.Note));
        }
    }

    [Fact]
    public void PicoIsFlashedByUf2OverItsOwnUsb()
    {
        BoardRecipe pico = BoardCatalog.Find("pico")!;
        Assert.Equal("rp2040", pico.Chip);
        Assert.Equal(FlashKind.Uf2, pico.Flash);
        // No vendor tool and no probe, so no port to name.
        Assert.False(pico.NeedsPort);
    }

    [Fact]
    public void TheC3BuildsFromTheSameWorkspaceAsTheEsp32ButForAnotherChip()
    {
        BoardRecipe esp32 = BoardCatalog.Find("esp32")!;
        BoardRecipe c3 = BoardCatalog.Find("esp32c3")!;
        Assert.Equal(esp32.WorkspaceDir, c3.WorkspaceDir);

        // Three things have to line up or the image is silently for the wrong
        // chip: the ESP-IDF build takes its SoC from MCU, the Rust side needs
        // the RISC-V target rather than Xtensa, and the firmware feature picks
        // both the reported chip identity and the peripheral RNDP runs over.
        Assert.Equal("esp32c3", c3.Env["MCU"]);
        Assert.Contains("--target riscv32imc-esp-espidf", c3.BuildArgs);
        Assert.Contains("--features chip-esp32c3", c3.BuildArgs);
        Assert.Contains("--no-default-features", c3.BuildArgs);
    }

    [Fact]
    public void ResolveTargetReadsTheWorkspacesOwnCargoConfig()
    {
        string root = TempRepo();
        try
        {
            var board = new BoardRecipe
            {
                Id = "x", Name = "X", Chip = "any", WorkspaceDir = "runtime/port",
                BuildArgs = "build --release", BinaryName = "fw",
                Flash = FlashKind.Uf2, Requires = "-", Note = "-",
            };
            var (dir, triple) = BoardCatalog.ResolveTarget(root, board);

            // `target-dir` and `target` both come from `[build]`, and the
            // absolute `target-dir` must not be joined onto the workspace.
            Assert.Equal(ElsewhereDir, dir.Replace('\\', '/'));
            Assert.Equal("thumbv6m-none-eabi", triple);
            Assert.Equal(
                $"{ElsewhereDir}/thumbv6m-none-eabi/release/fw",
                BoardCatalog.ArtifactPath(root, board).Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ATargetInTheBuildArgumentsWinsOverTheConfig()
    {
        string root = TempRepo();
        try
        {
            var board = new BoardRecipe
            {
                Id = "x", Name = "X", Chip = "any", WorkspaceDir = "runtime/port",
                BuildArgs = "build --release --target riscv32imc-esp-espidf", BinaryName = "fw",
                Flash = FlashKind.Espflash, Requires = "-", Note = "-",
            };
            Assert.Equal("riscv32imc-esp-espidf", BoardCatalog.ResolveTarget(root, board).Triple);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DfuBoardsObjcopyBeforeTheyFlash()
    {
        // dfu-util writes a raw image to an address; handing it an ELF would
        // write the ELF headers to 0x08000000 and the board would not boot.
        foreach (string id in new[] { "netduino3", "meadow-f7" })
        {
            var steps = BoardCatalog.FlashPlan(TempRoot(), BoardCatalog.Find(id)!, port: null);
            Assert.Equal(2, steps.Count);
            Assert.Equal("rust-objcopy", Assert.IsType<RunStep>(steps[0]).Exe);
            var flash = Assert.IsType<RunStep>(steps[1]);
            Assert.Equal("dfu-util", flash.Exe);
            // The STM32 ROM bootloader's own id, and the internal-flash origin.
            Assert.Contains("0483:df11", flash.Args);
            Assert.Contains("0x08000000", flash.Args);
        }
    }

    [Fact]
    public void EspFlashPlanCarriesThePartitionTable()
    {
        var steps = BoardCatalog.FlashPlan(TempRoot(), BoardCatalog.Find("esp32")!, "COM5");
        var run = Assert.IsType<RunStep>(Assert.Single(steps));
        Assert.Equal("espflash", run.Exe);
        // Without it there is no FAT storage partition, and everything the
        // device is supposed to remember is lost on the next power cycle.
        Assert.Contains("--partition-table partitions.csv", run.Args);
        Assert.Contains("--port COM5", run.Args);
    }

    [Fact]
    public void K210PlanObjcopiesBeforeItFlashes()
    {
        var steps = BoardCatalog.FlashPlan(TempRoot(), BoardCatalog.Find("k210")!, "COM7");
        Assert.Equal(2, steps.Count);
        // kflash wants a raw binary; handing it the ELF flashes nonsense.
        Assert.Equal("rust-objcopy", Assert.IsType<RunStep>(steps[0]).Exe);
        Assert.Equal("kflash", Assert.IsType<RunStep>(steps[1]).Exe);
    }

    [Fact]
    public void PicoPlanAsksTheDeviceIntoItsBootloaderOnlyWhenOneIsNamed()
    {
        string root = TempRoot();
        BoardRecipe pico = BoardCatalog.Find("pico")!;

        var unattended = BoardCatalog.FlashPlan(root, pico, port: null, deviceSpec: "serial:COM12");
        Assert.Collection(unattended,
            s => Assert.IsType<RunStep>(s),
            s => Assert.IsType<EnterBootloaderStep>(s),
            s => Assert.IsType<CopyToVolumeStep>(s));

        // With no device to ask, the board is expected to be in BOOTSEL
        // already — the step is skipped rather than failing the plan.
        var manual = BoardCatalog.FlashPlan(root, pico, port: null);
        Assert.DoesNotContain(manual, s => s is EnterBootloaderStep);
    }

    /// <summary>A repo root with one workspace that has its own cargo config.</summary>
    /// An absolute path on whichever OS the tests are running on.
    ///
    /// The fixture used to hard-code "C:/elsewhere", which is absolute on
    /// Windows and an ordinary relative path everywhere else — so on Linux
    /// `ResolveTarget` correctly joined it onto the workspace and the test
    /// correctly reported a mismatch. The behaviour under test is "an
    /// absolute target-dir is not joined onto the workspace", and that
    /// sentence needs a path the running OS agrees is absolute.
    private static readonly string ElsewhereDir =
        OperatingSystem.IsWindows() ? "C:/elsewhere" : "/elsewhere";

    private static string TempRepo()
    {
        string root = TempRoot();
        string ws = Path.Combine(root, "runtime", "port", ".cargo");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "config.toml"), $"""
            [build]
            target = "thumbv6m-none-eabi"
            # keep the build tree out of the repo
            target-dir = "{ElsewhereDir}"

            [target.thumbv6m-none-eabi]
            runner = "probe-rs run"
            """);
        return root;
    }

    private static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "rustnet-boards-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }
}
