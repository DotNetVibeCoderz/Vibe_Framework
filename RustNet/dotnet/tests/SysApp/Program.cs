using RustNet.Buses;
using RustNet.Data;
using RustNet.Devices;
using RustNet.Serialization;
using RustNet.UI;

namespace SysApp;

/// <summary>
/// System feature exercise for runtime v0.3: field buses (CAN, Modbus,
/// 1-Wire), networking (Ethernet/Cellular), database, power/RTC/watchdog,
/// external memory, signal control, serializers, streams and UI.
/// Every section prints a marker the E2E test asserts on.
/// </summary>
public static class Program
{
    public static void Main()
    {
        Console.WriteLine("SysApp starting");

        // ---- CAN (loopback) ----
        Can.Init(0, 500000, true);
        byte[] payload = new byte[3];
        payload[0] = 0x11;
        payload[1] = 0x22;
        payload[2] = 0x33;
        Can.Write(0, 0x123, payload);
        CanFrame frame = Can.Read(0);
        if (frame != null)
        {
            Console.WriteLine($"can rx id={frame.Id} len={frame.Data.Length}");
        }

        // ---- Modbus (device-internal slave, real RTU framing) ----
        Modbus.WriteRegister(1, 100, 1234);
        int[] regs = Modbus.ReadHoldingRegisters(1, 100, 1);
        Console.WriteLine($"modbus reg100={regs[0]}");
        Modbus.WriteCoil(1, 5, true);
        byte[] coils = Modbus.ReadCoils(1, 5, 1);
        Console.WriteLine($"modbus coil5={coils[0]}");

        // ---- 1-Wire (DS18B20 on the simulated bus) ----
        Ds18b20 sensor = Ds18b20.Find(0);
        if (sensor == null)
        {
            Console.WriteLine("onewire: no sensor");
        }
        else
        {
            Console.WriteLine($"onewire temp={sensor.ReadCentiCelsius()}");
        }

        // ---- Networking ----
        bool eth = RustNet.Net.Ethernet.Up();
        Console.WriteLine(string.Concat("eth ip=", RustNet.Net.Ethernet.GetIp(), " up=", eth.ToString()));
        bool cell = RustNet.Net.Cellular.Up("internet", "", "");
        Console.WriteLine(string.Concat("cell op=", RustNet.Net.Cellular.GetOperator(), " rssi=", RustNet.Net.Cellular.GetRssi().ToString()));

        // ---- Database (in-memory + persisted) ----
        Database db = Database.Open("/data/sensors.db");
        db.Execute("CREATE TABLE IF NOT EXISTS readings (id INTEGER, room TEXT, temp REAL)");
        db.Execute("DELETE FROM readings");
        db.Execute("INSERT INTO readings VALUES (1, 'kitchen', 21.5), (2, 'garage', 16.0), (3, 'attic', 28.9)");
        string count = db.Scalar("SELECT COUNT(*) FROM readings");
        string hottest = db.Scalar("SELECT room FROM readings ORDER BY temp DESC LIMIT 1");
        Console.WriteLine($"db count={count} hottest={hottest}");
        // Secondary index: indexed equality lookup (v1.0).
        db.Execute("CREATE INDEX IF NOT EXISTS idx_room ON readings (room)");
        string idxRoom = db.Scalar("SELECT room FROM readings WHERE room = 'attic'");
        Console.WriteLine($"db indexed room={idxRoom}");
        db.Close();
        // Reopen proves WAL/snapshot durability across handles (v1.0).
        Database db2 = Database.Open("/data/sensors.db");
        string persisted = db2.Scalar("SELECT COUNT(*) FROM readings");
        Console.WriteLine($"db reopened count={persisted}");
        db2.Close();

        // ---- RTC ----
        RustNet.Sys.Rtc.Set(1786190400); // 2026-08-04 12:00:00 UTC
        Console.WriteLine(string.Concat("rtc now=", RustNet.Sys.Rtc.NowString()));

        // ---- Watchdog ----
        RustNet.Sys.Watchdog.Start(5000);
        RustNet.Sys.Watchdog.Feed();
        Console.WriteLine(string.Concat("watchdog running=", RustNet.Sys.Watchdog.IsRunning().ToString()));
        RustNet.Sys.Watchdog.Stop();

        // ---- External memory (QSPI flash semantics) ----
        RustNet.Sys.ExtMemory.Erase(0, 0, 16);
        byte[] blob = new byte[2];
        blob[0] = 0xAB;
        blob[1] = 0xCD;
        RustNet.Sys.ExtMemory.Write(0, 0, blob);
        byte[] back = RustNet.Sys.ExtMemory.Read(0, 0, 2);
        Console.WriteLine($"extmem kind={RustNet.Sys.ExtMemory.Kind(0)} b0={back[0]}");

        // ---- Power / wake / device info ----
        RustNet.Sys.Power.ArmWakeGpio(0, true);
        RustNet.Sys.Power.ArmWakeRtc(60);
        Console.WriteLine(string.Concat("wake reason=", RustNet.Sys.Power.WakeReason()));
        Console.WriteLine(string.Concat("device chip=", RustNet.Sys.DeviceInfo.Chip(), " v", RustNet.Sys.DeviceInfo.Version()));

        // ---- Signal control (HC-SR04 driver) ----
        HcSr04 sonar = new HcSr04(7);
        Console.WriteLine($"sonar mm={sonar.ReadMillimeters()}");

        // ---- JSON / XML / Binary serializers ----
        JsonValue doc = JsonValue.NewObject();
        doc.Set("device", "boiler-1");
        doc.Set("temp", 21.5);
        doc.Set("ok", true);
        JsonValue tags = JsonValue.NewArray();
        tags.Add(JsonValue.FromString("iot"));
        tags.Add(JsonValue.FromString("heat"));
        doc.Set("tags", tags);
        string json = doc.ToJson();
        JsonValue parsed = Json.Parse(json);
        Console.WriteLine($"json temp={parsed.Get("temp").AsDouble} tags={parsed.Get("tags").Count}");

        XmlNode xml = Xml.Parse("<config interval=\"30\"><mqtt host=\"broker\" /><name>boiler</name></config>");
        Console.WriteLine(string.Concat("xml interval=", xml.GetAttr("interval"), " name=", xml.Child("name").Text));

        byte[] bin = BinarySerializer.Serialize(doc);
        JsonValue round = BinarySerializer.Deserialize(bin);
        Console.WriteLine($"binary bytes={bin.Length} device={round.Get("device").AsString}");

        // ---- Streams ----
        RustNet.IO.MemoryStream ms = new RustNet.IO.MemoryStream();
        RustNet.IO.BinaryPacker packer = new RustNet.IO.BinaryPacker(ms);
        packer.WriteInt(42);
        packer.WriteString("stream");
        ms.Seek(0);
        int n42 = packer.ReadInt();
        string label = packer.ReadString();
        Console.WriteLine($"stream int={n42} str={label}");

        RustNet.IO.FileStream fs = RustNet.IO.FileStream.Create("/data/stream.bin");
        fs.Write(bin);
        fs.Close();
        Console.WriteLine($"filestream len={RustNet.IO.FileSystem.ReadAllBytes("/data/stream.bin").Length}");

        // ---- UI from XML ----
        UiElement screen = Ui.LoadXml("<window width=\"160\" height=\"128\" bg=\"0000\"><label id=\"title\" text=\"Boiler\" scale=\"2\" fg=\"FFFF\"/><progress id=\"temp\" value=\"42\" max=\"100\" fg=\"F800\"/><button text=\"RESET\" bg=\"4208\"/></window>");
        UiElement title = screen.FindById("title");
        Ui.Render(screen);
        Console.WriteLine(string.Concat("ui title=", title.Text));

        Console.WriteLine("SysApp finished");
    }
}
