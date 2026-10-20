using RustNet.Community.Bus;
using RustNet.Community.Ccs811;

namespace DriversApp;

public static class Ccs811Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x5A);
        sim.Responses.Enqueue(Hex.Bytes("81")); // HW_ID
        sim.Responses.Enqueue(Hex.Bytes("10")); // STATUS: APP_VALID, boot mode
        sim.Responses.Enqueue(Hex.Bytes("90")); // STATUS: FW_MODE | APP_VALID
        Ccs811 ccs = new Ccs811(sim);
        Check.True("ccs811 begin", ccs.Begin(Ccs811.ModeEverySecond));
        Check.True("ccs811 sw reset sequence", Hex.Wrote(sim, "FF 11 E5 72 8A"));
        Check.True("ccs811 app start", Hex.Wrote(sim, "F4"));
        Check.True("ccs811 drive mode 1", Hex.Wrote(sim, "01 10"));

        // eCO2 1234 ppm, TVOC 56 ppb, status 0x98, no error, raw: 20 uA, ADC 500 -> (20<<10)|500 = 0x51F4.
        sim.Responses.Enqueue(Hex.Bytes("98"));
        sim.Responses.Enqueue(Hex.Bytes("04 D2 00 38 98 00 51 F4"));
        Check.True("ccs811 data ready", ccs.DataReady());
        ccs.ReadData();
        Check.Equal("ccs811 eco2", 1234, ccs.ECo2Ppm);
        Check.Equal("ccs811 tvoc", 56, ccs.TvocPpb);
        Check.Equal("ccs811 current", 20, ccs.CurrentMicroamps);
        Check.Equal("ccs811 raw adc", 500, ccs.RawAdc);

        // Datasheet ENV_DATA: 48.5 %RH -> 0x6100; temperature stored with +25 offset: 23.25 C -> 48.25*512 = 0x6080.
        ccs.SetEnvironmentalData(48.5, 23.25);
        Check.True("ccs811 env data", Hex.Wrote(sim, "05 61 00 60 80"));
        ccs.WriteBaseline(0x847B);
        Check.True("ccs811 baseline", Hex.Wrote(sim, "11 84 7B"));

        sim.Responses.Enqueue(Hex.Bytes("01 90 00 00 99 05 00 00"));
        bool threw = false;
        try
        {
            ccs.ReadData();
        }
        catch (Exception)
        {
            threw = true;
        }
        Check.True("ccs811 error flagged", threw);
        Check.Equal("ccs811 error id", 5, ccs.ErrorId);

        SimI2cDevice noApp = new SimI2cDevice(0x5A);
        noApp.Responses.Enqueue(Hex.Bytes("81"));
        noApp.Responses.Enqueue(Hex.Bytes("00"));
        Check.True("ccs811 no app refused", !new Ccs811(noApp).Begin(1));
    }
}
