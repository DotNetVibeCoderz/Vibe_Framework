using RustNet.Core;

namespace RustNet.Buses;

/// <summary>
/// Modbus master (RTU framing on the device side; the frame layer lives in
/// Rust for wire-speed encode/decode). Registers are 16-bit; multi-register
/// payloads travel big-endian, matching the wire format.
/// </summary>
public static class Modbus
{
    /// <summary>Raw big-endian register bytes; prefer <see cref="ReadHoldingRegisters"/>.</summary>
    [InternalCall]
    public static byte[] ReadHolding(int unit, int address, int count) => throw new RuntimeOnlyException();

    [InternalCall]
    public static byte[] ReadInput(int unit, int address, int count) => throw new RuntimeOnlyException();

    /// <summary>One byte per coil: 0 or 1.</summary>
    [InternalCall]
    public static byte[] ReadCoils(int unit, int address, int count) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void WriteCoil(int unit, int address, bool on) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void WriteRegister(int unit, int address, int value) => throw new RuntimeOnlyException();

    /// <summary>Raw big-endian register bytes; prefer <see cref="WriteRegisters"/>.</summary>
    [InternalCall]
    public static void WriteRegistersRaw(int unit, int address, byte[] beWords) => throw new RuntimeOnlyException();

    public static int[] ReadHoldingRegisters(int unit, int address, int count)
        => DecodeWords(ReadHolding(unit, address, count));

    public static int[] ReadInputRegisters(int unit, int address, int count)
        => DecodeWords(ReadInput(unit, address, count));

    public static void WriteRegisters(int unit, int address, int[] values)
    {
        byte[] be = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            be[i * 2] = (byte)(values[i] >> 8);
            be[i * 2 + 1] = (byte)values[i];
        }
        WriteRegistersRaw(unit, address, be);
    }

    private static int[] DecodeWords(byte[] be)
    {
        int[] words = new int[be.Length / 2];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = (be[i * 2] << 8) | be[i * 2 + 1];
        }
        return words;
    }
}
