using RustNet.Community.Bus;
using RustNet.Community.Sht3x;

namespace DriversApp;

public static class Sht3xSmoke
{
    public static void Run()
    {
        // Datasheet 4.12: CRC of 0xBEEF is 0x92.
        Check.Equal("sht3x crc beef", 0x92, Sht3x.Crc(0xBE, 0xEF));

        SimI2cDevice sim = new SimI2cDevice(0x44);
        sim.Responses.Enqueue(Hex.Bytes("80 10 E1")); // status after reset: alert pending, reset detected
        Sht3x sht = new Sht3x(sim);
        Check.True("sht3x begin", sht.Begin());
        Check.True("sht3x soft reset", Hex.Wrote(sim, "30 A2"));

        // S_T = 0x6666 -> -45 + 175*26214/65535 = 25.000 C; S_RH = 0x8000 -> 50.0008 %.
        sim.Responses.Enqueue(Hex.Bytes("66 66 93 80 00 A2"));
        sht.Measure();
        Check.True("sht3x single shot high", Hex.Wrote(sim, "24 00"));
        Check.Equal("sht3x raw t", 0x6666, sht.RawTemperature);
        Check.Near("sht3x temperature", 25.0, sht.TemperatureCelsius, 0.0005);
        Check.Near("sht3x humidity", 50.000763, sht.HumidityPercent, 0.0005);
        Check.Near("sht3x 0 raw", -45.0, Sht3x.ConvertTemperature(0), 0.0001);
        Check.Near("sht3x full scale", 130.0, Sht3x.ConvertTemperature(65535), 0.0001);

        sim.Responses.Enqueue(Hex.Bytes("20 00 5D"));
        Check.True("sht3x heater status bit", sht.IsHeaterOn());
        sht.SetHeater(false);
        Check.True("sht3x heater off cmd", Hex.Wrote(sim, "30 66"));

        sim.Responses.Enqueue(Hex.Bytes("66 66 94 80 00 A2"));
        bool threw1 = false;
        try
        {
            sht.Measure();
        }
        catch (Exception)
        {
            threw1 = true;
        }
        Check.True("sht3x bad crc rejected", threw1);

        SimI2cDevice absent = new SimI2cDevice(0x45);
        absent.Present = false;
        Check.True("sht3x absent", !new Sht3x(absent).Begin());
    }
}
