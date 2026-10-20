using RustNet.Community.Bus;
using RustNet.Community.Scd30;

namespace DriversApp;

public static class Scd30Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x61);
        sim.Responses.Enqueue(Hex.Bytes("03 42 F3")); // firmware 3.66
        Scd30 scd = new Scd30(sim);
        Check.True("scd30 begin", scd.Begin());
        Check.Equal("scd30 firmware", 0x0342, scd.FirmwareVersion);
        Check.True("scd30 fw cmd", Hex.Wrote(sim, "D1 00"));

        // Interface description example: start continuous with 1013 mbar -> 0x0010 0x03F5 CRC 0xDB.
        scd.StartContinuous(1013);
        Check.True("scd30 start continuous", Hex.Wrote(sim, "00 10 03 F5 DB"));
        scd.SetMeasurementInterval(2);
        Check.True("scd30 interval", Hex.Wrote(sim, "46 00 00 02 E3"));

        sim.Responses.Enqueue(Hex.Bytes("00 01 B0"));
        Check.True("scd30 data ready", scd.DataReady());

        // Interface description example frame: CO2 0x43DB8C2E = 439.09 ppm,
        // T 0x41D9E7FF = 27.24 C, RH 0x42431B74 = 48.78 %.
        sim.Responses.Enqueue(Hex.Bytes("43 DB CB 8C 2E 8F 41 D9 70 E7 FF F5 42 43 BF 1B 74 C9"));
        scd.ReadMeasurement();
        Check.True("scd30 read cmd", Hex.Wrote(sim, "03 00"));
        Check.Near("scd30 co2", 439.095154, scd.Co2Ppm, 0.0001);
        Check.Near("scd30 temperature", 27.238279, scd.TemperatureCelsius, 0.0001);
        Check.Near("scd30 humidity", 48.776810, scd.HumidityPercent, 0.0001);
        // Built at run time: a constant like 0xC1480000L compiles to ldc.i4 + conv.u8.
        long negHigh = 0xC148;
        Check.Near("scd30 negative float", -12.5, Scd30.SingleFromBits(negHigh << 16), 0.0000001);
        Check.Near("scd30 zero float", 0.0, Scd30.SingleFromBits(0), 0.0000001);

        sim.Responses.Enqueue(Hex.Bytes("43 DB CB 8C 2E 8F 41 D9 71 E7 FF F5 42 43 BF 1B 74 C9"));
        bool threw = false;
        try
        {
            scd.ReadMeasurement();
        }
        catch (Exception)
        {
            threw = true;
        }
        Check.True("scd30 bad crc", threw);

        scd.SetTemperatureOffset(1.5);
        Check.True("scd30 temp offset 150", Hex.Wrote(sim, "54 03 00 96 1E"));
    }
}
