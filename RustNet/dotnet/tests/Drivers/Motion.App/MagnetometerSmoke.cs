using RustNet.Community.Bus;
using RustNet.Community.Magnetometer;

namespace DriversApp;

public static class MagnetometerSmoke
{
    public static void RunHmc5883l()
    {
        SimI2cDevice sim = new SimI2cDevice(0x1E);
        sim.SetRegister(0x0A, 'H');
        sim.SetRegister(0x0B, '4');
        sim.SetRegister(0x0C, '3');
        // Wire order X, Z, Y (big-endian). Gain 1: 1090 LSB/gauss.
        sim.SetRegister16(0x03, 1090);
        sim.SetRegister16(0x05, -545);
        sim.SetRegister16(0x07, 0);
        Hmc5883l m = new Hmc5883l(sim);
        Check.True("hmc5883l begin", m.Begin());
        Check.Equal("hmc5883l cra", 0x70, sim.Registers[0x00]);
        Check.Equal("hmc5883l crb", 0x20, sim.Registers[0x01]);
        Check.Equal("hmc5883l continuous", 0x00, sim.Registers[0x02]);
        m.Read();
        Check.Near("hmc5883l x", 100.0, m.X, 1e-9);
        Check.Near("hmc5883l z", -50.0, m.Z, 1e-9);
        Check.Near("hmc5883l y", 0.0, m.Y, 1e-9);
        Check.Near("hmc5883l heading", 0.0, m.HeadingDegrees(0), 1e-9);
        Check.True("hmc5883l no overflow", !m.Overflow);
        sim.SetRegister16(0x07, -4096);
        m.Read();
        Check.True("hmc5883l overflow", m.Overflow);
        m.SetGain(7);
        sim.SetRegister16(0x03, 230);
        m.Read();
        Check.Near("hmc5883l x gain 7", 100.0, m.X, 1e-9);
        Check.True("hmc5883l absent", !new Hmc5883l(new SimI2cDevice(0x1E)).Begin());
    }

    public static void RunLis3mdl()
    {
        SimI2cDevice sim = new SimI2cDevice(0x1C);
        sim.SetRegister(0x0F, 0x3D);
        // Auto-increment sub-address 0x28 | 0x80.
        sim.SetRegister16Le(0xA8, 6842);
        sim.SetRegister16Le(0xAA, -3421);
        sim.SetRegister16Le(0xAC, 0);
        sim.SetRegister16Le(0xAE, 80);
        Lis3mdl m = new Lis3mdl(sim);
        Check.True("lis3mdl begin", m.Begin());
        Check.Equal("lis3mdl ctrl1", 0xFC, sim.Registers[0x20]);
        Check.Equal("lis3mdl ctrl2", 0x00, sim.Registers[0x21]);
        Check.Equal("lis3mdl ctrl5 bdu", 0x40, sim.Registers[0x24]);
        m.Read();
        // ±4 gauss: 6842 LSB/gauss; 1 gauss = 100 µT.
        Check.Near("lis3mdl x", 100.0, m.X, 1e-9);
        Check.Near("lis3mdl y", -50.0, m.Y, 1e-9);
        Check.Near("lis3mdl heading", 333.434948822922, m.HeadingDegrees(0), 1e-6);
        Check.Near("lis3mdl temperature", 35.0, m.ReadTemperatureCelsius(), 1e-9);
        m.SetRange(16);
        Check.Equal("lis3mdl fs 16", 0x60, sim.Registers[0x21]);
        sim.SetRegister16Le(0xA8, 1711);
        m.Read();
        Check.Near("lis3mdl x 16g", 100.0, m.X, 1e-9);
    }

    public static void RunMag3110()
    {
        SimI2cDevice sim = new SimI2cDevice(0x0E);
        sim.SetRegister(0x07, 0xC4);
        sim.SetRegister16(0x01, 0);
        sim.SetRegister16(0x03, 1000);
        sim.SetRegister16(0x05, -250);
        sim.SetRegister(0x0F, 0xFB);
        Mag3110 m = new Mag3110(sim);
        Check.True("mag3110 begin", m.Begin());
        Check.Equal("mag3110 auto reset", 0x80, sim.Registers[0x11]);
        Check.Equal("mag3110 active", 0x01, sim.Registers[0x10]);
        m.Read();
        // 0.1 µT/LSB.
        Check.Near("mag3110 y", 100.0, m.Y, 1e-9);
        Check.Near("mag3110 z", -25.0, m.Z, 1e-9);
        Check.Near("mag3110 heading", 90.0, m.HeadingDegrees(0), 1e-9);
        Check.Equal("mag3110 die temp", -5, m.ReadDieTemperature());
        m.SetOffsets(100, -3, 0);
        byte[]? off = sim.LastWriteTo(0x09);
        Check.True("mag3110 offsets", off != null && off.Length == 7 && off[1] == 0x00 && off[2] == 0xC8 && off[3] == 0xFF && off[4] == 0xFA);
    }
}
