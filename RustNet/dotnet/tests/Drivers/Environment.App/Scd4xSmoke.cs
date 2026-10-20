using RustNet.Community.Bus;
using RustNet.Community.Scd4x;

namespace DriversApp;

public static class Scd4xSmoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x62);
        sim.Responses.Enqueue(Hex.Bytes("00 00 81 01 90 4C 00 42 DE"));
        Scd4x scd = new Scd4x(sim);
        Check.True("scd4x begin", scd.Begin());
        Check.True("scd4x stop periodic", Hex.Wrote(sim, "3F 86"));
        Check.True("scd4x serial cmd", Hex.Wrote(sim, "36 82"));
        Check.Equal("scd4x serial", 0x000001900042L, scd.SerialNumber);

        scd.StartPeriodic();
        Check.True("scd4x start periodic", Hex.Wrote(sim, "21 B1"));
        sim.Responses.Enqueue(Hex.Bytes("00 01 B0"));
        Check.True("scd4x data ready", scd.DataReady());

        // Datasheet read_measurement example: 0x01F4 (CRC 0x33) = 500 ppm, 0x6667 (0xA2) = 25 C, 0x5EB9 (0x3C) = 37 %RH.
        sim.Responses.Enqueue(Hex.Bytes("01 F4 33 66 67 A2 5E B9 3C"));
        scd.ReadMeasurement();
        Check.True("scd4x read cmd", Hex.Wrote(sim, "EC 05"));
        Check.Equal("scd4x co2", 500, scd.Co2Ppm);
        Check.Near("scd4x temperature", 25.002670, scd.TemperatureCelsius, 0.0001);
        Check.Near("scd4x humidity", 37.001602, scd.HumidityPercent, 0.0001);

        // Datasheet examples: temperature offset 5.4 C -> 0x07E6 CRC 0x48; altitude 1950 m -> 0x079E CRC 0x09.
        scd.SetTemperatureOffset(5.4);
        Check.True("scd4x temp offset", Hex.Wrote(sim, "24 1D 07 E6 48"));
        scd.SetSensorAltitude(1950);
        Check.True("scd4x altitude", Hex.Wrote(sim, "24 27 07 9E 09"));
        // 98700 Pa -> 987 hPa = 0x03DB, CRC 0x42.
        scd.SetAmbientPressure(98700);
        Check.True("scd4x ambient pressure", Hex.Wrote(sim, "E0 00 03 DB 42"));

        // FRC response 0x7FFF (CRC 0x8F) -> correction -1 ppm; 0xFFFF means failure.
        sim.Responses.Enqueue(Hex.Bytes("7F FF 8F"));
        Check.Equal("scd4x frc correction", -1, scd.ForcedRecalibration(400));
        Check.True("scd4x frc cmd", Hex.Wrote(sim, "36 2F 01 90 4C"));

        sim.Responses.Enqueue(Hex.Bytes("00 00 81"));
        Check.True("scd4x self test", scd.SelfTest());

        SimI2cDevice bad = new SimI2cDevice(0x62);
        bad.Responses.Enqueue(Hex.Bytes("00 00 80 01 90 4C 00 42 DE"));
        Check.True("scd4x bad crc", !new Scd4x(bad).Begin());
    }
}
