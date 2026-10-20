using RustNet.Community.Bus;
using RustNet.Community.Ina;

namespace DriversApp;

public static class InaSmoke
{
    public static void RunIna219()
    {
        SimWordI2c sim = new SimWordI2c(0x40);
        // 0.1 Ω shunt carrying 0.2 A at 12 V: shunt 20 mV = 2000 × 10 µV, bus 12 V = 3000 × 4 mV (bits 15:3, CNVR set).
        sim.Set(0x01, 2000, 2);
        sim.Set(0x02, (3000 << 3) | 0x02, 2);
        // What the chip computes with CAL = 4096: current = shunt × CAL / 4096, power = current × bus / 5000.
        sim.Set(0x04, 2000, 2);
        sim.Set(0x03, 2000 * 3000 / 5000, 2);
        Ina219 ina = new Ina219(sim);
        ina.Begin(Ina219.ConfigDefault);
        Check.Equal("ina219 config", 0x399F, sim.Get(0x00));
        // Datasheet: CAL = trunc(0.04096 / (Current_LSB × Rshunt)) = 0.04096 / (100 µA × 0.1 Ω) = 4096.
        ina.Calibrate(0.1, 0.0001);
        Check.Equal("ina219 calibration", 4096, sim.Get(0x05));
        Check.Near("ina219 shunt mV", 20.0, ina.ReadShuntMillivolts(), 1e-9);
        Check.Near("ina219 bus V", 12.0, ina.ReadBusVolts(), 1e-9);
        Check.True("ina219 conversion ready", ina.ConversionReady && !ina.MathOverflow);
        Check.Near("ina219 current = V/R", 0.2, ina.ReadCurrentAmps(), 1e-9);
        Check.Near("ina219 power = V*I", 2.4, ina.ReadPowerWatts(), 1e-9);
        sim.Set(0x01, -2000 & 0xFFFF, 2);
        Check.Near("ina219 negative shunt", -20.0, ina.ReadShuntMillivolts(), 1e-9);
    }

    public static void RunIna228()
    {
        SimWordI2c sim = new SimWordI2c(0x40);
        sim.Set(0x3E, 0x5449, 2);
        sim.Set(0x3F, 0x2281, 2);
        // 15 mV / 312.5 nV = 48000; 12 V / 195.3125 µV = 61440; both in bits 23:4.
        sim.Set(0x04, 48000L << 4, 3);
        sim.Set(0x05, 61440L << 4, 3);
        sim.Set(0x06, 3200, 2); // 25 °C / 7.8125 m°C
        sim.Set(0x07, 52429L << 4, 3);
        sim.Set(0x08, 196608, 3);
        sim.Set(0x09, 3686400, 5);
        sim.Set(0x0A, 0x10000000000L - 524288, 5);
        Ina228 ina = new Ina228(sim);
        Check.True("ina228 begin", ina.Begin());
        Check.Equal("ina228 adc config", 0xFB68, sim.Get(0x01));
        // 15 mΩ shunt, 10 A: CURRENT_LSB = 10/2^19, SHUNT_CAL = 13107.2e6 × LSB × R = 3750.
        ina.Calibrate(0.015, 10.0, false);
        Check.Equal("ina228 shunt cal", 3750, sim.Get(0x02));
        double lsb = 10.0 / 524288.0;
        Check.Near("ina228 shunt mV", 15.0, ina.ReadShuntMillivolts(), 1e-9);
        Check.Near("ina228 bus V", 12.0, ina.ReadBusVolts(), 1e-9);
        Check.Near("ina228 die temp", 25.0, ina.ReadDieTemperatureCelsius(), 1e-9);
        Check.Near("ina228 current", 52429 * lsb, ina.ReadCurrentAmps(), 1e-12);
        Check.Near("ina228 power 12 W", 12.0, ina.ReadPowerWatts(), 1e-9);
        Check.Near("ina228 energy 1 Wh", 3600.0, ina.ReadEnergyJoules(), 1e-9);
        Check.Near("ina228 charge", -10.0, ina.ReadChargeCoulombs(), 1e-9);
        sim.Set(0x04, (-48000L & 0xFFFFF) << 4, 3);
        Check.Near("ina228 negative shunt", -15.0, ina.ReadShuntMillivolts(), 1e-9);
        ina.Calibrate(0.015, 10.0, true);
        Check.Equal("ina228 shunt cal x4", 15000, sim.Get(0x02));
        Check.Equal("ina228 adcrange", 0x0010, sim.Get(0x00));
        SimWordI2c other = new SimWordI2c(0x40);
        other.Set(0x3E, 0x5449, 2);
        other.Set(0x3F, 0x2270, 2);
        Check.True("ina228 wrong device", !new Ina228(other).Begin());
    }

    public static void RunIna260()
    {
        SimWordI2c sim = new SimWordI2c(0x40);
        sim.Set(0xFE, 0x5449, 2);
        sim.Set(0xFF, 0x2270, 2);
        sim.Set(0x01, 800, 2);
        sim.Set(0x02, 9600, 2);
        sim.Set(0x03, 1200, 2);
        Ina260 ina = new Ina260(sim);
        Check.True("ina260 begin", ina.Begin());
        Check.Equal("ina260 config", 0x6127, sim.Get(0x00));
        // 1.25 mA, 1.25 mV and 10 mW per LSB.
        Check.Near("ina260 current", 1.0, ina.ReadCurrentAmps(), 1e-9);
        Check.Near("ina260 bus", 12.0, ina.ReadBusVolts(), 1e-9);
        Check.Near("ina260 power", 12.0, ina.ReadPowerWatts(), 1e-9);
        sim.Set(0x01, -400 & 0xFFFF, 2);
        Check.Near("ina260 negative current", -0.5, ina.ReadCurrentAmps(), 1e-9);
        ina.SetAveraging(3);
        Check.Equal("ina260 avg 64", 0x6727, sim.Get(0x00));
    }
}
