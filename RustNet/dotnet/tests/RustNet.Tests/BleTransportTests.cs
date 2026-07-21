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
}
