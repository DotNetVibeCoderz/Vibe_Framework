using RustNet.Community.Bus;

namespace RustNet.Community.ArduCam;

/// <summary>
/// ArduCAM Mini camera shields (2MP OV2640, 5MP OV5642 Plus): the ArduChip
/// frame-buffer controller on SPI (mode 0, ≤ 8 MHz) plus the image sensor's
/// SCCB/I2C registers. ArduChip registers are written as (addr | 0x80, value)
/// and read as (addr &amp; 0x7F, dummy). A capture is: clear the FIFO flag,
/// start, wait for CAP_DONE, read the FIFO length, burst-read the FIFO.
///
/// Sensor initialisation tables (ArduCAM's OV2640_JPEG_INIT /
/// OV5642_JPEG_* arrays, a few hundred register pairs each) are not
/// shipped here: pass them to <see cref="WriteSensorRegisters8"/> /
/// <see cref="WriteSensorRegisters16"/>.
/// </summary>
public class ArduCam
{
    public const int Ov2640Address = 0x30;
    public const int Ov5642Address = 0x3C;

    public const int RegTest = 0x00;
    public const int RegMode = 0x02;
    public const int RegFifo = 0x04;
    public const int RegVersion = 0x40;
    public const int RegTrigger = 0x41;
    public const int RegFifoSize1 = 0x42;
    public const int RegFifoSize2 = 0x43;
    public const int RegFifoSize3 = 0x44;
    public const int BurstFifoRead = 0x3C;
    public const int SingleFifoRead = 0x3D;

    public const int FifoClearMask = 0x01;
    public const int FifoStartMask = 0x02;
    public const int FifoReadPointerReset = 0x10;
    public const int FifoWritePointerReset = 0x20;
    public const int CaptureDoneMask = 0x08;

    private readonly SpiDevice _spi;
    private readonly I2cDevice _sensor;

    /// <summary>Polls (1 ms apart) to wait for CAP_DONE.</summary>
    public int CapturePolls = 3000;

    /// <summary>
    /// Some shield revisions (OV2640 Mini without "Plus") shift out one dummy
    /// byte after the burst command; set true to drop it.
    /// </summary>
    public bool SkipFirstBurstByte;

    /// <summary>Mask applied to the 24-bit FIFO length (0x7FFFFF on the Mini boards).</summary>
    public int FifoLengthMask = 0x7FFFFF;

    public ArduCam(int spiBus, int chipSelectPin, int i2cBus, int sensorAddress)
        : this(new SpiDevice(spiBus, chipSelectPin, 4000000, 0), new I2cDevice(i2cBus, sensorAddress))
    {
    }

    public ArduCam(SpiDevice spi, I2cDevice sensor)
    {
        _spi = spi;
        _sensor = sensor;
    }

    public void WriteRegister(int register, int value)
    {
        byte[] b = new byte[2];
        b[0] = (byte)(register | 0x80);
        b[1] = (byte)value;
        _spi.Write(b);
    }

    public int ReadRegister(int register)
    {
        byte[] b = new byte[2];
        b[0] = (byte)(register & 0x7F);
        return _spi.Transfer(b)[1];
    }

    /// <summary>SPI link check: write 0x55 to the test register and read it back.</summary>
    public bool CheckSpi()
    {
        WriteRegister(RegTest, 0x55);
        if (ReadRegister(RegTest) != 0x55)
        {
            return false;
        }
        WriteRegister(RegTest, 0xAA);
        return ReadRegister(RegTest) == 0xAA;
    }

    // ---- Sensor (SCCB/I2C) ----

    public void WriteSensor8(int register, int value) => _sensor.WriteRegister(register, value);

    public int ReadSensor8(int register) => _sensor.ReadRegister(register);

