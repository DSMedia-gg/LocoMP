using System.IO;
using LocoMP.Core.Protocol;
using Xunit;

namespace LocoMP.Core.Tests;

public class PacketCodecTests
{
    [Fact]
    public void Primitives_round_trip_in_write_order()
    {
        byte[] bytes = new PacketWriter()
            .WriteByte(0xAB)
            .WriteVarUInt(300)          // multi-byte varint
            .WriteVarUInt(0)            // single-byte varint
            .WriteInt64(-1234567890123)
            .WriteSingle(3.14159f)
            .WriteString("Loco MP — ✓")  // multi-byte UTF-8
            .ToArray();

        var r = new PacketReader(bytes);
        Assert.Equal(0xAB, r.ReadByte());
        Assert.Equal(300u, r.ReadVarUInt());
        Assert.Equal(0u, r.ReadVarUInt());
        Assert.Equal(-1234567890123, r.ReadInt64());
        Assert.Equal(3.14159f, r.ReadSingle());
        Assert.Equal("Loco MP — ✓", r.ReadString());
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void Reading_past_the_end_throws_rather_than_returning_garbage()
    {
        byte[] bytes = new PacketWriter().WriteByte(1).ToArray();
        var r = new PacketReader(bytes);
        r.ReadByte();
        Assert.Throws<EndOfStreamException>(() => r.ReadInt64());
    }

    [Fact]
    public void A_hostile_string_length_prefix_is_rejected_by_the_cap()
    {
        // Declare a 1 MB string but provide no bytes — the cap must reject before allocating.
        byte[] bytes = new PacketWriter().WriteVarUInt(1_000_000).ToArray();
        var r = new PacketReader(bytes);
        Assert.Throws<InvalidDataException>(() => r.ReadString());
    }
}
