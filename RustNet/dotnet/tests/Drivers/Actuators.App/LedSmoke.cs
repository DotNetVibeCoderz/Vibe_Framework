using RustNet.Community.Apa102;
using RustNet.Community.Bus;
using RustNet.Community.Led;
using RustNet.Community.Pca9633;
using RustNet.Community.Ws2812;

namespace DriversApp;

public static class LedSmoke
{
    public static void RunLed()
    {
        SimDigitalOutput pin = new SimDigitalOutput();
        Led led = new Led(pin, true);
        led.On();
        Check.True("led active-low on drives low", !pin.State);
        led.Toggle();
        Check.True("led toggle off drives high", pin.State && !led.IsOn);
        List<int> sleeps = Expect.RecordSleeps();
        led.Blink(2, 100, 50);
        Expect.StopRecordingSleeps();
        Expect.Levels("led blink history (on, toggle, 2 blinks)", "010101", pin.History);
        Expect.Duties("led blink timing", "100,50,100,50", sleeps);
    }

    /// <summary>Gamma 2.2: 0.5^2.2 = 0.21764 -> 2176; 0.25^2.2 -> 474 (python).</summary>
    public static void RunPwmLed()
    {
        SimPwmOutput pwm = new SimPwmOutput(1000);
        PwmLed led = new PwmLed(pwm, false);
        led.SetBrightness(0.5);
        Check.Equal("pwmled 50% gamma", 2176, pwm.Duty);
        led.SetBrightness(0.25);
        Check.Equal("pwmled 25% gamma", 474, pwm.Duty);
        led.On();
        Check.Equal("pwmled on", 10000, pwm.Duty);
        led.GammaExponent = 1.0;
        led.SetBrightness(0.5);
        Check.Equal("pwmled linear", 5000, pwm.Duty);

        SimPwmOutput sink = new SimPwmOutput(1000);
        PwmLed low = new PwmLed(sink, true);
        low.SetBrightness(0.5);
        Check.Equal("pwmled active-low inverted", 7824, sink.Duty);
    }

    public static void RunRgbLed()
    {
        SimDigitalOutput r = new SimDigitalOutput();
        SimDigitalOutput g = new SimDigitalOutput();
        SimDigitalOutput b = new SimDigitalOutput();
        RgbLed anode = new RgbLed(r, g, b, true);
        anode.SetColor(true, false, true);
        Check.True("rgbled common anode magenta sinks R,B", !r.State && g.State && !b.State);
        anode.Off();
        Check.True("rgbled common anode off = all high", r.State && g.State && b.State);

        RgbLed cathode = new RgbLed(r, g, b, false);
        cathode.SetColor(true, false, true);
        Check.True("rgbled common cathode magenta drives R,B", r.State && !g.State && b.State);
    }

    /// <summary>HSV references from python colorsys.</summary>
    public static void RunRgbPwmLed()
    {
        SimPwmOutput r = new SimPwmOutput(1000);
        SimPwmOutput g = new SimPwmOutput(1000);
        SimPwmOutput b = new SimPwmOutput(1000);
        RgbPwmLed led = new RgbPwmLed(r, g, b, false);
        led.SetColor(255, 128, 0);
        Check.Equal("rgbpwm red", 10000, r.Duty);
        Check.Equal("rgbpwm green 128/255", 5020, g.Duty);
        Check.Equal("rgbpwm blue", 0, b.Duty);

        led.CommonAnode = true;
        led.SetColor(255, 128, 0);
        Check.True("rgbpwm common anode inverted", r.Duty == 0 && g.Duty == 4980 && b.Duty == 10000);
        led.CommonAnode = false;

        led.SetHsv(120, 1, 1);
        Check.True("hsv 120 = green", led.Red == 0 && led.Green == 255 && led.Blue == 0);
        led.SetHsv(30, 1, 1);
        Check.True("hsv 30 = orange", led.Red == 255 && led.Green == 128 && led.Blue == 0);
        led.SetHsv(200, 0.5, 0.8);
        Check.Equal("hsv 200/.5/.8 r", 102, led.Red);
        Check.Equal("hsv 200/.5/.8 g", 170, led.Green);
        Check.Equal("hsv 200/.5/.8 b", 204, led.Blue);
        led.SetHsv(300, 0.25, 0.6);
        Check.True("hsv 300/.25/.6", led.Red == 153 && led.Green == 115 && led.Blue == 153);
        led.SetHsv(-60, 1, 1);
        Check.True("hsv -60 wraps to magenta", led.Red == 255 && led.Green == 0 && led.Blue == 255);
    }