    public void WriteSensor16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)(register >> 8);
        b[1] = (byte)register;
        b[2] = (byte)value;
        _sensor.Write(b);
    }

    public int ReadSensor16(int register)
    {
        byte[] a = new byte[2];
        a[0] = (byte)(register >> 8);
        a[1] = (byte)register;
        return _sensor.WriteRead(a, 1)[0];
    }

    /// <summary>OV2640: select the sensor bank (0xFF = 0x01) and check PID 0x26 / VER 0x41 or 0x42.</summary>
    public bool DetectOv2640()
    {
        WriteSensor8(0xFF, 0x01);
        int pid = ReadSensor8(0x0A);
        int ver = ReadSensor8(0x0B);
        return pid == 0x26 && (ver == 0x41 || ver == 0x42);
    }

    /// <summary>OV5642: chip ID registers 0x300A/0x300B = 0x56/0x42.</summary>
    public bool DetectOv5642() => ReadSensor16(0x300A) == 0x56 && ReadSensor16(0x300B) == 0x42;

    /// <summary>
    /// Write a register table for an 8-bit-register sensor (OV2640) packed as
    /// (register, value) byte pairs; a (0xFF, 0xFF) pair ends it, as in ArduCAM's tables.
    /// </summary>
    public void WriteSensorRegisters8(byte[] pairs)
    {
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (pairs[i] == 0xFF && pairs[i + 1] == 0xFF)
            {
                return;
            }
            WriteSensor8(pairs[i], pairs[i + 1]);
        }
    }

    /// <summary>
    /// Write a table for a 16-bit-register sensor (OV5642) packed as
    /// (register high, register low, value) triples; register 0xFFFF ends it.
    /// </summary>
    public void WriteSensorRegisters16(byte[] triples)
    {
        for (int i = 0; i + 2 < triples.Length; i += 3)
        {
            int reg = (triples[i] << 8) | triples[i + 1];
            if (reg == 0xFFFF)
            {
                return;
            }
            WriteSensor16(reg, triples[i + 2]);
        }
    }

    // ---- Capture ----

    /// <summary>Clear the capture-done flag and reset the FIFO pointers.</summary>
    public void ClearFifo() => WriteRegister(RegFifo, FifoClearMask);

    public void StartCapture() => WriteRegister(RegFifo, FifoStartMask);

    public bool CaptureDone() => (ReadRegister(RegTrigger) & CaptureDoneMask) != 0;

    /// <summary>Bytes captured in the FIFO.</summary>
    public int FifoLength()
    {
        int s1 = ReadRegister(RegFifoSize1);
        int s2 = ReadRegister(RegFifoSize2);
        int s3 = ReadRegister(RegFifoSize3) & 0x7F;
        return ((s3 << 16) | (s2 << 8) | s1) & FifoLengthMask;
    }

    /// <summary>Clear, start and wait for one capture. Returns the FIFO length, or -1 on timeout.</summary>
    public int Capture()
    {
        ClearFifo();
        StartCapture();
        int polls = 0;
        while (!CaptureDone())
        {
            if (++polls > CapturePolls)
            {
                return -1;
            }
            Timing.SleepMs(1);
        }
        return FifoLength();
    }

    /// <summary>
    /// Burst-read <paramref name="count"/> FIFO bytes in one SPI transaction
    /// (command 0x3C, then data). Successive bursts continue where the last
    /// one stopped.
    /// </summary>
    public byte[] ReadFifoBurst(int count)
    {
        int skip = SkipFirstBurstByte ? 1 : 0;
        byte[] tx = new byte[count + 1 + skip];
        tx[0] = BurstFifoRead;
        byte[] rx = _spi.Transfer(tx);
        byte[] r = new byte[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = rx[1 + skip + i];
        }
        SkipFirstBurstByte = false; // only the first burst after a capture carries it
        return r;
    }

    /// <summary>
    /// Capture and read a whole frame in <paramref name="chunkSize"/>-byte
    /// bursts. For JPEG, the image runs from FF D8 to FF D9 (the FIFO may hold
    /// padding after it, see <see cref="JpegLength"/>). Null on timeout.
    /// </summary>
    public byte[]? CaptureFrame(int chunkSize)
    {
        bool skip = SkipFirstBurstByte;
        int len = Capture();
        if (len <= 0)
        {
            return null;
        }
        byte[] frame = new byte[len];
        int at = 0;
        SkipFirstBurstByte = skip;
        while (at < len)
        {
            int n = len - at < chunkSize ? len - at : chunkSize;
            byte[] part = ReadFifoBurst(n);
            for (int i = 0; i < n; i++)
            {
                frame[at + i] = part[i];
            }
            at += n;
        }
        SkipFirstBurstByte = skip;
        return frame;
    }

    /// <summary>Length of the JPEG in <paramref name="data"/> up to and including the FF D9 end marker (-1 if no SOI/EOI).</summary>
    public static int JpegLength(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return -1;
        }
        for (int i = 2; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                return i + 2;
            }
        }
        return -1;
    }
}
