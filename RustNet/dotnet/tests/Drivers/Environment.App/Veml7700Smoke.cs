using RustNet.Community.Bus;
using RustNet.Community.Veml7700;

namespace DriversApp;

public static class Veml7700Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x10);
        sim.SetRegister16Le(0x07, 0xC481);
        Veml7700 veml = new Veml7700(sim);
        Check.True("veml7700 begin", veml.Begin());
        // Gain 1/8 (code 2, bits 12:11) and 100 ms (code 0): ALS_CONF = 0x1000, written LSB first.
        Check.True("veml7700 als_conf", Hex.Wrote(sim, "00 00 10"));
        // 0.0042 * (800/100) * (2/0.125) = 0.5376 lx/count -> 537.6 lx.
        Check.Near("veml7700 resolution", 0.5376, veml.Resolution, 0.000001);
        // 16-bit registers: the byte-addressed register file cannot hold ALS (0x04) and WHITE (0x05) side by side,
        // so their reads are served from the reply queue: ALS = 1000, WHITE = 1500.
        sim.Responses.Enqueue(Hex.Bytes("E8 03"));
        sim.Responses.Enqueue(Hex.Bytes("DC 05"));
        Check.Near("veml7700 lux", 537.6, veml.ReadLux(), 0.0001);
        Check.Equal("veml7700 white", 1500, veml.White);
        Check.Near("veml7700 corrected", 560.97853, Veml7700.CorrectHighLux(537.6), 0.001);
        veml.Configure(Veml7700.Gain1, Veml7700.It100ms);
        sim.Responses.Enqueue(Hex.Bytes("E8 03"));
        sim.Responses.Enqueue(Hex.Bytes("DC 05"));
        Check.Near("veml7700 gain1 lux", 67.2, veml.ReadLux(), 0.0001);
        veml.Configure(Veml7700.Gain2, Veml7700.It800ms);
        Check.Near("veml7700 max resolution", 0.0042, veml.Resolution, 0.0000001);
    }
}
