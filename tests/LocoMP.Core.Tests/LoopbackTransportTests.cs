using LocoMP.Core.Net;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

public class LoopbackTransportTests
{
    [Fact]
    public void Payload_sent_on_one_end_is_delivered_to_the_other_on_poll()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        byte[]? received = null;
        b.Received += (_, payload) => received = payload;

        a.Send(peerId: 0, payload: new byte[] { 1, 2, 3 }, DeliveryMethod.ReliableOrdered);
        Assert.Null(received); // nothing delivered until the peer pumps its events...
        b.Poll();

        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }

    [Fact]
    public void Sender_mutating_its_buffer_after_send_does_not_affect_the_delivered_copy()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        byte[]? received = null;
        b.Received += (_, payload) => received = payload;

        var buffer = new byte[] { 9 };
        a.Send(0, buffer, DeliveryMethod.SequencedUnreliable);
        buffer[0] = 42; // mutate after send
        b.Poll();

        Assert.Equal(new byte[] { 9 }, received);
    }
}
