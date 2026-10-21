using RustNet.Deploy;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// Unit tests for RNDP-over-BLE: the byte stream is fragmented into MTU-sized
/// GATT packets and reassembled intact, so the RNDP framing works over BLE. The
/// radio is stubbed with an in-memory loopback link.
/// </summary>
public class BleTransportTests
{
    [Fact]
    public void FragmentsAndReassemblesLargePayloadIntact()
    {
        var (a, b) = LoopbackBleLink.Pair(mtu: 23);
        using var sender = new BleTransport(a);
        using var receiver = new BleTransport(b);

        byte[] payload = new byte[5000];
        new Random(42).NextBytes(payload);
        sender.Write(payload);

        byte[] got = new byte[payload.Length];
        int total = 0;
        byte[] tmp = new byte[256];
        while (total < payload.Length)
        {
            int n = receiver.Read(tmp, 1000);
            Assert.True(n > 0, "read timed out before the full payload arrived");
            Array.Copy(tmp, 0, got, total, n);
            total += n;
        }
        Assert.Equal(payload, got);
    }

    [Fact]
    public void SplitsIntoMtuSizedPackets()
    {
        var (a, b) = LoopbackBleLink.Pair(mtu: 20);
        using var sender = new BleTransport(a);
        sender.Write(new byte[100]);

        int packets = 0;
        byte[] buf = new byte[64];
        int n;
        while ((n = b.Receive(buf, 100)) > 0)
        {
            Assert.True(n <= 20, "packet exceeds MTU");
            packets++;
        }
        Assert.Equal(5, packets); // ceil(100 / 20)
    }

