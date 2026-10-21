using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace RustNet.Deploy.Ble.Windows;

/// <summary>
/// The Windows BLE backend's entry point. <see cref="BlePlatform.TryRegister"/>
/// finds this type by name and calls <see cref="Register"/>; keep both names
/// in step with <see cref="BlePlatform.WindowsEntryType"/>.
/// </summary>
public static class WinRtBle
{
    /// <summary>How long a connect may scan for a device named by name, or not yet seen.</summary>
    public static TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static void Register()
    {
        TransportFactory.BleLinkProvider = spec => WinRtBleLink.Connect(BleTarget.Parse(spec), DiscoveryTimeout);
        TransportFactory.BleScanProvider = WinRtBleScanner.Scan;
    }

    internal static T Wait<T>(IAsyncOperation<T> operation, string what, TimeSpan? timeout = null)
    {
        var task = operation.AsTask();
        try
        {
            if (!task.Wait(timeout ?? TimeSpan.FromSeconds(30)))
            {
                operation.Cancel();
                throw new TimeoutException($"BLE: timed out {what}");
            }
            return task.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner)
        {
            // WinRT failures arrive as a bare HRESULT with an empty message;
            // say which step failed.
            string detail = inner.Message.Length > 0 ? inner.Message : "no message";
            throw new IOException($"BLE: {what} failed: {detail} (0x{inner.HResult:X8})", inner);
        }
    }
}

/// <summary>Finds advertising RustNet devices with the WinRT advertisement watcher.</summary>
public static class WinRtBleScanner
{
    private sealed class Seen
    {
        public string Name = "";
        public bool HasService;
        public short Rssi;
    }

    /// <summary>Every RustNet device heard within <paramref name="duration"/>, strongest first.</summary>
    public static IReadOnlyList<BleAdvertisement> Scan(TimeSpan duration)
        => Run(duration, stopWhen: null);

    /// <summary>Scan until a device matching <paramref name="target"/> is heard, or the timeout passes.</summary>
    public static BleAdvertisement? Find(BleTarget target, TimeSpan timeout)
    {
        var found = Run(timeout, stopWhen: ad => Matches(target, ad));
        return found.FirstOrDefault(ad => Matches(target, ad));
    }

    private static bool Matches(BleTarget target, BleAdvertisement ad)
        => target.Address is { } a ? ad.Address == a : target.MatchesName(ad.Name);

    private static IReadOnlyList<BleAdvertisement> Run(TimeSpan duration, Func<BleAdvertisement, bool>? stopWhen)
    {
        EnsureRadio();
        var seen = new ConcurrentDictionary<ulong, Seen>();
        using var done = new ManualResetEventSlim();
        BluetoothError? aborted = null;

        // Active scanning: the device's name travels in its scan response (its
        // advertisement is full with the 128-bit service UUID), and only an
        // active scanner asks for that.
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var entry = seen.GetOrAdd(e.BluetoothAddress, _ => new Seen());
            lock (entry)
            {
                if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
                {
                    entry.Name = e.Advertisement.LocalName;
                }
                if (e.Advertisement.ServiceUuids.Contains(RustNetBle.ServiceUuid))
                {
                    entry.HasService = true;
                }
                entry.Rssi = e.RawSignalStrengthInDBm;
                if (stopWhen is not null && IsRustNet(entry)
                    && stopWhen(new BleAdvertisement(e.BluetoothAddress, entry.Name, entry.Rssi)))
                {
                    done.Set();
                }
            }
        };
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
            {
                aborted = e.Error;
            }
            done.Set();
        };
        watcher.Start();
        done.Wait(duration);
        watcher.Stop();
        if (aborted is { } error)
        {
            throw new IOException($"BLE scan stopped: {error} (is Bluetooth switched on?)");
        }

        return seen
            .Where(kv => IsRustNet(kv.Value))
            .Select(kv => new BleAdvertisement(kv.Key, kv.Value.Name, kv.Value.Rssi))
            .OrderByDescending(ad => ad.Rssi)
            .ToList();
    }

    private static bool IsRustNet(Seen s)
        => s.HasService || s.Name.StartsWith(RustNetBle.NamePrefix, StringComparison.Ordinal);

    internal static void EnsureRadio()
    {
        var adapter = WinRtBle.Wait(BluetoothAdapter.GetDefaultAsync(), "finding the Bluetooth adapter")
            ?? throw new IOException("BLE: this PC has no Bluetooth adapter");
        if (!adapter.IsLowEnergySupported)
        {
            throw new IOException("BLE: the Bluetooth adapter does not support Low Energy");
        }
    }
}

