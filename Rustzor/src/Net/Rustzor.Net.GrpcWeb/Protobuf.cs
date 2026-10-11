using System.Text;

namespace Rustzor.Net.GrpcWeb;

/// <summary>
/// A protobuf message written by hand: <see cref="WriteTo"/> and
/// <see cref="ReadFrom"/> follow the field numbers in the .proto file. There
/// is no code generator in the browser; a message is a few lines per field.
/// </summary>
/// <example>
/// <code>
/// // message Item { int32 id = 1; string name = 2; double price = 3; }
/// public sealed class Item : IProtoMessage
/// {
///     public int Id; public string Name = ""; public double Price;
///     public void WriteTo(ProtoWriter w) { w.Int32(1, Id); w.String(2, Name); w.Double(3, Price); }
///     public void ReadFrom(ProtoReader r)
///     {
///         while (r.Next())
///             switch (r.Field) { case 1: Id = r.Int32(); break; case 2: Name = r.String(); break; case 3: Price = r.Double(); break; default: r.Skip(); break; }
///     }
/// }
/// </code>
/// </example>
public interface IProtoMessage
{
    void WriteTo(ProtoWriter writer);
    void ReadFrom(ProtoReader reader);
}

public enum WireType { Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5 }

/// <summary>Writes protobuf's binary encoding. Default values (0, "", false) are skipped, as proto3 does.</summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _bytes = new();

    public byte[] ToArray() => _bytes.ToArray();

    public static byte[] Encode(IProtoMessage message)
    {
        var w = new ProtoWriter();
        message.WriteTo(w);
        return w.ToArray();
    }

    private void Tag(int field, WireType type) => Varint((ulong)((field << 3) | (int)type));

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        _bytes.Add((byte)value);
    }

    private void Fixed64(ulong value)
    {
        for (var i = 0; i < 8; i++) _bytes.Add((byte)(value >> (8 * i)));
    }

    private void Fixed32(uint value)
    {
        for (var i = 0; i < 4; i++) _bytes.Add((byte)(value >> (8 * i)));
    }

    // Negative int32 and int64 take ten bytes on the wire, as the spec says.
    public void Int32(int field, int value) { if (value != 0) { Tag(field, WireType.Varint); Varint((ulong)(long)value); } }
    public void Int64(int field, long value) { if (value != 0) { Tag(field, WireType.Varint); Varint((ulong)value); } }
    public void UInt32(int field, uint value) { if (value != 0) { Tag(field, WireType.Varint); Varint(value); } }
    public void UInt64(int field, ulong value) { if (value != 0) { Tag(field, WireType.Varint); Varint(value); } }
    public void SInt32(int field, int value) { if (value != 0) { Tag(field, WireType.Varint); Varint((uint)((value << 1) ^ (value >> 31))); } }
    public void SInt64(int field, long value) { if (value != 0) { Tag(field, WireType.Varint); Varint((ulong)((value << 1) ^ (value >> 63))); } }
    public void Bool(int field, bool value) { if (value) { Tag(field, WireType.Varint); _bytes.Add(1); } }
    public void Enum(int field, int value) => Int32(field, value);
    public void Double(int field, double value) { if (value != 0) { Tag(field, WireType.Fixed64); Fixed64((ulong)BitConverter.DoubleToInt64Bits(value)); } }
    public void Float(int field, float value) { if (value != 0) { Tag(field, WireType.Fixed32); Fixed32((uint)BitConverter.SingleToInt32Bits(value)); } }

    public void String(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Bytes(field, Encoding.UTF8.GetBytes(value));
    }

    public void Bytes(int field, byte[]? value)
    {
        if (value is null || value.Length == 0) return;
        Tag(field, WireType.LengthDelimited);
        Varint((ulong)value.Length);
        _bytes.AddRange(value);
    }

    /// <summary>A nested message; null is left out.</summary>
    public void Message(int field, IProtoMessage? value)
    {
        if (value is null) return;
        Tag(field, WireType.LengthDelimited);
        var inner = Encode(value);
        Varint((ulong)inner.Length);
        _bytes.AddRange(inner);
    }

    public void Messages<T>(int field, IEnumerable<T> values) where T : IProtoMessage
    {
        foreach (var v in values) Message(field, v);
    }

    public void Strings(int field, IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            Tag(field, WireType.LengthDelimited);
            var b = Encoding.UTF8.GetBytes(v);
            Varint((ulong)b.Length);
            _bytes.AddRange(b);
        }
    }

    /// <summary>Repeated int32, packed (proto3's default).</summary>
    public void PackedInt32(int field, IEnumerable<int> values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values) inner.Varint((ulong)(long)v);
        if (inner._bytes.Count == 0) return;
        Tag(field, WireType.LengthDelimited);
        Varint((ulong)inner._bytes.Count);
        _bytes.AddRange(inner._bytes);
    }

    /// <summary>Repeated double, packed.</summary>
    public void PackedDouble(int field, IEnumerable<double> values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values) inner.Fixed64((ulong)BitConverter.DoubleToInt64Bits(v));
        if (inner._bytes.Count == 0) return;
        Tag(field, WireType.LengthDelimited);
        Varint((ulong)inner._bytes.Count);
        _bytes.AddRange(inner._bytes);
    }
}

