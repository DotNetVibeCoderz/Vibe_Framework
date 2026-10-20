using RustNet.Community.Bus;
using Xunit;

namespace RustNet.Drivers.Tests;

/// <summary>
/// Runs the same smoke scenarios the on-device app runs, on the desktop CLR,
/// so a driver bug shows up here first with a stack trace.
/// </summary>
public class SmokeTests
{
    static SmokeTests()
    {
        Timing.SleepOverride = _ => { };
    }

    private static void Smoke(string name, Action body)
    {
        int before = DriversApp.Check.Failed;
        DriversApp.Check.Run(name, body);
        Assert.Equal(before, DriversApp.Check.Failed);
    }

    [Fact]
    public void Stepprofile() => Smoke("stepprofile", DriversApp.StepperSmoke.RunProfile);

    [Fact]
    public void Hbridgemotor() => Smoke("hbridgemotor", DriversApp.DcMotorSmoke.RunHBridge);

    [Fact]
    public void Bidirectionaldcmotor() => Smoke("bidirectionaldcmotor", DriversApp.DcMotorSmoke.RunBidirectional);

    [Fact]
    public void Tb67h420ftg() => Smoke("tb67h420ftg", DriversApp.DcMotorSmoke.RunTb67h420ftg);

    [Fact]
    public void A4988() => Smoke("a4988", DriversApp.StepperSmoke.RunA4988);

    [Fact]
    public void Uln2003() => Smoke("uln2003", DriversApp.StepperSmoke.RunUln2003);

    [Fact]
    public void Gpiostepper() => Smoke("gpiostepper", DriversApp.StepperSmoke.RunGpioStepper);

    [Fact]
    public void Servo() => Smoke("servo", DriversApp.ServoSmoke.RunServo);

    [Fact]
    public void Esc() => Smoke("esc", DriversApp.ServoSmoke.RunEsc);

    [Fact]
    public void Bld510b() => Smoke("bld510b", DriversApp.IndustrialSmoke.RunBld510b);

    [Fact]
    public void Cerusxdrive() => Smoke("cerusxdrive", DriversApp.IndustrialSmoke.RunCerusXDrive);

    [Fact]
    public void Led() => Smoke("led", DriversApp.LedSmoke.RunLed);

    [Fact]
    public void Pwmled() => Smoke("pwmled", DriversApp.LedSmoke.RunPwmLed);

    [Fact]
    public void Rgbled() => Smoke("rgbled", DriversApp.LedSmoke.RunRgbLed);

    [Fact]
    public void Rgbpwmled() => Smoke("rgbpwmled", DriversApp.LedSmoke.RunRgbPwmLed);

    [Fact]
    public void Ledbargraph() => Smoke("ledbargraph", DriversApp.LedSmoke.RunLedBarGraph);

    [Fact]
    public void Ws2812() => Smoke("ws2812", DriversApp.LedSmoke.RunWs2812);

    [Fact]
    public void Apa102() => Smoke("apa102", DriversApp.LedSmoke.RunApa102);

    [Fact]
    public void Pca9633() => Smoke("pca9633", DriversApp.LedSmoke.RunPca9633);

    [Fact]
    public void Relay() => Smoke("relay", DriversApp.OutputSmoke.RunRelay);

    [Fact]
    public void Piezo() => Smoke("piezo", DriversApp.OutputSmoke.RunPiezo);

    [Fact]
    public void Ads1115() => Smoke("ads1115", DriversApp.ConverterSmoke.RunAds1115);

    [Fact]
    public void Ads1015() => Smoke("ads1015", DriversApp.ConverterSmoke.RunAds1015);

    [Fact]
    public void Ads7128() => Smoke("ads7128", DriversApp.ConverterSmoke.RunAds7128);

    [Fact]
    public void Mcp3xxx() => Smoke("mcp3xxx", DriversApp.ConverterSmoke.RunMcp3xxx);

    [Fact]
    public void Mcp4725() => Smoke("mcp4725", DriversApp.ConverterSmoke.RunMcp4725);

    [Fact]
    public void Mcp49xx() => Smoke("mcp49xx", DriversApp.ConverterSmoke.RunMcp49xx);

    [Fact]
    public void Mcp23017() => Smoke("mcp23017", DriversApp.ExpanderSmoke.RunMcp23017);

    [Fact]
    public void Mcp23008() => Smoke("mcp23008", DriversApp.ExpanderSmoke.RunMcp23008);

    [Fact]
    public void Mcp23s17() => Smoke("mcp23s17", DriversApp.ExpanderSmoke.RunMcp23s17);

    [Fact]
    public void Pcf857x() => Smoke("pcf857x", DriversApp.ExpanderSmoke.RunPcf857x);

    [Fact]
    public void Pca95xx() => Smoke("pca95xx", DriversApp.ExpanderSmoke.RunPca95xx);

    [Fact]
    public void Ht16k33() => Smoke("ht16k33", DriversApp.ExpanderSmoke.RunHt16k33);

    [Fact]
    public void RtcDayofweek() => Smoke("rtc-dayofweek", DriversApp.RtcSmoke.RunDayOfWeek);

    [Fact]
    public void Ds1307() => Smoke("ds1307", DriversApp.RtcSmoke.RunDs1307);

    [Fact]
    public void Ds3231() => Smoke("ds3231", DriversApp.RtcSmoke.RunDs3231);

    [Fact]
    public void Pcf8523() => Smoke("pcf8523", DriversApp.RtcSmoke.RunPcf8523);

    [Fact]
    public void Mcp41xx() => Smoke("mcp41xx", DriversApp.DigipotSmoke.RunMcp41xx);

    [Fact]
    public void Mcp401x() => Smoke("mcp401x", DriversApp.DigipotSmoke.RunMcp401x);

    [Fact]
    public void Mcp44xx() => Smoke("mcp44xx", DriversApp.DigipotSmoke.RunMcp44xx);
}
