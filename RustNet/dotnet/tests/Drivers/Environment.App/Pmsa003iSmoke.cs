using RustNet.Community.Bus;
using RustNet.Community.Pmsa003i;

namespace DriversApp;

public static class Pmsa003iSmoke
{
    // 0x42 0x4D, length 28, PM 12/25/30 (std and env), counts 1500/450/90/12/3/1, reserved 0x9100,
    // checksum = sum of the first 30 bytes = 0x03D0 (independently computed).
    private const string Frame =
        "42 4D 00 1C 00 0C 00 19 00 1E 00 0C 00 19 00 1E 05 DC 01 C2 00 5A 00 0C 00 03 00 01 91 00 03 D0";

    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x12);
        sim.Responses.Enqueue(Hex.Bytes(Frame));
        Pmsa003i pm = new Pmsa003i(sim);
        Check.True("pmsa003i begin", pm.Begin());
        Check.Equal("pmsa003i pm1.0", 12, pm.Pm10Standard);
        Check.Equal("pmsa003i pm2.5", 25, pm.Pm25Standard);
        Check.Equal("pmsa003i pm10", 30, pm.Pm100Standard);
        Check.Equal("pmsa003i pm2.5 env", 25, pm.Pm25Environmental);
        Check.Equal("pmsa003i >0.3um", 1500, pm.Particles03);
        Check.Equal("pmsa003i >0.5um", 450, pm.Particles05);
        Check.Equal("pmsa003i >10um", 1, pm.Particles100);
        Check.Equal("pmsa003i version", 0x91, pm.Version);

        byte[] bad = Hex.Bytes(Frame);
        bad[31] = 0xD1;
        sim.Responses.Enqueue(bad);
        Check.True("pmsa003i bad checksum", !pm.Begin());

        // EPA 2024 PM2.5 breakpoints: 12.0 -> 56, 35.4 -> 100, 55.5 -> 151, beyond 325.4 -> 500.
        Check.Equal("pmsa003i aqi 12", 56, Pmsa003i.AqiFromPm25(12.0));
        Check.Equal("pmsa003i aqi 35.4", 100, Pmsa003i.AqiFromPm25(35.4));
        Check.Equal("pmsa003i aqi 55.5", 151, Pmsa003i.AqiFromPm25(55.5));
        Check.Equal("pmsa003i aqi 400", 500, Pmsa003i.AqiFromPm25(400.0));
    }
}
