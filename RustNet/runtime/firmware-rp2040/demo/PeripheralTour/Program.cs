using System.Text;
using RustNet.Core;
using RustNet.Hal;
using RustNet.IO;
using RustNet.Sys;
using RustNet.Threading;

namespace PeripheralTour;

/// <summary>Board facts the firmware knows and the app does not.</summary>
internal static class Board
{
    /// <summary>The user LED's GPIO: 25 on a Pico. A host that does not know
    /// this call answers 0, which is how the tour tells a dry run.</summary>
    [InternalCall]
    public static int UserLed() => throw new RuntimeOnlyException();
}

/// <summary>
/// Exercises every managed peripheral API the RP2040 port binds and logs a
/// line per check, for the first pass on real hardware.
///
/// Wiring (see runtime/firmware-rp2040/README.md, "On-hardware checklist"):
///   GP15 --[1 kOhm]-- GP14   PWM source into Signal capture / pulse feedback
///   GP19 ------------ GP16   SPI0 MOSI into MISO
///   GP8  ------------ GP9    UART1 TX into RX
///   GP4/GP5                  I2C0 SDA/SCL, optional sensor (BME280, MPU6050, SSD1306...)
///   GP26                     ADC0, optional: to 3V3, GND or a potentiometer wiper
/// Nothing wired is not a crash: the loopback checks report FAIL and say what
/// they expected.
/// </summary>
internal static class Program
{
    private const int PwmSourcePin = 15;
    private const int CapturePin = 14;
    private const int SpiBus = 0;
    private const int UartPort = 1;
    private const int I2cBus = 0;

    private static int _passed;
    private static int _failed;
    private static int _skipped;

