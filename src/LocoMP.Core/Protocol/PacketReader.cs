using System;
using System.IO;
using System.Text;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Bounds-checked reader mirroring <see cref="PacketWriter"/>. Every packet is treated as UNTRUSTED
/// (03 §9): a read past the end throws <see cref="EndOfStreamException"/> rather than returning
/// garbage, varints are length-bounded, and a string's declared length is capped so a hostile prefix
/// cannot trigger a huge allocation. Callers wrap dispatch in try/catch and simply drop bad packets.
/// </summary>
public sealed class PacketReader
{
    private readonly byte[] _buf;
    private int _pos;

    /// <summary>Hard cap on one string's byte length (03 §9 size caps). Presence strings are tiny.</summary>
    public const int MaxStringBytes = 4096;

    public PacketReader(byte[] buffer) => _buf = buffer ?? throw new ArgumentNullException(nameof(buffer));

    public bool AtEnd => _pos >= _buf.Length;

    public byte ReadByte()
    {
        Require(1);
        return _buf[_pos++];
    }

    public uint ReadVarUInt()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            if (shift > 28) throw new InvalidDataException("varint too long for uint32");
            byte b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    public long ReadInt64()
    {
        Require(8);
        long value = 0;
        for (int i = 0; i < 8; i++) value |= (long)_buf[_pos++] << (8 * i);
        return value;
    }

    public float ReadSingle()
    {
        Require(4);
        if (BitConverter.IsLittleEndian)
        {
            float v = BitConverter.ToSingle(_buf, _pos);
            _pos += 4;
            return v;
        }
        byte[] tmp = { _buf[_pos + 3], _buf[_pos + 2], _buf[_pos + 1], _buf[_pos] };
        _pos += 4;
        return BitConverter.ToSingle(tmp, 0);
    }

    public string ReadString()
    {
        uint len = ReadVarUInt();
        if (len > MaxStringBytes) throw new InvalidDataException($"string length {len} exceeds cap {MaxStringBytes}");
        Require((int)len);
        string s = Encoding.UTF8.GetString(_buf, _pos, (int)len);
        _pos += (int)len;
        return s;
    }

    private void Require(int count)
    {
        if (_pos + count > _buf.Length) throw new EndOfStreamException("packet truncated");
    }
}
