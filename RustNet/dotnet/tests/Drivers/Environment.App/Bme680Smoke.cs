using RustNet.Community.Bme680;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Bme680Smoke
{
    /// <summary>
    /// A BME680 with a plausible calibration set and ADC values. Expected values were computed
    /// independently from the datasheet/BME68x API formulas (integer path, cross-checked against
    /// the floating-point path: 27.370 C, 91170.9 Pa, 56.82 %RH).
    /// </summary>
    public static SimI2cDevice Chip(int variant)
    {
        SimI2cDevice sim = new SimI2cDevice(0x76);
        sim.SetRegister(0xD0, 0x61);
        sim.SetRegister(0xF0, variant);
        sim.SetRegister16Le(0x8A, 26266);  // par_t2
        sim.SetRegister(0x8C, 3);          // par_t3
        sim.SetRegister16Le(0x8E, 36213);  // par_p1
        sim.SetRegister16Le(0x90, -10325); // par_p2
        sim.SetRegister(0x92, 88);         // par_p3
        sim.SetRegister16Le(0x94, 6941);   // par_p4
        sim.SetRegister16Le(0x96, -102);   // par_p5
        sim.SetRegister(0x98, 37);         // par_p7
        sim.SetRegister(0x99, 30);         // par_p6
        sim.SetRegister16Le(0x9C, -3214);  // par_p8
        sim.SetRegister16Le(0x9E, -2491);  // par_p9
        sim.SetRegister(0xA0, 30);         // par_p10
        sim.SetRegister(0xE1, 0x3F);       // par_h2 = 0x3FF (1023)
        sim.SetRegister(0xE2, 0xFD);       // h2 low nibble F | h1 low nibble D
        sim.SetRegister(0xE3, 0x2C);       // par_h1 = 0x2CD (717)
        sim.SetRegister(0xE4, 0);          // par_h3
        sim.SetRegister(0xE5, 45);         // par_h4
        sim.SetRegister(0xE6, 20);         // par_h5
        sim.SetRegister(0xE7, 120);        // par_h6
        sim.SetRegister(0xE8, 0x9C);       // par_h7 = -100
        sim.SetRegister16Le(0xE9, 26100);  // par_t1
        sim.SetRegister16Le(0xEB, -12623); // par_g2
        sim.SetRegister(0xED, 0xE2);       // par_g1 = -30
        sim.SetRegister(0xEE, 18);         // par_g3
        sim.SetRegister(0x00, 41);         // res_heat_val
        sim.SetRegister(0x02, 0x10);       // res_heat_range = 1
        sim.SetRegister(0x04, 0x10);       // range_switching_error = 1
        sim.SetRegister(0x1D, 0x80);       // new_data_0
        // press_adc = 410000, temp_adc = 505000 (20-bit, left-justified), hum_adc = 22000.
        sim.SetRegister(0x1F, 410000 >> 12);
        sim.SetRegister(0x20, (410000 >> 4) & 0xFF);
        sim.SetRegister(0x21, (410000 & 0x0F) << 4);
        sim.SetRegister(0x22, 505000 >> 12);
        sim.SetRegister(0x23, (505000 >> 4) & 0xFF);
        sim.SetRegister(0x24, (505000 & 0x0F) << 4);
        sim.SetRegister16(0x25, 22000);
        return sim;
    }

    public static void Run()
    {
        SimI2cDevice sim = Chip(0);
        // BME680 gas: adc 400, range 5, gas_valid + heat_stab.
        sim.SetRegister(0x2A, 400 >> 2);
        sim.SetRegister(0x2B, ((400 & 3) << 6) | 0x30 | 5);
        Bme680 bme = new Bme680(sim);
        Check.True("bme680 begin", bme.Begin());
        Check.Equal("bme680 variant", 0, bme.Variant);
        Check.Equal("bme680 h1", 717, bme.H1);
        Check.Equal("bme680 h2", 1023, bme.H2);
        Check.Equal("bme680 g1", -30, bme.G1);
        Check.Equal("bme680 range sw err", 1, bme.RangeSwitchingError);
        bme.Measure();
        Check.True("bme680 reset", Hex.Wrote(sim, "E0 B6"));
        Check.True("bme680 res_heat 320C", Hex.Wrote(sim, "5A 73"));   // 115
        Check.True("bme680 gas_wait 150ms", Hex.Wrote(sim, "64 65"));
        Check.True("bme680 run_gas", Hex.Wrote(sim, "71 10"));
        Check.True("bme680 ctrl_hum x2", Hex.Wrote(sim, "72 02"));
        Check.True("bme680 ctrl_meas forced", Hex.Wrote(sim, "74 8D"));
        Check.Equal("bme680 t_fine", 140136, bme.TFine);
        Check.Equal("bme680 temperature", 2737, bme.TemperatureCentiCelsius);
        Check.Equal("bme680 pressure", 91172, bme.PressurePascal);
        Check.Equal("bme680 humidity", 56795, bme.HumidityMilliPercent);
        Check.True("bme680 gas valid", bme.GasValid);
        Check.Near("bme680 gas resistance", 271061.855, bme.GasResistanceOhms, 0.01);
        Check.Equal("bme680 gas wait 100ms", 0x59, Bme680.GasWait(100));
        Check.Equal("bme680 gas wait 25ms", 0x19, Bme680.GasWait(25));
        Check.Equal("bme680 res heat 300", 110, bme.HeaterResistance(300, 25));

        SimI2cDevice sim688 = Chip(1);
        sim688.SetRegister(0x2C, 300 >> 2);
        sim688.SetRegister(0x2D, ((300 & 3) << 6) | 0x30 | 5);
        Bme680 b688 = new Bme680(sim688);
        Check.True("bme688 begin", b688.Begin());
        b688.Measure();
        Check.True("bme688 run_gas bit5", Hex.Wrote(sim688, "71 20"));
        Check.Near("bme688 gas resistance", 2367630.0578, b688.GasResistanceOhms, 0.01);
        Check.Equal("bme688 temperature", 2737, b688.TemperatureCentiCelsius);

        SimI2cDevice other = new SimI2cDevice(0x77);
        other.SetRegister(0xD0, 0x60);
        Check.True("bme680 wrong id", !new Bme680(other).Begin());
    }
}
