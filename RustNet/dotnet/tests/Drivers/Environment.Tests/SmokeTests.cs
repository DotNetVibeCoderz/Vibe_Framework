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
    public void Bme280() => Smoke("bme280", DriversApp.Bme280Smoke.Run);

    [Fact]
    public void Sht3x() => Smoke("sht3x", DriversApp.Sht3xSmoke.Run);

    [Fact]
    public void Sht4x() => Smoke("sht4x", DriversApp.Sht4xSmoke.Run);

    [Fact]
    public void Htu21d() => Smoke("htu21d", DriversApp.Htu21dSmoke.Run);

    [Fact]
    public void Htu31d() => Smoke("htu31d", DriversApp.Htu31dSmoke.Run);

    [Fact]
    public void Aht() => Smoke("aht", DriversApp.AhtSmoke.Run);

    [Fact]
    public void Dht12() => Smoke("dht12", DriversApp.Dht12Smoke.Run);

    [Fact]
    public void Bme680() => Smoke("bme680", DriversApp.Bme680Smoke.Run);

    [Fact]
    public void Bmp180() => Smoke("bmp180", DriversApp.Bmp180Smoke.Run);

    [Fact]
    public void Ms5611() => Smoke("ms5611", DriversApp.Ms5611Smoke.Run);

    [Fact]
    public void Mpl3115a2() => Smoke("mpl3115a2", DriversApp.Mpl3115a2Smoke.Run);

    [Fact]
    public void Ccs811() => Smoke("ccs811", DriversApp.Ccs811Smoke.Run);

    [Fact]
    public void Sgp40() => Smoke("sgp40", DriversApp.Sgp40Smoke.Run);

    [Fact]
    public void Scd30() => Smoke("scd30", DriversApp.Scd30Smoke.Run);

    [Fact]
    public void Scd4x() => Smoke("scd4x", DriversApp.Scd4xSmoke.Run);

    [Fact]
    public void Ens160() => Smoke("ens160", DriversApp.Ens160Smoke.Run);

    [Fact]
    public void Pmsa003i() => Smoke("pmsa003i", DriversApp.Pmsa003iSmoke.Run);

    [Fact]
    public void Ags01db() => Smoke("ags01db", DriversApp.Ags01dbSmoke.Run);

    [Fact]
    public void Bh1750() => Smoke("bh1750", DriversApp.Bh1750Smoke.Run);

    [Fact]
    public void Bh1745() => Smoke("bh1745", DriversApp.Bh1745Smoke.Run);

    [Fact]
    public void Tsl2591() => Smoke("tsl2591", DriversApp.Tsl2591Smoke.Run);

    [Fact]
    public void Veml7700() => Smoke("veml7700", DriversApp.Veml7700Smoke.Run);

    [Fact]
    public void Max44009() => Smoke("max44009", DriversApp.Max44009Smoke.Run);

    [Fact]
    public void Si1145() => Smoke("si1145", DriversApp.Si1145Smoke.Run);

    [Fact]
    public void Temt6000() => Smoke("temt6000", DriversApp.Temt6000Smoke.Run);

    [Fact]
    public void Tcs34725() => Smoke("tcs34725", DriversApp.Tcs34725Smoke.Run);

    [Fact]
    public void Lm75() => Smoke("lm75", DriversApp.Lm75Smoke.Run);

    [Fact]
    public void Tmp102() => Smoke("tmp102", DriversApp.Tmp102Smoke.Run);

    [Fact]
    public void Mcp9808() => Smoke("mcp9808", DriversApp.Mcp9808Smoke.Run);

    [Fact]
    public void Max6675() => Smoke("max6675", DriversApp.Max6675Smoke.Run);

    [Fact]
    public void Mcp9600() => Smoke("mcp9600", DriversApp.Mcp9600Smoke.Run);

    [Fact]
    public void Thermistor() => Smoke("thermistor", DriversApp.ThermistorSmoke.Run);

    [Fact]
    public void SoilMoisture() => Smoke("soilmoisture", DriversApp.SoilMoistureSmoke.Run);

    [Fact]
    public void Ky038() => Smoke("ky038", DriversApp.Ky038Smoke.Run);
}
