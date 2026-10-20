using RustNet.Community.Ads1x15;
using RustNet.Community.Ads7128;
using RustNet.Community.Bus;
using RustNet.Community.Mcp3xxx;
using RustNet.Community.Mcp4725;
using RustNet.Community.Mcp49xx;

namespace DriversApp;

public static class ConverterSmoke
{
    private static byte[] Two(int hi, int lo)
    {
        byte[] b = new byte[2];
        b[0] = (byte)hi;
        b[1] = (byte)lo;
        return b;
    }

    /// <summary>
    /// ADS1115 datasheet: AIN0-GND (MUX 100), ±4.096 V (PGA 001), single-shot, 128 SPS (DR 100),
    /// comparator off = 0xC383; LSB at ±4.096 V = 125 µV, at ±2.048 V = 62.5 µV.
    /// </summary>
    public static void RunAds1115()
    {
        SimI2cDevice sim = new SimI2cDevice(0x48);
        Ads1115 adc = new Ads1115(sim);
        adc.Gain = Ads1x15.Pga4096;
        sim.Responses.Enqueue(Two(0xC3, 0x83)); // config read-back: OS = 1, done
        sim.Responses.Enqueue(Two(0x5D, 0xC0)); // 24000 counts
        double mv = adc.ReadSingleEndedMillivolts(0);
        Expect.Write("ads1115 config write", "01 C3 83", sim, 0);
        Check.Near("ads1115 24000 x 125 uV", 3000.0, mv, 0.0001);
        Expect.LastWrite("ads1115 conversion pointer", "00", sim);

        adc.Gain = Ads1x15.Pga2048;
        sim.Responses.Enqueue(Two(0x85, 0x83));
        sim.Responses.Enqueue(Two(0xFF, 0x38)); // -200 counts
        double diff = adc.ReadDifferentialMillivolts(Ads1x15.MuxDiff01);
        Check.Equal("ads1115 AIN0-AIN1 default config", 0x8583, adc.LastConfig);
        Check.Near("ads1115 -200 x 62.5 uV", -12.5, diff, 0.0001);

        Check.Near("ads1115 LSB 6.144", 187.5, adc.LsbMicrovolts(Ads1x15.Pga6144), 0.0001);
        Check.Near("ads1115 LSB 0.256", 7.8125, adc.LsbMicrovolts(Ads1x15.Pga256), 0.0001);
        Check.Equal("ads1115 DR 111 = 860 SPS", 860, adc.SamplesPerSecond(7));
    }

    /// <summary>ADS1015: 12-bit left-justified result; LSB at ±2.048 V = 1 mV, ±6.144 V = 3 mV; DR 100 = 1600 SPS.</summary>
    public static void RunAds1015()
    {
        SimI2cDevice sim = new SimI2cDevice(0x48);
        Ads1015 adc = new Ads1015(sim);
        adc.Gain = Ads1x15.Pga1024;
        sim.Responses.Enqueue(Two(0xF7, 0x83));
        sim.Responses.Enqueue(Two(0x7F, 0xF0)); // +2047
        int raw = adc.ReadRaw(Ads1x15.MuxSingle3);
        Expect.Write("ads1015 AIN3 ±1.024 config", "01 F7 83", sim, 0);
        Check.Equal("ads1015 raw", 2047, raw);
        Check.Near("ads1015 2047 x 0.5 mV", 1023.5, adc.ToMillivolts(raw), 0.0001);

        sim.Responses.Enqueue(Two(0xF7, 0x83));
        sim.Responses.Enqueue(Two(0x80, 0x00));
        Check.Equal("ads1015 negative full scale", -2048, adc.ReadRaw(Ads1x15.MuxSingle3));
        Check.Near("ads1015 LSB 2.048", 1000.0, adc.LsbMicrovolts(Ads1x15.Pga2048), 0.0001);
        Check.Near("ads1015 LSB 6.144", 3000.0, adc.LsbMicrovolts(Ads1x15.Pga6144), 0.0001);
        Check.Equal("ads1015 DR 100 = 1600 SPS", 1600, adc.SamplesPerSecond(4));

        adc.SetThresholds(100, 1000);
        Expect.Write("ads1015 lo thresh shifted", "02 06 40", sim, sim.Writes.Count - 2);
        Expect.LastWrite("ads1015 hi thresh shifted", "03 3E 80", sim);
    }