    public static void RunLedBarGraph()
    {
        SimDigitalOutput[] pins = new SimDigitalOutput[10];
        Led[] leds = new Led[10];
        for (int i = 0; i < 10; i++)
        {
            pins[i] = new SimDigitalOutput();
            leds[i] = new Led(pins[i], false);
        }
        LedBarGraph bar = new LedBarGraph(leds);
        bar.SetPercentage(0.35);
        Check.Equal("bargraph 35% of 10 rounds to 4", 4, bar.Lit);
        Check.True("bargraph led 3 on, 4 off", pins[3].State && !pins[4].State);
        bar.SetLevel(2);
        Check.Equal("bargraph unchanged LED not rewritten", 1, pins[0].History.Count);
        Expect.Levels("bargraph led 3 history", "10", pins[3].History);
        Check.Equal("bargraph untouched LED", 0, pins[9].History.Count);
        bar.SetDot(7);
        Check.True("bargraph dot", pins[7].State && !pins[0].State && !pins[1].State);
    }

    /// <summary>WS2812 at 2.4 MHz: bit 1 = 110, bit 0 = 100; 0xFF -> DB 6D B6, 0x00 -> 92 49 24, GRB order.</summary>
    public static void RunWs2812()
    {
        SimSpiDevice spi = Expect.Spi();
        Ws2812 strip = new Ws2812(spi, 2);
        strip.SetPixel(0, 0x12, 0xFF, 0x00);
        strip.SetPixel(1, 0x80, 0x00, 0xA5);
        strip.Show();
        Check.Equal("ws2812 one SPI write", 1, spi.Transactions.Count);
        byte[] f = spi.Transactions[0];
        Check.Equal("ws2812 frame length (2x9 + 90 reset)", 108, f.Length);
        byte[] data = new byte[18];
        for (int i = 0; i < 18; i++)
        {
            data[i] = f[i];
        }
        Expect.Bytes("ws2812 GRB encoding", "DB 6D B6 92 69 34 92 49 24 92 49 24 D2 49 24 D3 49 A6", data);
        bool resetLow = true;
        for (int i = 18; i < f.Length; i++)
        {
            resetLow = resetLow && f[i] == 0;
        }
        Check.True("ws2812 300 us reset latch low", resetLow);
        Check.Equal("ws2812 getpixel", 0x8000A5, strip.GetPixel(1));

        strip.Brightness = 128;
        strip.Fill(0, 255, 0);
        byte[] dim = strip.Encode();
        Check.True("ws2812 brightness 255*128/255 = 0x80 -> D2 49 24", dim[0] == 0xD2 && dim[1] == 0x49 && dim[2] == 0x24);
    }

    /// <summary>APA102: 32 zero bits, per LED 111+5-bit brightness, B, G, R, then 0xFF end frame.</summary>
    public static void RunApa102()
    {
        SimSpiDevice spi = Expect.Spi();
        Apa102 strip = new Apa102(spi, 3);
        strip.SetPixel(0, 1, 2, 3, 31);
        strip.SetPixel(1, 0xFF, 0x00, 0x10, 5);
        strip.Show();
        Expect.LastSpi("apa102 frame", "00 00 00 00 FF 03 02 01 E5 10 00 FF FF 00 00 00 FF FF FF FF", spi);

        Apa102 long40 = new Apa102(spi, 40);
        Check.Equal("apa102 end frame min 4 bytes", 4, long40.EndFrameBytes());
        Apa102 long100 = new Apa102(spi, 100);
        Check.Equal("apa102 end frame n/16 bytes", 7, long100.EndFrameBytes());
        strip.SetPixel(2, 0, 0, 0, 99);
        Check.Equal("apa102 brightness clamped", 0xFF, strip.Encode()[12]);
    }

    public static void RunPca9633()
    {
        SimI2cDevice sim = new SimI2cDevice(0x62);
        Pca9633 p = new Pca9633(sim);
        p.Begin();
        Expect.Write("pca9633 MODE1 wake", "00 01", sim, 0);
        Expect.Write("pca9633 MODE2 totem pole", "01 04", sim, 1);
        Expect.Write("pca9633 LEDOUT all PWM", "08 AA", sim, 2);

        p.SetPwm(2, 0x80);
        Expect.LastWrite("pca9633 PWM2", "04 80", sim);
        p.SetAll(10, 20, 30, 40);
        Expect.LastWrite("pca9633 auto-increment brightness", "A2 0A 14 1E 28", sim);

        int n = sim.Writes.Count;
        p.SetGroupBlink(1.0, 0.5);
        Expect.Write("pca9633 DMBLNK", "01 24", sim, n);
        Expect.Write("pca9633 GRPFREQ 1 s = 23", "07 17", sim, n + 1);
        Expect.Write("pca9633 GRPPWM 50%", "06 80", sim, n + 2);
        Expect.Write("pca9633 LEDOUT group", "08 FF", sim, n + 3);

        n = sim.Writes.Count;
        p.SetGroupDimming(64);
        Expect.Write("pca9633 dimming mode", "01 04", sim, n);
        Expect.Write("pca9633 GRPPWM dim", "06 40", sim, n + 1);

        p.SetOutputMode(1, Pca9633.LedOff);
        Expect.LastWrite("pca9633 LED1 off", "08 F3", sim);
    }
}
