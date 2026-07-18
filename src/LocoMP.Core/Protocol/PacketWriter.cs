using System;
using System.Collections.Generic;
using System.Text;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Minimal binary writer for LocoMP's hand-rolled control-plane packets (hard rule 4 — hot/control
/// paths are hand-rolled; MessagePack is reserved for bulk from M2). Everything is written
/// little-endian on the wire regardless of host endianness, so a big-endian peer still interops.
/// Pair with <see cref="PacketReader"/>: the read order must mirror the write order exactly.
/// The fluent returns let a whole message be composed in one expression.
/// </summary>
public sealed class PacketWriter
{
    private readonly List<byte> _buf;

    public PacketWriter(int capacity = 64) => _buf = new List<byte>(capacity);

    public PacketWriter WriteByte(byte value)
    {
        _buf.Add(value);
        return this;
    }

    /// <summary>Unsigned LEB128 varint — compact for the small ids and counts presence traffic carries.</summary>
    public PacketWriter WriteVarUInt(uint value)
    {
        while (value >= 0x80)
        {
            _buf.Add((byte)(value | 0x80));
            value >>= 7;
        }
        _buf.Add((byte)value);
        return this;
    }

    /// <summary>Fixed 8-byte little-endian — used for the server clock (monotonic ms, 03 §5).</summary>
    public PacketWriter WriteInt64(long value)
    {
        for (int i = 0; i < 8; i++)
        {
            _buf.Add((byte)(value & 0xFF));
            value >>= 8;
        }
        return this;
    }

    public PacketWriter WriteSingle(float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        _buf.AddRange(bytes);
        return this;
    }

    /// <summary>Length-prefixed (varint) UTF-8. Reader caps the length so a hostile prefix can't over-allocate.</summary>
    public PacketWriter WriteString(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUInt((uint)bytes.Length);
        _buf.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => _buf.ToArray();
}
