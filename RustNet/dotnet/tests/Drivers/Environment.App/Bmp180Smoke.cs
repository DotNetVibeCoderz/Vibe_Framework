using RustNet.Community.Bmp180;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Bmp180Smoke
{
    public static void Run()
    {
        // Datasheet 3.5 worked example: calibration, UT = 27898, UP = 23843 (oss 0) -> 15.0 C, 69964 Pa.
        SimI2cDevice sim = new SimI2cDevice(0x77);
        sim.SetRegister(0xD0, 0x55);
        int[] cal = new int[11];
        cal[0] = 408;
        cal[1] = -72;
        cal[2] = -14383;
        cal[3] = 32741;
        cal[4] = 32757;
        cal[5] = 23153;
        cal[6] = 6190;
        cal[7] = 4;
        cal[8] = -32768;
        cal[9] = -8711;
        cal[10] = 2868;
        for (int i = 0; i < 11; i++)
        {
            sim.SetRegister16(0xAA + i * 2, cal[i]);
        }
        Bmp180 bmp = new Bmp180(sim);
        Check.True("bmp180 begin", bmp.Begin());
        Check.Equal("bmp180 ac4 unsigned", 32741, bmp.AC4);
        Check.Equal("bmp180 mb signed", -32768, bmp.MB);

        bmp.Oversampling = Bmp180.UltraLowPower;
        sim.Responses.Enqueue(Hex.Bytes("6C FA"));     // UT = 27898
        sim.Responses.Enqueue(Hex.Bytes("5D 23 00"));  // UP = 23843 << 8 (oss 0)
        bmp.Measure();
        Check.True("bmp180 temp command", Hex.Wrote(sim, "F4 2E"));
        Check.True("bmp180 pressure command", Hex.Wrote(sim, "F4 34"));
        Check.Equal("bmp180 ut", 27898, bmp.RawTemperature);
        Check.Equal("bmp180 up", 23843, bmp.RawPressure);
        Check.Equal("bmp180 temperature", 150, bmp.TemperatureDeciCelsius);
        Check.Equal("bmp180 pressure", 69964, bmp.PressurePascal);
        // 44330 * (1 - (69964/101325)^(1/5.255)) = 3016.66 m.
        Check.Near("bmp180 altitude", 3016.659, bmp.AltitudeMeters(101325.0), 0.01);

        SimI2cDevice other = new SimI2cDevice(0x77);
        other.SetRegister(0xD0, 0x58);
        Check.True("bmp180 wrong id", !new Bmp180(other).Begin());
    }
}
