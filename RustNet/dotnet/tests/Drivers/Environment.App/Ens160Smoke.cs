using RustNet.Community.Bus;
using RustNet.Community.Ens160;

namespace DriversApp;

public static class Ens160Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x53);
        sim.SetRegister16Le(0x00, 0x0160);   // PART_ID, little-endian
        sim.SetRegister(0x4C, 5);            // GPR_READ[4..6] = firmware 5.4.6
        sim.SetRegister(0x4D, 4);
        sim.SetRegister(0x4E, 6);
        Ens160 ens = new Ens160(sim);
        Check.True("ens160 begin", ens.Begin());
        Check.True("ens160 reset", Hex.Wrote(sim, "10 F0"));
        Check.True("ens160 get app version", Hex.Wrote(sim, "12 0E"));
        Check.True("ens160 standard mode", Hex.Wrote(sim, "10 02"));
        Check.Equal("ens160 firmware", 0x050406, ens.FirmwareVersion);

        // Status 0x83: running, normal validity, new data; AQI 3, TVOC 0x01F4 = 500 ppb, eCO2 0x0384 = 900 ppm.
        sim.SetRegister(0x20, 0x83);
        sim.SetRegister(0x21, 0x03);
        sim.SetRegister16Le(0x22, 500);
        sim.SetRegister16Le(0x24, 900);
        Check.True("ens160 data ready", ens.DataReady());
        ens.ReadData();
        Check.Equal("ens160 aqi", 3, ens.Aqi);
        Check.Equal("ens160 tvoc", 500, ens.TvocPpb);
        Check.Equal("ens160 eco2", 900, ens.ECo2Ppm);
        Check.Equal("ens160 validity", 0, ens.Validity);

        // TEMP_IN = (25 + 273.15) * 64 = 19081.6 -> 0x4A8A; RH_IN = 50 * 512 = 0x6400 (both little-endian).
        ens.SetCompensation(25.0, 50.0);
        Check.True("ens160 compensation", Hex.Wrote(sim, "13 8A 4A 00 64"));
        sim.SetRegister16Le(0x30, 0x4A8A);
        sim.SetRegister16Le(0x32, 0x6400);
        Check.Near("ens160 data_t", 25.00625, ens.ReadCompensationTemperature(), 0.00001);
        Check.Near("ens160 data_rh", 50.0, ens.ReadCompensationHumidity(), 0.00001);

        SimI2cDevice other = new SimI2cDevice(0x53);
        Check.True("ens160 wrong part", !new Ens160(other).Begin());
    }
}
