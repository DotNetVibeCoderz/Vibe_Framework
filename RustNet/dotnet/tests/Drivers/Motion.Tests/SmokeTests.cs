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
    public void Adxl335() => Smoke("adxl335", DriversApp.AdxlSmoke.RunAnalog);

    [Fact]
    public void Adxl345() => Smoke("adxl345", DriversApp.AdxlSmoke.Run345);

    [Fact]
    public void Adxl362() => Smoke("adxl362", DriversApp.AdxlSmoke.Run362);

    [Fact]
    public void Mpu6050() => Smoke("mpu6050", DriversApp.ImuSmoke.RunMpu6050);

    [Fact]
    public void Lsm6dsox() => Smoke("lsm6dsox", DriversApp.ImuSmoke.RunLsm6dsox);

    [Fact]
    public void Lsm303agr() => Smoke("lsm303agr", DriversApp.ImuSmoke.RunLsm303agr);

    [Fact]
    public void Bno055() => Smoke("bno055", DriversApp.ImuSmoke.RunBno055);

    [Fact]
    public void Bmi270() => Smoke("bmi270", DriversApp.ImuSmoke.RunBmi270);

    [Fact]
    public void Hmc5883l() => Smoke("hmc5883l", DriversApp.MagnetometerSmoke.RunHmc5883l);

    [Fact]
    public void Lis3mdl() => Smoke("lis3mdl", DriversApp.MagnetometerSmoke.RunLis3mdl);

    [Fact]
    public void Mag3110() => Smoke("mag3110", DriversApp.MagnetometerSmoke.RunMag3110);

    [Fact]
    public void Vl53l0x() => Smoke("vl53l0x", DriversApp.Vl53l0xSmoke.Run);

    [Fact]
    public void Hcsr04() => Smoke("hcsr04", DriversApp.UltrasonicSmoke.RunHcSr04);

    [Fact]
    public void Maxbotix() => Smoke("maxbotix", DriversApp.UltrasonicSmoke.RunMaxBotix);

    [Fact]
    public void A02yyuw() => Smoke("a02yyuw", DriversApp.UltrasonicSmoke.RunA02yyuw);

    [Fact]
    public void Ina219() => Smoke("ina219", DriversApp.InaSmoke.RunIna219);

    [Fact]
    public void Ina228() => Smoke("ina228", DriversApp.InaSmoke.RunIna228);

    [Fact]
    public void Ina260() => Smoke("ina260", DriversApp.InaSmoke.RunIna260);

    [Fact]
    public void Max1704x() => Smoke("max1704x", DriversApp.PowerSmoke.RunMax1704x);

    [Fact]
    public void Hx711() => Smoke("hx711", DriversApp.PowerSmoke.RunHx711);

    [Fact]
    public void Nau7802() => Smoke("nau7802", DriversApp.PowerSmoke.RunNau7802);

    [Fact]
    public void Flowmeter() => Smoke("flowmeter", DriversApp.InputSmoke.RunFlowMeter);

    [Fact]
    public void Xpt2046() => Smoke("xpt2046", DriversApp.InputSmoke.RunXpt2046);

    [Fact]
    public void Tsc2004() => Smoke("tsc2004", DriversApp.InputSmoke.RunTsc2004);

    [Fact]
    public void Mpr121() => Smoke("mpr121", DriversApp.InputSmoke.RunMpr121);

    [Fact]
    public void Keypad() => Smoke("keypad", DriversApp.InputSmoke.RunKeypad);

    [Fact]
    public void Joystick() => Smoke("joystick", DriversApp.InputSmoke.RunJoystick);

    [Fact]
    public void Mt3339() => Smoke("mt3339", DriversApp.GnssSmoke.RunMt3339);

    [Fact]
    public void Ublox() => Smoke("ublox", DriversApp.GnssSmoke.RunUblox);

    [Fact]
    public void Bg95() => Smoke("bg95", DriversApp.GnssSmoke.RunBg95);

    [Fact]
    public void Amg8833() => Smoke("amg8833", DriversApp.CameraSmoke.RunAmg8833);

    [Fact]
    public void Mlx90640() => Smoke("mlx90640", DriversApp.Mlx90640Smoke.Run);

    [Fact]
    public void Vc0706() => Smoke("vc0706", DriversApp.CameraSmoke.RunVc0706);

    [Fact]
    public void Arducam() => Smoke("arducam", DriversApp.CameraSmoke.RunArduCam);
}
