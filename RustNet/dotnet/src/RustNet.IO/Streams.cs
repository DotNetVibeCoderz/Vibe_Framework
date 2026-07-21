using System.Text;

namespace RustNet.IO;

/// <summary>
/// Growable in-memory byte stream (System.IO.MemoryStream shape, sized for
/// the RustNet runtime dialect: concrete class, no inheritance).
/// </summary>
public class MemoryStream
{
    private byte[] _buf;
    private int _length;
    private int _pos;

    public MemoryStream()
    {
        _buf = new byte[64];
        _length = 0;
        _pos = 0;
    }

    public MemoryStream(byte[] initial)
    {
        _buf = initial;
        _length = initial.Length;
        _pos = 0;
    }

    public int Length => _length;
    public int Position { get => _pos; set => _pos = value; }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
        {
            return;
        }
        int cap = _buf.Length * 2;
        if (cap < needed)
        {
            cap = needed;
        }
        byte[] bigger = new byte[cap];
        for (int i = 0; i < _length; i++)
        {
            bigger[i] = _buf[i];
        }
        _buf = bigger;
    }

    public void WriteByte(int value)
    {
        EnsureCapacity(_pos + 1);
        _buf[_pos] = (byte)value;
        _pos = _pos + 1;
        if (_pos > _length)
        {
            _length = _pos;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        EnsureCapacity(_pos + count);
        for (int i = 0; i < count; i++)
        {
            _buf[_pos + i] = data[offset + i];
        }
        _pos = _pos + count;
        if (_pos > _length)
        {
            _length = _pos;
        }
    }

    public void Write(byte[] data) => Write(data, 0, data.Length);

    /// <summary>-1 at end of stream.</summary>
    public int ReadByte()
    {
        if (_pos >= _length)
        {
            return -1;
        }
        int v = _buf[_pos];
        _pos = _pos + 1;
        return v;
    }

    public int Read(byte[] target, int offset, int count)
    {
        int n = 0;
        while (n < count && _pos < _length)
        {
            target[offset + n] = _buf[_pos];
            _pos = _pos + 1;
            n = n + 1;
        }
        return n;
    }

    public void Seek(int position)
    {
        _pos = position;
    }

    public byte[] ToArray()
    {
        byte[] copy = new byte[_length];
        for (int i = 0; i < _length; i++)
        {
            copy[i] = _buf[i];
        }
        return copy;
    }
}

/// <summary>
/// File-backed stream over the device filesystem. Reads snapshot the file;
/// writes are buffered and land on Flush()/Close().
/// </summary>
public class FileStream
{
    private readonly string _path;
    private readonly MemoryStream _mem;
    private bool _dirty;

    public FileStream(string path, bool truncate)
    {
        _path = path;
        if (!truncate && FileSystem.Exists(path))
        {
            _mem = new MemoryStream(FileSystem.ReadAllBytes(path));
        }
        else
        {
            _mem = new MemoryStream();
        }
        _dirty = truncate;
    }

    public static FileStream OpenRead(string path) => new FileStream(path, false);
    public static FileStream Create(string path) => new FileStream(path, true);

    public int Length => _mem.Length;
    public int Position { get => _mem.Position; set => _mem.Position = value; }

    public int ReadByte() => _mem.ReadByte();
    public int Read(byte[] target, int offset, int count) => _mem.Read(target, offset, count);

    public void WriteByte(int value)
    {
        _mem.WriteByte(value);
        _dirty = true;
    }

    public void Write(byte[] data, int offset, int count)
    {
        _mem.Write(data, offset, count);
        _dirty = true;
    }

    public void Write(byte[] data) => Write(data, 0, data.Length);

    public void Flush()
    {
        if (_dirty)
        {
            FileSystem.WriteAllBytes(_path, _mem.ToArray());
            _dirty = false;
        }
    }

    public void Close() => Flush();
}

/// <summary>
/// Little-endian primitive packer/unpacker over a MemoryStream
/// (BinaryWriter/BinaryReader shape).
/// </summary>
public class BinaryPacker
{
    private readonly MemoryStream _stream;

    public BinaryPacker(MemoryStream stream)
    {
        _stream = stream;
    }

    public MemoryStream Stream => _stream;

    public void WriteInt(int v)
    {
        _stream.WriteByte(v);
        _stream.WriteByte(v >> 8);
        _stream.WriteByte(v >> 16);
        _stream.WriteByte(v >> 24);
    }

    public void WriteShort(int v)
    {
        _stream.WriteByte(v);
        _stream.WriteByte(v >> 8);
    }

    public void WriteString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        WriteInt(utf8.Length);
        _stream.Write(utf8);
    }

    public int ReadInt()
    {
        int a = _stream.ReadByte();
        int b = _stream.ReadByte();
        int c = _stream.ReadByte();
        int d = _stream.ReadByte();
        return a | (b << 8) | (c << 16) | (d << 24);
    }

    public int ReadShort()
    {
        int a = _stream.ReadByte();
        int b = _stream.ReadByte();
        return a | (b << 8);
    }

    public string ReadString()
    {
        int len = ReadInt();
        byte[] utf8 = new byte[len];
        _stream.Read(utf8, 0, len);
        return Encoding.UTF8.GetString(utf8);
    }
}
