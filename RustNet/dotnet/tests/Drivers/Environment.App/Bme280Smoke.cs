using RustNet.Community.Bme280;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Bme280Smoke
{
    /// <summary>A BMP280 holding the datasheet's worked example (section 3.12): 25.08 °C, 100653.25 Pa.</summary>
    public static SimI2cDevice DatasheetChip()
    {
        SimI2cDevice sim = new SimI2cDevice(0x76);
        sim.SetRegister(0xD0, 0x58);
        int[] cal = new int[12];
        cal[0] = 27504;
        cal[1] = 26435;
        cal[2] = -1000;
        cal[3] = 36477;
        cal[4] = -10685;
        cal[5] = 3024;
        cal[6] = 2855;
        cal[7] = 140;
        cal[8] = -7;
        cal[9] = 15500;
        cal[10] = -14600;
        cal[11] = 6000;
        for (int i = 0; i < 12; i++)
        {
            sim.SetRegister16Le(0x88 + i * 2, cal[i]);
        }
        // adc_P = 415148, adc_T = 519888, as the 20-bit left-justified registers.
        sim.SetRegister(0xF7, 415148 >> 12);
        sim.SetRegister(0xF8, (415148 >> 4) & 0xFF);
        sim.SetRegister(0xF9, (415148 & 0x0F) << 4);
        sim.SetRegister(0xFA, 519888 >> 12);
        sim.SetRegister(0xFB, (519888 >> 4) & 0xFF);
        sim.SetRegister(0xFC, (519888 & 0x0F) << 4);
        return sim;
    }

    public static void Run()
    {
        SimI2cDevice sim = DatasheetChip();
        Bme280 bmp = new Bme280(sim);
        Check.True("bme280 begin", bmp.Begin());
        Check.True("bme280 bmp has no humidity", !bmp.HasHumidity);
        bmp.Measure();
        Check.Equal("bme280 t_fine", 128422, bmp.TFine);
        Check.Equal("bme280 temperature", 2508, bmp.TemperatureCentiCelsius);
        Check.Equal("bme280 pressure", 25767233, bmp.PressureQ24_8);
        byte[]? reset = sim.LastWriteTo(0xE0);
        Check.True("bme280 soft reset sent", reset != null && reset[1] == 0xB6);
        byte[]? meas = sim.LastWriteTo(0xF4);
        Check.True("bme280 forced mode", meas != null && (meas[1] & 0x03) == 0x01);

        SimI2cDevice absent = new SimI2cDevice(0x77);
        Check.True("bme280 wrong chip id refused", !new Bme280(absent).Begin());
    }
}
