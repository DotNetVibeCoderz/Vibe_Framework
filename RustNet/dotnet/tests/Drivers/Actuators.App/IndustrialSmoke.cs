using RustNet.Community.Bld510b;
using RustNet.Community.Bus;
using RustNet.Community.CerusXDrive;

namespace DriversApp;

public static class IndustrialSmoke
{
    public static void RunBld510b()
    {
        SimPwmOutput sv = new SimPwmOutput(20000);
        SimDigitalOutput dir = new SimDigitalOutput();
        SimDigitalOutput en = new SimDigitalOutput();
        SimDigitalOutput brk = new SimDigitalOutput();
        SimDigitalInput speed = new SimDigitalInput();
        Bld510b d = new Bld510b(sv, dir, en, brk, speed);

        d.Begin();
        Check.True("bld510b safe: EN open (high) = stopped", en.State);
        Check.True("bld510b safe: BRK open (high) = brake", brk.State);
        Check.True("bld510b safe: forward", dir.State);
        Check.Equal("bld510b safe: SV 0", 0, sv.Duty);

        d.Run(false, 0.4);
        Check.True("bld510b run: EN to COM", !en.State);
        Check.True("bld510b run: brake released", !brk.State);
        Check.True("bld510b run: reverse = F/R to COM", !dir.State);
        Check.Equal("bld510b run: SV duty", 4000, sv.Duty);

        d.SetRpm(1500);
        Check.Equal("bld510b 1500/3000 rpm", 5000, sv.Duty);

        d.Stop();
        Check.True("bld510b stop", sv.Duty == 0 && brk.State);

        // RPM = 20 F / pole pairs: 100 Hz on a 2-pole-pair motor = 1000 rpm.
        Check.Near("bld510b rpm 2 pairs", 1000.0, d.RpmFromPulseFrequency(100), 0.001);
        d.PolePairs = 4;
        Check.Near("bld510b rpm 4 pairs", 500.0, d.RpmFromPulseFrequency(100), 0.001);
        d.PolePairs = 2;

        // A fake clock advancing 1 ms per SPEED sample; the signal has a 10 ms period (100 Hz).
        FakeSpeedSignal sig = new FakeSpeedSignal();
        Timing.ClockOverride = sig.Now;
        speed.Source = sig.Sample;
        double rpm = d.MeasureRpm(100);
        Timing.ClockOverride = null;
        Check.Near("bld510b measured rpm", 1000.0, rpm, 0.001);
    }

    /// <summary>Delta-compatible map: 0x2000 run (0x12 run fwd, 0x22 run rev, 0x01 stop), 0x2001 0.01 Hz, 0x2002 bit1 reset.</summary>
    public static void RunCerusXDrive()
    {
        SimModbusTransport bus = new SimModbusTransport();
        CerusXDrive vfd = new CerusXDrive(bus, 1);

        vfd.SetFrequency(45.5);
        vfd.Run(true);
        vfd.Run(false);
        vfd.Stop();
        vfd.ResetFault();
        vfd.Jog(true);
        Check.Equal("cerus write count", 6, bus.WriteCount);
        Check.Equal("cerus unit", 1, bus.Writes[0]);
        Check.Equal("cerus freq reg", 0x2001, bus.WriteAddress(0));
        Check.Equal("cerus 45.5 Hz", 4550, bus.WriteValue(0));
        Check.Equal("cerus run reg", 0x2000, bus.WriteAddress(1));
        Check.Equal("cerus run fwd", 0x0012, bus.WriteValue(1));
        Check.Equal("cerus run rev", 0x0022, bus.WriteValue(2));
        Check.Equal("cerus stop", 0x0001, bus.WriteValue(3));
        Check.Equal("cerus fault reg", 0x2002, bus.WriteAddress(4));
        Check.Equal("cerus fault reset", 0x0002, bus.WriteValue(4));
        Check.Equal("cerus jog fwd", 0x0013, bus.WriteValue(5));

        bus.Registers[0x2100] = 0;
        bus.Registers[0x2101] = 0x0003;
        bus.Registers[0x2102] = 4550;
        bus.Registers[0x2103] = 4500;
        bus.Registers[0x2104] = 123;
        bus.Registers[0x2105] = 3250;
        bus.Registers[0x2106] = 2300;
        int readsBefore = bus.ReadCount;
        CerusStatus s = vfd.ReadStatus();
        Check.Equal("cerus status one transaction", readsBefore + 1, bus.ReadCount);
        Check.True("cerus running", s.Running);
        Check.True("cerus forward", !s.Reverse);
        Check.Near("cerus output freq", 45.0, s.OutputFrequencyHz, 0.0001);
        Check.Near("cerus freq command", 45.5, s.FrequencyCommandHz, 0.0001);
        Check.Near("cerus current", 12.3, s.OutputCurrentA, 0.0001);
        Check.Near("cerus dc bus", 325.0, s.DcBusVoltage, 0.0001);
        Check.Near("cerus output voltage", 230.0, s.OutputVoltage, 0.0001);

        bus.Registers[0x2101] = 0x001B;
        Check.True("cerus reverse status", vfd.ReadStatus().Reverse);
        bus.Registers[0x2101] = 0x0000;
        Check.True("cerus stopped status", vfd.ReadStatus().Stopped);

        bus.Registers[0x2103] = 6000;
        Check.Near("cerus read output freq", 60.0, vfd.ReadOutputFrequency(), 0.0001);
        bus.Registers[0x220E] = 452;
        Check.Near("cerus igbt temp", 45.2, vfd.ReadIgbtTemperature(), 0.0001);
        bus.Registers[0x220F] = 0xFF9C;
        Check.Near("cerus ambient negative", -10.0, vfd.ReadAmbientTemperature(), 0.0001);
        bus.Registers[0x2100] = 82;
        Check.Equal("cerus error code", 82, vfd.ReadErrorCode());
    }
}

/// <summary>A 1 ms-per-sample clock and a 100 Hz square wave (10 ms period) on it.</summary>
public class FakeSpeedSignal
{
    public int T;

    public long Now() => T;

    public bool Sample()
    {
        T++;
        return (T / 5) % 2 == 1;
    }
}
