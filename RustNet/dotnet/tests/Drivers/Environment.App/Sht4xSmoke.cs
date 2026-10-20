using RustNet.Community.Bus;
using RustNet.Community.Sht4x;

namespace DriversApp;

public static class Sht4xSmoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x44);
        sim.Responses.Enqueue(Hex.Bytes("12 34 37 56 78 7D"));
        Sht4x sht = new Sht4x(sim);
        Check.True("sht4x begin", sht.Begin());
        Check.Equal("sht4x serial", 0x12345678L, sht.SerialNumber);
        Check.True("sht4x reset cmd", Hex.Wrote(sim, "94"));

        // S_T 0x6666 -> 25.000 C; S_RH 0x8000 -> -6 + 125*32768/65535 = 56.5010 %.
        sim.Responses.Enqueue(Hex.Bytes("66 66 93 80 00 A2"));
        sht.Measure();
        Check.True("sht4x high precision cmd", Hex.Wrote(sim, "FD"));
        Check.Near("sht4x temperature", 25.0, sht.TemperatureCelsius, 0.0005);
        Check.Near("sht4x humidity", 56.500954, sht.HumidityPercent, 0.0005);
        // The RH formula goes below 0 near raw 0 and above 100 near full scale: both clip.
        Check.Near("sht4x rh clip low", 0.0, Sht4x.ConvertHumidity(0), 0.0001);
        Check.Near("sht4x rh clip high", 100.0, Sht4x.ConvertHumidity(65535), 0.0001);

        sim.Responses.Enqueue(Hex.Bytes("66 66 93 00 00 81"));
        sht.MeasureWithHeater(Sht4x.Heater20mW100ms);
        Check.True("sht4x heater cmd", Hex.Wrote(sim, "15"));
        Check.Near("sht4x heater rh clipped", 0.0, sht.HumidityPercent, 0.0001);

        SimI2cDevice bad = new SimI2cDevice(0x44);
        bad.Responses.Enqueue(Hex.Bytes("12 34 00 56 78 7D"));
        Check.True("sht4x bad serial crc", !new Sht4x(bad).Begin());
    }
}