    public static void RunAds7128()
    {
        SimI2cDevice sim = new SimI2cDevice(0x10);
        Ads7128 adc = new Ads7128(sim);
        sim.Responses.Enqueue(Two(0x81, 0)); // SYSTEM_STATUS reset value
        Check.True("ads7128 begin", adc.Begin());
        Expect.Write("ads7128 reset (set bit RST)", "18 01 01", sim, 0);
        Expect.Write("ads7128 PIN_CFG analog", "08 05 00", sim, 1);
        Expect.Write("ads7128 manual sequence", "08 10 00", sim, 2);
        Expect.Write("ads7128 register read opcode", "10 00", sim, 3);

        sim.Responses.Enqueue(Two(0xAB, 0xC0));
        int code = adc.ReadRaw(3);
        Expect.LastWrite("ads7128 CHANNEL_SEL 3", "08 11 03", sim);
        Check.Equal("ads7128 12-bit code", 0xABC, code);
        Check.Near("ads7128 mV at 3.3 V", 2213.9648, adc.ToMillivolts(code), 0.001);

        int n = sim.Writes.Count;
        adc.ConfigureDigitalOutput(5);
        Expect.Write("ads7128 gpio pin cfg", "18 05 20", sim, n);
        Expect.Write("ads7128 gpio output", "18 07 20", sim, n + 1);
        Expect.Write("ads7128 gpio push-pull", "18 09 20", sim, n + 2);
        adc.WriteDigital(5, false);
        Expect.LastWrite("ads7128 clear GPO", "20 0B 20", sim);
        sim.Responses.Enqueue(Two(0x04, 0));
        Check.True("ads7128 read GPI", adc.ReadDigital(2));
        Expect.LastWrite("ads7128 GPI_VALUE read", "10 0D", sim);
    }

    /// <summary>MCP3008 frame 01 · SGL D2 D1 D0 0000 · 00; MCP3208 frame 0000 01 SGL D2 · D1 D0 000000 · 00.</summary>
    public static void RunMcp3xxx()
    {
        SimSpiDevice spi = Expect.Spi();
        spi.Responder = new Mcp3xxxChip().Respond;
        Mcp3008 m3008 = new Mcp3008(spi);
        int v = m3008.ReadRaw(5, true);
        Expect.LastSpi("mcp3008 ch5 single", "01 D0 00", spi);
        Check.Equal("mcp3008 10-bit result", 0x25A, v);
        Check.Near("mcp3008 mV", 1940.039, m3008.ToMillivolts(v), 0.001);

        Mcp3004 m3004 = new Mcp3004(spi);
        m3004.ReadRaw(2, false);
        Expect.LastSpi("mcp3004 ch2 differential", "01 20 00", spi);

        Mcp3208 m3208 = new Mcp3208(spi);
        m3208.ReferenceMillivolts = 5000;
        int w = m3208.ReadRaw(6, true);
        Expect.LastSpi("mcp3208 ch6 single", "07 80 00", spi);
        Check.Equal("mcp3208 12-bit result", 0xBCD, w);
        Check.Near("mcp3208 mV at 5 V", 3687.744, m3208.ToMillivolts(w), 0.001);

        Mcp3204 m3204 = new Mcp3204(spi);
        m3204.ReadRaw(1, false);
        Expect.LastSpi("mcp3204 ch1 differential", "04 40 00", spi);
    }

