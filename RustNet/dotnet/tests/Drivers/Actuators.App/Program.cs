using RustNet.Community.Bus;

namespace DriversApp;

public static class Program
{
    public static void Main()
    {
        Timing.SleepOverride = ms => { };
        Console.WriteLine("drivers starting");
        Check.Run("stepprofile", StepperSmoke.RunProfile);
        Check.Run("hbridgemotor", DcMotorSmoke.RunHBridge);
        Check.Run("bidirectionaldcmotor", DcMotorSmoke.RunBidirectional);
        Check.Run("tb67h420ftg", DcMotorSmoke.RunTb67h420ftg);
        Check.Run("a4988", StepperSmoke.RunA4988);
        Check.Run("uln2003", StepperSmoke.RunUln2003);
        Check.Run("gpiostepper", StepperSmoke.RunGpioStepper);
        Check.Run("servo", ServoSmoke.RunServo);
        Check.Run("esc", ServoSmoke.RunEsc);
        Check.Run("bld510b", IndustrialSmoke.RunBld510b);
        Check.Run("cerusxdrive", IndustrialSmoke.RunCerusXDrive);
        Check.Run("led", LedSmoke.RunLed);
        Check.Run("pwmled", LedSmoke.RunPwmLed);
        Check.Run("rgbled", LedSmoke.RunRgbLed);
        Check.Run("rgbpwmled", LedSmoke.RunRgbPwmLed);
        Check.Run("ledbargraph", LedSmoke.RunLedBarGraph);
        Check.Run("ws2812", LedSmoke.RunWs2812);
        Check.Run("apa102", LedSmoke.RunApa102);
        Check.Run("pca9633", LedSmoke.RunPca9633);
        Check.Run("relay", OutputSmoke.RunRelay);
        Check.Run("piezo", OutputSmoke.RunPiezo);
        Check.Run("ads1115", ConverterSmoke.RunAds1115);
        Check.Run("ads1015", ConverterSmoke.RunAds1015);
        Check.Run("ads7128", ConverterSmoke.RunAds7128);
        Check.Run("mcp3xxx", ConverterSmoke.RunMcp3xxx);
        Check.Run("mcp4725", ConverterSmoke.RunMcp4725);
        Check.Run("mcp49xx", ConverterSmoke.RunMcp49xx);
        Check.Run("mcp23017", ExpanderSmoke.RunMcp23017);
        Check.Run("mcp23008", ExpanderSmoke.RunMcp23008);
        Check.Run("mcp23s17", ExpanderSmoke.RunMcp23s17);
        Check.Run("pcf857x", ExpanderSmoke.RunPcf857x);
        Check.Run("pca95xx", ExpanderSmoke.RunPca95xx);
        Check.Run("ht16k33", ExpanderSmoke.RunHt16k33);
        Check.Run("rtc-dayofweek", RtcSmoke.RunDayOfWeek);
        Check.Run("ds1307", RtcSmoke.RunDs1307);
        Check.Run("ds3231", RtcSmoke.RunDs3231);
        Check.Run("pcf8523", RtcSmoke.RunPcf8523);
        Check.Run("mcp41xx", DigipotSmoke.RunMcp41xx);
        Check.Run("mcp401x", DigipotSmoke.RunMcp401x);
        Check.Run("mcp44xx", DigipotSmoke.RunMcp44xx);
        Console.WriteLine($"drivers finished passed={Check.Passed} failed={Check.Failed}");
    }
}
