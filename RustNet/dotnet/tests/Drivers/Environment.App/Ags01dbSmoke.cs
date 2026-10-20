using RustNet.Community.Ags01db;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Ags01dbSmoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x11);
        sim.Responses.Enqueue(Hex.Bytes("0B 46")); // version 0x0B, CRC-8(0xFF init) = 0x46
        Ags01db ags = new Ags01db(sim);
        Check.True("ags01db begin", ags.Begin());
        Check.Equal("ags01db version", 0x0B, ags.Version);
        Check.True("ags01db version cmd", Hex.Wrote(sim, "0A 01"));

        // 125 x 0.1 ppm = 12.5 ppm, CRC 0x35.
        sim.Responses.Enqueue(Hex.Bytes("00 7D 35"));
        Check.Near("ags01db voc", 12.5, ags.ReadVocPpm(), 0.0001);
        Check.True("ags01db read cmd", Hex.Wrote(sim, "00 02"));

        sim.Responses.Enqueue(Hex.Bytes("00 7D 36"));
        bool threw = false;
        try
        {
            ags.ReadVocPpm();
        }
        catch (Exception)
        {
            threw = true;
        }
        Check.True("ags01db bad crc", threw);
    }
}
