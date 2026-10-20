using RustNet.Community.Bus;

namespace DriversApp;

public static class Program
{
    public static void Main()
    {
        Timing.SleepOverride = ms => { };
        Console.WriteLine("drivers starting");
        Check.Run("bme280", Bme280Smoke.Run);
        Check.Run("sht3x", Sht3xSmoke.Run);
        Check.Run("sht4x", Sht4xSmoke.Run);
        Check.Run("htu21d", Htu21dSmoke.Run);
        Check.Run("htu31d", Htu31dSmoke.Run);
        Check.Run("aht", AhtSmoke.Run);
        Check.Run("dht12", Dht12Smoke.Run);
        Check.Run("bme680", Bme680Smoke.Run);
        Check.Run("bmp180", Bmp180Smoke.Run);
        Check.Run("ms5611", Ms5611Smoke.Run);
        Check.Run("mpl3115a2", Mpl3115a2Smoke.Run);
        Check.Run("ccs811", Ccs811Smoke.Run);
        Check.Run("sgp40", Sgp40Smoke.Run);
        Check.Run("scd30", Scd30Smoke.Run);
        Check.Run("scd4x", Scd4xSmoke.Run);
        Check.Run("ens160", Ens160Smoke.Run);
        Check.Run("pmsa003i", Pmsa003iSmoke.Run);
        Check.Run("ags01db", Ags01dbSmoke.Run);
        Check.Run("bh1750", Bh1750Smoke.Run);
        Check.Run("bh1745", Bh1745Smoke.Run);
        Check.Run("tsl2591", Tsl2591Smoke.Run);
        Check.Run("veml7700", Veml7700Smoke.Run);
        Check.Run("max44009", Max44009Smoke.Run);
        Check.Run("si1145", Si1145Smoke.Run);
        Check.Run("temt6000", Temt6000Smoke.Run);
        Check.Run("tcs34725", Tcs34725Smoke.Run);
        Check.Run("lm75", Lm75Smoke.Run);
        Check.Run("tmp102", Tmp102Smoke.Run);
        Check.Run("mcp9808", Mcp9808Smoke.Run);
        Check.Run("max6675", Max6675Smoke.Run);
        Check.Run("mcp9600", Mcp9600Smoke.Run);
        Check.Run("thermistor", ThermistorSmoke.Run);
        Check.Run("soilmoisture", SoilMoistureSmoke.Run);
        Check.Run("ky038", Ky038Smoke.Run);
        Console.WriteLine($"drivers finished passed={Check.Passed} failed={Check.Failed}");
    }
}