    /// <summary>False under a host that is not the RP2040 firmware (e.g. the
    /// run_rnx dev host), which answers byte[]-returning HAL calls with
    /// nothing; those checks are skipped there instead of faulting.</summary>
    private static bool _onDevice;

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }

        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}: {detail}");
    }

    private static void Fail(string what, Exception ex)
    {
        _failed++;
        Console.WriteLine($"  [FAIL] {what}: threw {ex.Message}");
    }

    private static bool DeviceOnly(string what)
    {
        if (_onDevice)
        {
            return true;
        }

        _skipped++;
        Console.WriteLine($"  [skip] {what}: needs the RP2040 firmware");
        return false;
    }

    private static string Hex(int value)
    {
        string digits = "0123456789ABCDEF";
        StringBuilder sb = new StringBuilder();
        sb.Append("0x");
        sb.Append(digits[(value >> 4) & 0xF]);
        sb.Append(digits[value & 0xF]);
        return sb.ToString();
    }

    private static bool Near(int value, int target, int tolerance)
    {
        return value >= target - tolerance && value <= target + tolerance;
    }

    private static void Main()
    {
        int led = Board.UserLed();
        _onDevice = led != 0;
        Console.WriteLine("RustNet RP2040 peripheral tour");
        if (!_onDevice)
        {
            Console.WriteLine("(dry run: not on the RP2040 firmware; device-only checks are skipped)");
            led = 25;
        }

        Adc();
        Pwm(led);
        Signals(led);
        Spi();
        Uart();
        I2c();
        Files();

        Console.WriteLine($"passed {_passed}, failed {_failed}, skipped {_skipped}");
        if (_onDevice)
        {
            Report(led);
        }
    }

    // ---- ADC ---------------------------------------------------------------

    private static void Adc()
    {
        Console.WriteLine("ADC");
        try
        {
            int raw = RustNet.Hal.Adc.ReadRaw(4);
            int mv = RustNet.Hal.Adc.ReadMillivolts(4);
            // Datasheet 4.9.5: 0.706 V at 27 C, -1.721 mV per degree.
            int tenths = 270 - (mv - 706) * 10000 / 1721;
            bool plausible = raw > 300 && raw < 1500 && tenths > -100 && tenths < 700;
            Check("temperature sensor (ch 4)", plausible || !_onDevice,
                $"raw {raw}, {mv} mV, {tenths / 10}.{Math.Abs(tenths % 10)} C");
        }
        catch (Exception ex)
        {
            Fail("temperature sensor (ch 4)", ex);
        }

        try
        {
            int mv = RustNet.Hal.Adc.ReadMillivolts(0);
            Check("ADC0 on GP26", mv >= 0 && mv <= 3300, $"{mv} mV (3V3 ~3300, GND ~0)");
        }
        catch (Exception ex)
        {
            Fail("ADC0 on GP26", ex);
        }

        if (DeviceOnly("ADC channel 5 is refused"))
        {
            bool threw = false;
            try
            {
                RustNet.Hal.Adc.ReadRaw(5);
            }
            catch (Exception)
            {
                threw = true;
            }

            Check("ADC channel 5 is refused", threw, threw ? "threw" : "returned a reading");
        }
    }

    // ---- PWM ---------------------------------------------------------------

    private static void Pwm(int led)
    {
        Console.WriteLine("PWM");
        try
        {
            // Breathe the LED: up and down over about two seconds.
            for (int step = 0; step <= 20; step++)
            {
                int duty = step <= 10 ? step * 1000 : (20 - step) * 1000;
                RustNet.Hal.Pwm.Configure(led, 1000, duty);
                Sleep.Ms(100);
            }

            Check("LED breathes on GP25 (watch it)", true, "1 kHz, duty 0..100..0 %");
        }
        catch (Exception ex)
        {
            Fail("LED PWM", ex);
        }

        try
        {
            // The loopback source for the Signal checks: 1 kHz, 25 % -> 250 us high.
            RustNet.Hal.Pwm.Configure(PwmSourcePin, 1000, 2500);
            Check("PWM source on GP15", true, "1 kHz, 25 %");
        }
        catch (Exception ex)
        {
            Fail("PWM source on GP15", ex);
        }

        if (DeviceOnly("a 1 Hz PWM is refused"))
        {
            bool threw = false;
            try
            {
                RustNet.Hal.Pwm.Configure(PwmSourcePin + 2, 1, 5000);
            }
            catch (Exception)
            {
                threw = true;
            }

            Check("a 1 Hz PWM is refused", threw, threw ? "threw (slowest is ~8 Hz)" : "accepted");
        }
    }

    // ---- Signal (PIO) ------------------------------------------------------

    private static void Signals(int led)
    {
        Console.WriteLine("Signal");
        if (DeviceOnly("capture GP14 (PWM via GP15)"))
        {
            try
            {
                int[] widths = Signal.Capture(CapturePin, 8, 20000);
                StringBuilder seen = new StringBuilder();
                int good = 0;
                for (int i = 0; i < widths.Length; i++)
                {
                    seen.Append(widths[i]);
                    seen.Append(' ');
                    if (Near(widths[i], 250, 3) || Near(widths[i], 750, 3))
                    {
                        good++;
                    }
                }

                Check("capture GP14 (PWM via GP15)", widths.Length == 8 && good == 8,
                    $"{widths.Length} widths: {seen}(want 250/750 us alternating)");
            }
            catch (Exception ex)
            {
                Fail("capture GP14", ex);
            }
        }

        if (DeviceOnly("pulse feedback on GP14"))
        {
            try
            {
                int echo = Signal.PulseFeedback(CapturePin, true, 10, 20000);
                Check("pulse feedback on GP14", Near(echo, 250, 3), $"echo {echo} us (want 250, the PWM high time)");
            }
            catch (Exception ex)
            {
                Fail("pulse feedback on GP14", ex);
            }

            try
            {
                // Nothing on GP13: a pulse with no echo times out as 0, not a hang.
                int none = Signal.PulseFeedback(13, true, 10, 5000);
                Check("pulse feedback with no echo", none == 0, $"{none} us (want 0)");
            }
            catch (Exception ex)
            {
                Fail("pulse feedback with no echo", ex);
            }
        }

        try
        {
            // Three 150 ms flashes on the LED; the train is blocking, so the
            // wall-clock time of the call is the sum of the timings.
            int[] timings = new int[5];
            for (int i = 0; i < timings.Length; i++)
            {
                timings[i] = 150000;
            }

            long t0 = Uptime.Ms();
            Signal.Generate(led, true, timings);
            long elapsed = Uptime.Ms() - t0;
            if (_onDevice)
            {
                Check("generate 3 flashes on GP25", elapsed >= 749 && elapsed <= 770, $"{elapsed} ms (want 750)");
            }
            else
            {
                Check("generate 3 flashes on GP25", true, "called");
            }
        }
        catch (Exception ex)
        {
            Fail("generate on GP25", ex);
        }
    }

    // ---- SPI ---------------------------------------------------------------

    private static void Spi()
    {
        Console.WriteLine("SPI");
        byte[] pattern = new byte[16];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 17 + 3);
        }

        for (int mode = 0; mode < 4; mode++)
        {
            string what = $"SPI0 loopback GP19->GP16, mode {mode}";
            try
            {
                RustNet.Hal.Spi.Configure(SpiBus, 1000000, mode);
                if (!DeviceOnly(what))
                {
                    continue;
                }

                byte[] back = RustNet.Hal.Spi.Transfer(SpiBus, pattern);
                int same = 0;
                for (int i = 0; i < back.Length && i < pattern.Length; i++)
                {
                    if (back[i] == pattern[i])
                    {
                        same++;
                    }
                }

                Check(what, back.Length == pattern.Length && same == pattern.Length,
                    $"{same}/{pattern.Length} bytes echoed, first {Hex(back.Length > 0 ? back[0] : 0)}");
            }
            catch (Exception ex)
            {
                Fail(what, ex);
            }
        }

        try
        {
            RustNet.Hal.Spi.Configure(SpiBus, 8000000, 0);
            RustNet.Hal.Spi.Write(SpiBus, pattern);
            if (DeviceOnly("SPI0 read at 8 MHz"))
            {
                byte[] zeros = RustNet.Hal.Spi.Read(SpiBus, 4);
                // Read sends zeros, so the loopback reads zeros back.
                Check("SPI0 write + read at 8 MHz", zeros.Length == 4 && zeros[0] == 0 && zeros[3] == 0,
                    $"{zeros.Length} bytes");
            }
        }
        catch (Exception ex)
        {
            Fail("SPI0 at 8 MHz", ex);
        }
    }

    // ---- UART --------------------------------------------------------------

    private static void Uart()
    {
        Console.WriteLine("UART");
        try
        {
            RustNet.Hal.Uart.Configure(UartPort, 115200);
            if (DeviceOnly("UART1 loopback GP8->GP9"))
            {
                while (RustNet.Hal.Uart.Available(UartPort) > 0)
                {
                    RustNet.Hal.Uart.Read(UartPort, 32);
                }

                string sent = "RustNet!";
                byte[] data = Encoding.UTF8.GetBytes(sent);
                RustNet.Hal.Uart.Write(UartPort, data);
                Sleep.Ms(5);
                byte[] back = RustNet.Hal.Uart.Read(UartPort, 32);
                string got = Encoding.UTF8.GetString(back);
                Check("UART1 loopback GP8->GP9", got == sent, $"sent '{sent}', got '{got}'");
            }
        }
        catch (Exception ex)
        {
            Fail("UART1", ex);
        }

        if (DeviceOnly("UART0 (console) is refused"))
        {
            bool threw = false;
            try
            {
                RustNet.Hal.Uart.Configure(0, 9600);
            }
            catch (Exception)
            {
                threw = true;
            }

            Check("UART0 (console) is refused", threw, threw ? "threw" : "reconfigured the console");
        }
    }

    // ---- I2C ---------------------------------------------------------------

    private static bool Probe(int address)
    {
        try
        {
            RustNet.Hal.I2c.Read(I2cBus, address, 1);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int ReadRegister(int address, int register)
    {
        byte[] reg = new byte[1];
        reg[0] = (byte)register;
        RustNet.Hal.I2c.Write(I2cBus, address, reg);
        byte[] value = RustNet.Hal.I2c.Read(I2cBus, address, 1);
        return value[0];
    }

    private static void I2c()
    {
        Console.WriteLine("I2C");
        if (!DeviceOnly("I2C0 scan on GP4/GP5"))
        {
            return;
        }

        try
        {
            StringBuilder found = new StringBuilder();
            int count = 0;
            int absent = 0;
            for (int a = 0x08; a <= 0x77; a++)
            {
                if (Probe(a))
                {
                    count++;
                    found.Append(Hex(a));
                    found.Append(' ');
                }
                else
                {
                    absent++;
                }
            }

            // The scan itself is the NACK test: every empty address must come
            // back as an exception, quickly, rather than as a hang or a "yes".
            Check("I2C0 scan, NACK is an error", absent > 0 && count < 112,
                $"{count} device(s): {found}");

            if (Probe(0x76) || Probe(0x77))
            {
                int addr = Probe(0x76) ? 0x76 : 0x77;
                int id = ReadRegister(addr, 0xD0);
                Check($"BMx280 at {Hex(addr)} chip id", id == 0x58 || id == 0x60,
                    $"{Hex(id)} (0x58 BMP280, 0x60 BME280)");
            }

            if (Probe(0x68))
            {
                int who = ReadRegister(0x68, 0x75);
                Check("MPU-6050 at 0x68 WHO_AM_I", who == 0x68, Hex(who));
            }

            if (count == 0)
            {
                _skipped++;
                Console.WriteLine("  [skip] sensor reads: nothing on I2C0 (pull-ups fitted?)");
            }
        }
        catch (Exception ex)
        {
            Fail("I2C0", ex);
        }
    }

    // ---- FileSystem --------------------------------------------------------

    private static bool Refused(string what, int which)
    {
        try
        {
            if (which == 0)
            {
                FileSystem.WriteAllText("/.sys/evil", "x");
            }
            else if (which == 1)
            {
                FileSystem.ReadAllBytes("/.sys/signing.pub");
            }
            else
            {
                FileSystem.Delete("\\.sys\\app.rnx");
            }
        }
        catch (Exception ex)
        {
            Check(what, true, $"threw: {ex.Message}");
            return true;
        }

        Check(what, false, "was allowed");
        return false;
    }

    private static void Files()
    {
        Console.WriteLine("FileSystem");
        try
        {
            FileSystem.CreateDirectory("/tour");
            FileSystem.WriteAllText("/tour/hello.txt", "hello");
            FileSystem.AppendText("/tour/hello.txt", " pico");
            string text = FileSystem.ReadAllText("/tour/hello.txt");
            Check("write, append, read text", text == "hello pico", $"'{text}'");
            Check("exists", FileSystem.Exists("/tour/hello.txt"), "/tour/hello.txt");

            byte[] data = new byte[16];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(255 - i);
            }

            FileSystem.WriteAllBytes("/tour/data.bin", data);
            if (DeviceOnly("bytes round trip, list, delete, /.sys guard"))
            {
                byte[] back = FileSystem.ReadAllBytes("/tour/data.bin");
                Check("bytes round trip", back.Length == 16 && back[0] == 255 && back[15] == 240,
                    $"{back.Length} bytes");

                string listing = FileSystem.List("/tour");
                Check("list /tour", listing.Contains("hello.txt") && listing.Contains("data.bin"),
                    listing.Replace("\n", ", "));

                FileSystem.Delete("/tour/data.bin");
                FileSystem.Delete("/tour/hello.txt");
                Check("delete", !FileSystem.Exists("/tour/hello.txt"), "gone");

                Refused("/.sys write refused", 0);
                Refused("/.sys read refused", 1);
                Refused("\\.sys delete refused (normalised path)", 2);
                string root = FileSystem.List("/");
                Check("root listing hides .sys", !root.Contains(".sys"), root.Replace("\n", ", "));
            }
        }
        catch (Exception ex)
        {
            Fail("FileSystem", ex);
        }
    }

    // ---- result on the LED ---------------------------------------------------

    /// <summary>A calm 1 Hz pulse means every check passed; otherwise the
    /// failure count is blinked quickly, then a pause.</summary>
    private static void Report(int led)
    {
        Gpio.SetMode(led, PinMode.Output);
        while (true)
        {
            if (_failed == 0)
            {
                Gpio.Write(led, true);
                Sleep.Ms(200);
                Gpio.Write(led, false);
                Sleep.Ms(800);
            }
            else
            {
                for (int i = 0; i < _failed; i++)
                {
                    Gpio.Write(led, true);
                    Sleep.Ms(120);
                    Gpio.Write(led, false);
                    Sleep.Ms(180);
                }

                Sleep.Ms(1500);
            }
        }
    }
}
