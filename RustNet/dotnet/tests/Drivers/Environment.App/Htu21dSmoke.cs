using RustNet.Community.Bus;
using RustNet.Community.Htu21d;

namespace DriversApp;

public static class Htu21dSmoke
{
    public static void Run()
    {
        // Datasheet CRC examples.
        Check.Equal("htu21d crc dc", 0x79, Htu21d.Crc(Hex.Bytes("DC"), 0, 1));
        Check.Equal("htu21d crc 683a", 0x7C, Htu21d.Crc(Hex.Bytes("68 3A"), 0, 2));
        Check.Equal("htu21d crc 4e85", 0x6B, Htu21d.Crc(Hex.Bytes("4E 85"), 0, 2));

        SimI2cDevice sim = new SimI2cDevice(0x40);
        sim.SetRegister(0xE7, 0x02); // user register default after reset
        Htu21d htu = new Htu21d(sim);
        Check.True("htu21d begin", htu.Begin());
        Check.True("htu21d reset", Hex.Wrote(sim, "FE"));

        // Datasheet examples: 0x683A -> 24.7 C, 0x4E85 -> 32.3 %RH (status bits masked).
        sim.Responses.Enqueue(Hex.Bytes("68 3A 7C"));
        Check.Near("htu21d temperature", 24.686401, htu.ReadTemperatureCelsius(), 0.0005);
        Check.True("htu21d no-hold T cmd", Hex.Wrote(sim, "F3"));
        sim.Responses.Enqueue(Hex.Bytes("4E 85 6B"));
        Check.Near("htu21d humidity", 32.337708, htu.ReadHumidityPercent(), 0.0005);
        Check.Equal("htu21d raw rh masked", 0x4E84, htu.RawHumidity);

        htu.SetResolution(Htu21d.ResolutionRh11T11);
        byte[]? w = sim.LastWriteTo(0xE6);
        Check.True("htu21d resolution write", w != null && w[1] == 0x83);

        sim.Responses.Enqueue(Hex.Bytes("68 3A 7D"));
        bool threw1 = false;
        try
        {
            htu.ReadTemperatureCelsius();
        }
        catch (Exception)
        {
            threw1 = true;
        }
        Check.True("htu21d bad crc", threw1);

        // Si7021: ID access 2 returns SNB_3 = 0x15; firmware byte 0x20 = revision 2.0.
        SimI2cDevice si = new SimI2cDevice(0x40);
        si.Responses.Enqueue(Hex.Bytes("A1 00 B2 00 C3 00 D4 00"));
        si.Responses.Enqueue(Hex.Bytes("15 FF 00 E6 F7 00"));
        si.Responses.Enqueue(Hex.Bytes("20"));
        Si70xx s = new Si70xx(si);
        Check.True("si70xx begin", s.Begin());
        Check.Equal("si70xx device id", 0x15, s.DeviceId);
        Check.Equal("si70xx serial high", unchecked((int)0xA1B2C3D4), (int)(s.SerialNumber >> 32));
        Check.Equal("si70xx serial low", 0x15FFE6F7, (int)s.SerialNumber);
        Check.Equal("si70xx firmware", 0x20, s.FirmwareRevision);
        Check.True("si70xx id cmd 1", Hex.Wrote(si, "FA 0F"));
        Check.True("si70xx id cmd 2", Hex.Wrote(si, "FC C9"));
        si.Responses.Enqueue(Hex.Bytes("68 3A"));
        Check.Near("si70xx temp from rh", 24.686401, s.ReadTemperatureFromPreviousHumidity(), 0.0005);
        s.SetHeaterLevel(5);
        Check.True("si70xx heater level", Hex.Wrote(si, "51 05"));
    }
}
