namespace LocoMP.Core.Net;

/// <summary>
/// Optional transport capability (M5.5): a transport whose peers carry a platform-authenticated
/// identity — today the Steam relay, whose far end is a SteamId64 verified by Valve's relay before
/// the first byte arrives. Anonymous transports (LiteNetLib UDP, Loopback) simply don't implement
/// this, and every consumer treats "no identity" as the normal case.
///
/// <para>This is deliberately a SEPARATE interface rather than a member on <see cref="ITransport"/>:
/// the seam's contract stays untouched (every existing adapter and test keeps compiling), and the
/// server discovers the capability with a type test at the one place it matters — the join gate's
/// persistent-ban check (U3) and the ban action's persistence write. A composite routes the question
/// to the inner link the peer actually lives on.</para>
/// </summary>
public interface IPeerIdentity
{
    /// <summary>The authenticated platform identity (SteamId64) of a connected peer, or null when the
    /// peer is unknown, already gone, or on a link that doesn't authenticate (UDP/Loopback).</summary>
    ulong? IdentityOf(int peerId);
}