    [Fact]
    public void FactoryRejectsBleWithoutBackendButAcceptsAProvider()
    {
        var saved = TransportFactory.BleLinkProvider;
        try
        {
            TransportFactory.BleLinkProvider = null;
            Assert.Throws<NotSupportedException>(() => TransportFactory.Open("ble:AA:BB:CC:DD:EE:FF"));

            var (a, _) = LoopbackBleLink.Pair(20);
            string? seenAddress = null;
            TransportFactory.BleLinkProvider = addr =>
            {
                seenAddress = addr;
                return a;
            };
            using var transport = TransportFactory.Open("ble:AA:BB:CC:DD:EE:FF");
            Assert.IsType<BleTransport>(transport);
            Assert.Equal("AA:BB:CC:DD:EE:FF", seenAddress); // address keeps its colons
        }
        finally
        {
            TransportFactory.BleLinkProvider = saved;
        }
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa-bb-cc-dd-ee-ff", 0xAABBCCDDEEFFUL)]
    [InlineData("24:0a:c4:12:ab:0f", 0x240AC412AB0FUL)]
    [InlineData("240AC412AB0F", 0x240AC412AB0FUL)]
    public void ParsesBleAddresses(string spec, ulong expected)
    {
        var target = BleTarget.Parse(spec);
        Assert.Equal(expected, target.Address);
        Assert.Null(target.Name);
        // Formatting round-trips through parsing.
        Assert.Equal(expected, BleTarget.Parse(BleTarget.FormatAddress(expected)).Address);
    }

    [Theory]
    [InlineData("RustNet-AB0F")]
    [InlineData("AA:BB:CC:DD:EE")]        // five octets
    [InlineData("AA:BB:CC:DD:EE:GG")]     // not hex
    [InlineData("AABBCCDDEEF")]           // eleven digits
    [InlineData("A:BB:CC:DD:EE:FF0")]     // octets of the wrong width
    public void AnythingThatIsNotAnAddressIsAName(string spec)
    {
        var target = BleTarget.Parse(spec);
        Assert.Null(target.Address);
        Assert.Equal(spec, target.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    public void EmptyOrStarMeansTheFirstRustNetDevice(string spec)
    {
        var target = BleTarget.Parse(spec);
        Assert.Null(target.Address);
        Assert.Null(target.Name);
        Assert.True(target.MatchesName("RustNet-1234"));
        Assert.False(target.MatchesName("SomeHeadphones"));
        Assert.False(target.MatchesName(""));
    }

    [Fact]
    public void NamedTargetMatchesCaseInsensitivelyAndExactly()
    {
        var target = BleTarget.Parse("rustnet-ab0f");
        Assert.True(target.MatchesName("RustNet-AB0F"));
        Assert.False(target.MatchesName("RustNet-AB0E"));
    }

    [Fact]
    public void AddressFormatsAsSixColonSeparatedOctets()
    {
        Assert.Equal("00:00:00:00:00:01", BleTarget.FormatAddress(1));
        Assert.Equal("24:0A:C4:12:AB:0F", new BleAdvertisement(0x240AC412AB0F, "RustNet-AB0F", -60).AddressText);
    }

    [Fact]
    public void ServiceUuidsMatchTheFirmware()
    {
        // runtime/firmware/src/ble.rs — Nordic UART Service.
        Assert.Equal("6e400001-b5a3-f393-e0a9-e50e24dcca9e", RustNetBle.ServiceUuid.ToString());
        Assert.Equal("6e400002-b5a3-f393-e0a9-e50e24dcca9e", RustNetBle.RxCharacteristicUuid.ToString());
        Assert.Equal("6e400003-b5a3-f393-e0a9-e50e24dcca9e", RustNetBle.TxCharacteristicUuid.ToString());
        Assert.Equal("RustNet-", RustNetBle.NamePrefix);
    }

    [Fact]
    public void BleWithoutBackendExplainsWhyForThisPlatform()
    {
        var saved = (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider);
        try
        {
            TransportFactory.BleLinkProvider = null;
            TransportFactory.BleScanProvider = null;
            var ex = Assert.Throws<NotSupportedException>(() => TransportFactory.Open("ble:RustNet-AB0F"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("backend", ex.Message);
            }
            else
            {
                Assert.Contains("only supported on Windows", ex.Message);
            }
        }
        finally
        {
            (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider) = saved;
        }
    }

    [Fact]
    public void PlatformRegistrationFailsCleanlyWhenTheBackendIsAbsent()
    {
        var saved = (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider);
        string empty = Directory.CreateTempSubdirectory("rustnet-ble-").FullName;
        try
        {
            TransportFactory.BleLinkProvider = null;
            TransportFactory.BleScanProvider = null;
            Assert.False(BlePlatform.TryRegister(empty));
            Assert.Null(TransportFactory.BleLinkProvider);
            Assert.NotNull(BlePlatform.LastError);
            Assert.Contains(OperatingSystem.IsWindows() ? "not found" : "only supported on Windows", BlePlatform.LastError);
        }
        finally
        {
            (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider) = saved;
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// The reflection contract between BlePlatform and the Windows backend
    /// (assembly, type and method names), checked against the copy the CLI
    /// build drops next to rustnet.exe. Registering touches no radio.
    /// </summary>
    [SkippableFact]
    public void CliShipsAWindowsBackendThatRegistersBothProviders()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), "WinRT BLE backend is Windows-only");
        string? cliDir = FindCliOutputWithBackend();
        Skip.If(cliDir is null, "CLI not built with the Windows BLE backend");

        var saved = (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider);
        try
        {
            TransportFactory.BleLinkProvider = null;
            TransportFactory.BleScanProvider = null;
            Assert.True(BlePlatform.TryRegister(cliDir), BlePlatform.LastError);
            Assert.NotNull(TransportFactory.BleLinkProvider);
            Assert.NotNull(TransportFactory.BleScanProvider);
        }
        finally
        {
            (TransportFactory.BleLinkProvider, TransportFactory.BleScanProvider) = saved;
        }
    }

    private static string? FindCliOutputWithBackend()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string cliBin = Path.Combine(dir.FullName, "tools", "RustNet.Cli", "bin");
            if (!Directory.Exists(cliBin))
            {
                continue;
            }
            return Directory
                .EnumerateFiles(cliBin, BlePlatform.WindowsAssembly + ".dll", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => File.Exists(Path.Combine(d!, "rustnet.dll")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d!, "rustnet.dll")))
                .FirstOrDefault();
        }
        return null;
    }
}