    public static void RunMcp4725()
    {
        SimI2cDevice sim = new SimI2cDevice(0x60);
        Mcp4725 dac = new Mcp4725(sim);
        dac.SetValueFast(0x123);
        Expect.LastWrite("mcp4725 fast mode", "01 23", sim);
        dac.SetValueFast(0x123, Mcp4725.PowerDown100k);
        Expect.LastWrite("mcp4725 fast mode PD=10", "21 23", sim);
        dac.SetValue(0xABC);
        Expect.LastWrite("mcp4725 write DAC register", "40 AB C0", sim);
        dac.SetValue(0xABC, Mcp4725.PowerDown1k);
        Expect.LastWrite("mcp4725 write DAC PD=01", "42 AB C0", sim);
        dac.SetMillivolts(1650);
        Expect.LastWrite("mcp4725 1.65 V of 3.3 V = 2048", "08 00", sim);

        byte[] ready = new byte[5];
        ready[0] = 0xC0;
        sim.Responses.Enqueue(ready);
        Check.True("mcp4725 eeprom write completes", dac.SetValueAndPersist(0x800));
        Expect.LastWrite("mcp4725 write DAC+EEPROM", "60 80 00", sim);

        byte[] st = new byte[5];
        st[0] = 0x44; // busy, POR set, PD = 10
        st[1] = 0xAB;
        st[2] = 0xC0;
        st[3] = 0x6A; // EEPROM PD = 11, D11..D8 = 1010
        st[4] = 0x55;
        sim.Responses.Enqueue(st);
        Mcp4725Status s = dac.ReadStatus();
        Check.True("mcp4725 status busy/por", !s.Ready && s.PowerOnReset);
        Check.Equal("mcp4725 status pd", 2, s.PowerDown);
        Check.Equal("mcp4725 status dac", 0xABC, s.DacValue);
        Check.Equal("mcp4725 status eeprom pd", 3, s.EepromPowerDown);
        Check.Equal("mcp4725 status eeprom value", 0xA55, s.EepromValue);
    }

    /// <summary>MCP4922 word: A/B BUF GA SHDN D11..D0.</summary>
    public static void RunMcp49xx()
    {
        SimSpiDevice spi = Expect.Spi();
        SimDigitalOutput ldac = new SimDigitalOutput();
        Mcp4922 dual = new Mcp4922(spi, ldac);
        dual.SetValue(Mcp49xx.ChannelA, 2048);
        Expect.LastSpi("mcp4922 A unbuffered 1x active 0x800", "38 00", spi);
        Expect.Levels("mcp4922 LDAC pulse", "01", ldac.History);
        dual.Buffered = true;
        dual.DoubleGain = true;
        dual.SetValue(Mcp49xx.ChannelB, 0xABC);
        Expect.LastSpi("mcp4922 B buffered 2x active", "DA BC", spi);
        dual.Buffered = false;
        dual.DoubleGain = false;
        dual.Shutdown(Mcp49xx.ChannelA);
        Expect.LastSpi("mcp4922 shutdown A", "20 00", spi);

        SimSpiDevice spi1 = Expect.Spi();
        Mcp4921 single = new Mcp4921(spi1, null);
        single.SetValue(Mcp49xx.ChannelB, 5000);
        Expect.LastSpi("mcp4921 forced to A, clamped", "3F FF", spi1);
        single.ReferenceMillivolts = 2048;
        single.SetMillivolts(Mcp49xx.ChannelA, 1000);
        Expect.LastSpi("mcp4921 1.000 V of 2.048 V = 2000", "37 D0", spi1);
    }
}

/// <summary>MCP3x0x reply bytes: undefined high bits set, null bit 0, then the result.</summary>
public class Mcp3xxxChip
{
    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[3];
        rx[0] = 0xFF;
        if (tx[0] == 0x01)
        {
            rx[1] = 0xFA; // MCP300x: null bit (bit 2) 0, B9B8 = 10
            rx[2] = 0x5A;
        }
        else
        {
            rx[1] = 0xEB; // MCP320x: null bit (bit 4) 0, B11..B8 = 1011
            rx[2] = 0xCD;
        }
        return rx;
    }
}