/// <summary>Reads protobuf's binary encoding: call <see cref="Next"/>, switch on <see cref="Field"/>, read or <see cref="Skip"/>.</summary>
public sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;

    public ProtoReader(byte[] data) : this(data, 0, data.Length) { }

    private ProtoReader(byte[] data, int start, int end)
    {
        _data = data;
        _pos = start;
        _end = end;
    }

    public int Field { get; private set; }
    public WireType Type { get; private set; }

    public static T Decode<T>(byte[] data) where T : IProtoMessage, new()
    {
        var m = new T();
        m.ReadFrom(new ProtoReader(data));
        return m;
    }

    /// <summary>Moves to the next field; false at the end of the message.</summary>
    public bool Next()
    {
        if (_pos >= _end) return false;
        var tag = Varint();
        Field = (int)(tag >> 3);
        Type = (WireType)(tag & 7);
        return true;
    }

    private ulong Varint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_pos >= _end) throw new FormatException("Truncated varint.");
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new FormatException("Varint is longer than ten bytes.");
    }

    private ulong Fixed64()
    {
        if (_pos + 8 > _end) throw new FormatException("Truncated fixed64.");
        ulong v = 0;
        for (var i = 0; i < 8; i++) v |= (ulong)_data[_pos++] << (8 * i);
        return v;
    }

    private uint Fixed32()
    {
        if (_pos + 4 > _end) throw new FormatException("Truncated fixed32.");
        uint v = 0;
        for (var i = 0; i < 4; i++) v |= (uint)_data[_pos++] << (8 * i);
        return v;
    }

    private (int Start, int Length) Span()
    {
        var length = (int)Varint();
        if (_pos + length > _end) throw new FormatException("Truncated length-delimited field.");
        var start = _pos;
        _pos += length;
        return (start, length);
    }

    public int Int32() => (int)Varint();
    public long Int64() => (long)Varint();
    public uint UInt32() => (uint)Varint();
    public ulong UInt64() => Varint();
    public int SInt32() { var v = (uint)Varint(); return (int)(v >> 1) ^ -(int)(v & 1); }
    public long SInt64() { var v = Varint(); return (long)(v >> 1) ^ -(long)(v & 1); }
    public bool Bool() => Varint() != 0;
    public int Enum() => Int32();
    public double Double() => BitConverter.Int64BitsToDouble((long)Fixed64());
    public float Float() => BitConverter.Int32BitsToSingle((int)Fixed32());

    public string String()
    {
        var (start, length) = Span();
        return Encoding.UTF8.GetString(_data, start, length);
    }

    public byte[] Bytes()
    {
        var (start, length) = Span();
        var b = new byte[length];
        Array.Copy(_data, start, b, 0, length);
        return b;
    }

    public T Message<T>() where T : IProtoMessage, new()
    {
        var (start, length) = Span();
        var m = new T();
        m.ReadFrom(new ProtoReader(_data, start, start + length));
        return m;
    }

    /// <summary>Repeated int32, packed or not.</summary>
    public void Int32s(List<int> into)
    {
        if (Type != WireType.LengthDelimited) { into.Add(Int32()); return; }
        var (start, length) = Span();
        var inner = new ProtoReader(_data, start, start + length);
        while (inner._pos < inner._end) into.Add((int)inner.Varint());
    }

    /// <summary>Repeated double, packed or not.</summary>
    public void Doubles(List<double> into)
    {
        if (Type != WireType.LengthDelimited) { into.Add(Double()); return; }
        var (start, length) = Span();
        var inner = new ProtoReader(_data, start, start + length);
        while (inner._pos < inner._end) into.Add(BitConverter.Int64BitsToDouble((long)inner.Fixed64()));
    }

    /// <summary>Skips a field this reader does not know: newer servers may add fields.</summary>
    public void Skip()
    {
        switch (Type)
        {
            case WireType.Varint: Varint(); break;
            case WireType.Fixed64: _pos += 8; break;
            case WireType.Fixed32: _pos += 4; break;
            case WireType.LengthDelimited: Span(); break;
            default: throw new FormatException($"Unsupported wire type {(int)Type}.");
        }
    }
}
