using RustNet.Community.Bus;
using RustNet.Community.Sgp40;

namespace DriversApp;

public static class Sgp40Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x59);
        sim.Responses.Enqueue(Hex.Bytes("00 00 81 01 90 4C 00 42 DE"));
        Sgp40 sgp = new Sgp40(sim);
        Check.True("sgp40 begin", sgp.Begin());
        Check.Equal("sgp40 serial", 0x000001900042L, sgp.SerialNumber);

        // Datasheet: without compensation send 0x8000 (+CRC 0xA2) and 0x6666 (+CRC 0x93).
        sim.Responses.Enqueue(Hex.Bytes("6A 2F 92"));
        Check.Equal("sgp40 sraw", 0x6A2F, sgp.MeasureRaw());
        Check.True("sgp40 default frame", Hex.Wrote(sim, "26 0F 80 00 A2 66 66 93"));
        Check.Equal("sgp40 50% ticks", 0x8000, Sgp40.HumidityTicks(50.0));
        Check.Equal("sgp40 25C ticks", 0x6666, Sgp40.TemperatureTicks(25.0));

        // 30 %RH -> 19660.5 -> 0x4CCD (CRC 0xB7); 20 C -> 24341.6 -> 0x5F16 (CRC 0x1A).
        sim.Responses.Enqueue(Hex.Bytes("6A 2F 92"));
        sgp.MeasureRaw(30.0, 20.0);
        Check.True("sgp40 compensated frame", Hex.Wrote(sim, "26 0F 4C CD B7 5F 16 1A"));

        sim.Responses.Enqueue(Hex.Bytes("D4 00 C6"));
        Check.True("sgp40 self test pass", sgp.SelfTest());
        Check.True("sgp40 self test cmd", Hex.Wrote(sim, "28 0E"));
        sim.Responses.Enqueue(Hex.Bytes("4B 00 12"));
        Check.True("sgp40 self test fail", !sgp.SelfTest());
        sgp.HeaterOff();
        Check.True("sgp40 heater off", Hex.Wrote(sim, "36 15"));
    }
}