/// <summary>
/// A connected RustNet device over WinRT GATT: RNDP bytes are written to the RX
/// characteristic and arrive as notifications on TX. <see cref="BleTransport"/>
/// turns this packet link into the byte stream <see cref="RndpClient"/> expects.
/// </summary>
public sealed class WinRtBleLink : IBlePacketLink
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _rx;
    private readonly GattCharacteristic _tx;
    private readonly GattWriteOption _writeOption;
    private readonly BlockingCollection<byte[]> _incoming = new();
    private byte[]? _partial;
    private int _partialPos;
    private volatile bool _lost;

    private WinRtBleLink(BluetoothLEDevice device, GattSession session, GattDeviceService service,
        GattCharacteristic rx, GattCharacteristic tx)
    {
        _device = device;
        _session = session;
        _service = service;
        _rx = rx;
        _tx = tx;
        // Write-without-response when the device offers it (it does): one
        // packet per connection event instead of one per round trip. Ordering
        // and delivery are still guaranteed by the link layer.
        _writeOption = rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
    }

    /// <summary>Application bytes per packet: the negotiated ATT MTU minus the 3-byte ATT header.</summary>
    public int Mtu => Math.Max(20, _session.MaxPduSize - 3);

    public static WinRtBleLink Connect(BleTarget target, TimeSpan discoveryTimeout)
    {
        WinRtBleScanner.EnsureRadio();
        BluetoothLEDevice? device = null;
        if (target.Address is { } known)
        {
            device = WinRtBle.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(known), "opening the device");
        }
        if (device is null)
        {
            // Named, or an address Windows has not heard yet: listen for it.
            var ad = WinRtBleScanner.Find(target, discoveryTimeout)
                ?? throw new IOException(
                    $"BLE: no RustNet device '{target}' found within {discoveryTimeout.TotalSeconds:0} s "
                    + "(is it powered, built with --features ble, and not connected to another host? try 'rustnet ble scan')");
            device = WinRtBle.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(ad.Address), "opening the device")
                ?? throw new IOException($"BLE: Windows could not open {ad.AddressText}");
        }

        try
        {
            var session = WinRtBle.Wait(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId), "opening a GATT session");
            session.MaintainConnection = true;

            var service = FindService(device);
            var rx = Characteristic(service, RustNetBle.RxCharacteristicUuid, "RX");
            var tx = Characteristic(service, RustNetBle.TxCharacteristicUuid, "TX");

            var link = new WinRtBleLink(device, session, service, rx, tx);
            device.ConnectionStatusChanged += (d, _) =>
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    link._lost = true;
                }
            };
            tx.ValueChanged += link.OnNotification;
            var status = WinRtBle.Wait(
                tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify),
                "subscribing to notifications");
            if (status != GattCommunicationStatus.Success)
            {
                link.Dispose();
                throw new IOException($"BLE: subscribing to RNDP notifications failed ({status})");
            }
            return link;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>The RNDP service, by UUID; failing that, from a full discovery.</summary>
    private static GattDeviceService FindService(BluetoothLEDevice device)
    {
        string address = BleTarget.FormatAddress(device.BluetoothAddress);
        GattDeviceServicesResult services;
        try
        {
            services = WinRtBle.Wait(
                device.GetGattServicesForUuidAsync(RustNetBle.ServiceUuid, BluetoothCacheMode.Uncached),
                "discovering services");
        }
        catch (IOException first)
        {
            // Discovery by UUID failed outright: try discovering everything,
            // which uses a different ATT request.
            try
            {
                services = WinRtBle.Wait(device.GetGattServicesAsync(BluetoothCacheMode.Uncached), "discovering all services");
            }
            catch (IOException second)
            {
                throw new IOException($"{first.Message}; {second.Message}", second);
            }
            var match = services.Services.FirstOrDefault(s => s.Uuid == RustNetBle.ServiceUuid);
            return match ?? throw new IOException(
                $"BLE: {address} has no RustNet RNDP service ({services.Status}, {services.Services.Count} services)");
        }
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            throw new IOException($"BLE: {address} has no RustNet RNDP service ({services.Status})");
        }
        return services.Services[0];
    }

    private static GattCharacteristic Characteristic(GattDeviceService service, Guid uuid, string what)
    {
        var result = WinRtBle.Wait(
            service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached),
            $"discovering the {what} characteristic");
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new IOException($"BLE: RNDP {what} characteristic missing ({result.Status})");
        }
        return result.Characteristics[0];
    }

    private void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] bytes);
        if (bytes is { Length: > 0 })
        {
            _incoming.Add(bytes);
        }
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        if (_lost)
        {
            throw new IOException("BLE: connection to the device was lost");
        }
        var buffer = CryptographicBuffer.CreateFromByteArray(packet.ToArray());
        var result = WinRtBle.Wait(_rx.WriteValueWithResultAsync(buffer, _writeOption), "writing to the device");
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"BLE: write failed ({result.Status}{(result.ProtocolError is { } e ? $", ATT error 0x{e:X2}" : "")})");
        }
    }

    public int Receive(byte[] buffer, int timeoutMs)
    {
        // A notification can be larger than the caller's buffer if the MTU grew
        // after it was sized; hand the rest over on the next call.
        if (_partial is null)
        {
            if (!_incoming.TryTake(out var packet, timeoutMs))
            {
                return 0;
            }
            _partial = packet;
            _partialPos = 0;
        }
        int n = Math.Min(buffer.Length, _partial.Length - _partialPos);
        Array.Copy(_partial, _partialPos, buffer, 0, n);
        _partialPos += n;
        if (_partialPos >= _partial.Length)
        {
            _partial = null;
        }
        return n;
    }

    public void Dispose()
    {
        _tx.ValueChanged -= OnNotification;
        if (!_lost)
        {
            try
            {
                WinRtBle.Wait(
                    _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None),
                    "unsubscribing", TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best effort: the device drops the subscription on disconnect anyway.
            }
        }
        _service.Dispose();
        _session.Dispose();
        _device.Dispose();
        _incoming.Dispose();
    }
}
