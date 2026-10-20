using RustNet.Community.Bus;

namespace DriversApp;

public static class Program
{
    public static void Main()
    {
        Timing.SleepOverride = ms => { };
        Console.WriteLine("drivers starting");
        Check.Run("adxl335", AdxlSmoke.RunAnalog);
        Check.Run("adxl345", AdxlSmoke.Run345);
        Check.Run("adxl362", AdxlSmoke.Run362);
        Check.Run("mpu6050", ImuSmoke.RunMpu6050);
        Check.Run("lsm6dsox", ImuSmoke.RunLsm6dsox);
        Check.Run("lsm303agr", ImuSmoke.RunLsm303agr);
        Check.Run("bno055", ImuSmoke.RunBno055);
        Check.Run("bmi270", ImuSmoke.RunBmi270);
        Check.Run("hmc5883l", MagnetometerSmoke.RunHmc5883l);
        Check.Run("lis3mdl", MagnetometerSmoke.RunLis3mdl);
        Check.Run("mag3110", MagnetometerSmoke.RunMag3110);
        Check.Run("vl53l0x", Vl53l0xSmoke.Run);
        Check.Run("hcsr04", UltrasonicSmoke.RunHcSr04);
        Check.Run("maxbotix", UltrasonicSmoke.RunMaxBotix);
        Check.Run("a02yyuw", UltrasonicSmoke.RunA02yyuw);
        Check.Run("ina219", InaSmoke.RunIna219);
        Check.Run("ina228", InaSmoke.RunIna228);
        Check.Run("ina260", InaSmoke.RunIna260);
        Check.Run("max1704x", PowerSmoke.RunMax1704x);
        Check.Run("hx711", PowerSmoke.RunHx711);
        Check.Run("nau7802", PowerSmoke.RunNau7802);
        Check.Run("flowmeter", InputSmoke.RunFlowMeter);
        Check.Run("xpt2046", InputSmoke.RunXpt2046);
        Check.Run("tsc2004", InputSmoke.RunTsc2004);
        Check.Run("mpr121", InputSmoke.RunMpr121);
        Check.Run("keypad", InputSmoke.RunKeypad);
        Check.Run("joystick", InputSmoke.RunJoystick);
        Check.Run("mt3339", GnssSmoke.RunMt3339);
        Check.Run("ublox", GnssSmoke.RunUblox);
        Check.Run("bg95", GnssSmoke.RunBg95);
        Check.Run("amg8833", CameraSmoke.RunAmg8833);
        Check.Run("mlx90640", Mlx90640Smoke.Run);
        Check.Run("vc0706", CameraSmoke.RunVc0706);
        Check.Run("arducam", CameraSmoke.RunArduCam);
        Console.WriteLine($"drivers finished passed={Check.Passed} failed={Check.Failed}");
    }
}
