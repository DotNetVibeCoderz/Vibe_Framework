using RustNet.Community.Bus;
using RustNet.Community.SoilMoisture;

namespace DriversApp;

public static class SoilMoistureSmoke
{
    public static void Run()
    {
        SimAnalogInput adc = new SimAnalogInput();
        adc.Raw = 2000;
        CapacitiveSoilSensor cap = new CapacitiveSoilSensor(adc);
        // Default calibration 2800 dry / 1200 wet: 2000 is half way.
        Check.Near("soil capacitive 50%", 50.0, cap.ReadPercent(), 0.0001);
        adc.Raw = 3000;
        Check.Near("soil clamp dry", 0.0, cap.ReadPercent(), 0.0001);
        adc.Raw = 1000;
        Check.Near("soil clamp wet", 100.0, cap.ReadPercent(), 0.0001);
        adc.Raw = 2500;
        cap.CalibrateDry();
        adc.Raw = 1500;
        cap.CalibrateWet();
        adc.Raw = 2250;
        Check.Near("soil recalibrated", 25.0, cap.ReadPercent(), 0.0001);

        // FC-28: 4095 dry / 1500 wet; 3446 -> (4095-3446)/2595 = 25.0096 %. Power pin toggles around the read.
        SimAnalogInput ao = new SimAnalogInput();
        ao.Raw = 3446;
        SimDigitalInput dout = new SimDigitalInput();
        SimDigitalOutput power = new SimDigitalOutput();
        Fc28SoilSensor fc = new Fc28SoilSensor(ao, dout, power);
        Check.Near("soil fc28 percent", 25.009634, fc.ReadPercent(), 0.0001);
        Check.True("soil fc28 power on/off", power.History.Count == 2 && power.History[0] && !power.History[1]);
        dout.Level = false;
        Check.True("soil fc28 wet (DO low)", fc.IsWet());
        dout.Level = true;
        Check.True("soil fc28 dry (DO high)", !fc.IsWet());
    }
}
