using RustNet.Community.Aht;
using RustNet.Community.Bus;

namespace DriversApp;

public static class AhtSmoke
{
    public static void Run()
    {
        // AHT20 that is not yet calibrated: Begin must send 0xBE 0x08 0x00.
        SimI2cDevice sim = new SimI2cDevice(0x38);
        sim.Responses.Enqueue(Hex.Bytes("10"));
        sim.Responses.Enqueue(Hex.Bytes("18"));
        Aht20 aht = new Aht20(sim);
        Check.True("aht20 begin", aht.Begin());
        Check.True("aht20 init", Hex.Wrote(sim, "BE 08 00"));

        // One busy reply, then data: S_RH 0x6E35A -> 43.0506 %, S_T 0x5A3C7 -> 20.4969 C.
        sim.Responses.Enqueue(Hex.Bytes("9C 00 00 00 00 00 00"));
        sim.Responses.Enqueue(Hex.Bytes("1C 6E 35 A5 A3 C7 CB"));
        aht.Measure();
        Check.True("aht20 trigger", Hex.Wrote(sim, "AC 33 00"));
        Check.Equal("aht20 raw rh", 0x6E35A, aht.RawHumidity);
        Check.Equal("aht20 raw t", 0x5A3C7, aht.RawTemperature);
        Check.Near("aht20 humidity", 43.050575, aht.HumidityPercent, 0.0005);
        Check.Near("aht20 temperature", 20.496941, aht.TemperatureCelsius, 0.0005);

        // 50 %RH / 25 C exactly: 0x80000 and 0x60000, CRC 0x4E.
        sim.Responses.Enqueue(Hex.Bytes("1C 80 00 06 00 00 4E"));
        aht.Measure();
        Check.Near("aht20 50%", 50.0, aht.HumidityPercent, 0.0001);
        Check.Near("aht20 25C", 25.0, aht.TemperatureCelsius, 0.0001);

        sim.Responses.Enqueue(Hex.Bytes("1C 80 00 06 00 00 4F"));
        bool threw1 = false;
        try
        {
            aht.Measure();
        }
        catch (Exception)
        {
            threw1 = true;
        }
        Check.True("aht20 bad crc", threw1);

        // AHT10 / DHT10: init 0xE1, 6-byte reply without CRC.
        SimI2cDevice s10 = new SimI2cDevice(0x38);
        s10.Responses.Enqueue(Hex.Bytes("00"));
        s10.Responses.Enqueue(Hex.Bytes("08"));
        Dht10 dht = new Dht10(s10);
        Check.True("dht10 begin", dht.Begin());
        Check.True("dht10 init e1", Hex.Wrote(s10, "E1 08 00"));
        s10.Responses.Enqueue(Hex.Bytes("1C 80 00 06 00 00"));
        dht.Measure();
        Check.Near("dht10 humidity", 50.0, dht.HumidityPercent, 0.0001);
        Check.Near("dht10 temperature", 25.0, dht.TemperatureCelsius, 0.0001);
    }
}
