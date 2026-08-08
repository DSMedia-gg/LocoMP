using System;
using System.Collections.Generic;
using System.Globalization;
using Steamworks;

namespace LocoMP.Net;

/// <summary>One friend row for the Active-Friends panel (M5.5): a Steam friend currently in Derail
/// Valley with LocoMP loaded. <see cref="JoinableHost"/> is the SteamId64 of the session they're in
/// (join target), or null when they're just in-game with the mod.</summary>
internal readonly struct FriendEntry
{
    public FriendEntry(ulong id, string name, ulong? joinableHost)
    {
        Id = id;
        Name = name;
        JoinableHost = joinableHost;
    }

    public ulong Id { get; }
    public string Name { get; }
    public ulong? JoinableHost { get; }
}

/// <summary>
/// Steam rich-presence plumbing (M5.5, 10-plan §M5.5): the "in LocoMP" marker + the connect string
/// that makes the overlay's <i>Join Game</i> work, the friends scan behind the Active-Friends panel,
/// and overlay invites. All through the game's own Steam client — read-only against Steam state
/// except for OUR presence keys.
///
/// <para><b>Connect string.</b> <c>+locomp &lt;hostSteamId64&gt;</c>, set for host AND guests — a
/// friend can join the session through any member, and the string always names the HOST (the relay
/// listen socket). Steam fires <see cref="SteamFriends.OnGameRichPresenceJoinRequested"/> on the
/// receiving side; that arrives on a background thread, so it lands in a pending slot the
/// controller's Update drains on the main thread.</para>
/// </summary>
internal static class SteamPresence
{
    private const string MarkerKey = "locomp";   // presence: this player runs LocoMP right now
    private const string ConnectKey = "connect"; // Steam's own join-game key
    private const string ConnectPrefix = "+locomp ";

    private static readonly object Gate = new();
    private static ulong? _pendingJoin;
    private static bool _installed;

    /// <summary>True once the overlay-join hook is live. False after a too-early Install — the
    /// retry loop in Main.OnUpdate keys off this (R5-1: DV initialises Steamworks ASYNC, frames
    /// AFTER mods load, so the load-time Install silently missed on every normal launch).</summary>
    public static bool Installed => _installed;

    /// <summary>Steam up (game launched through it) AND the Facepunch layer initialized. False on a
    /// Steam-less launch — every caller degrades to the UDP-only behaviour.</summary>
    public static bool Available
    {
        get
        {
            try { return SteamClient.IsValid; }
            catch (Exception) { return false; } // a half-dead Steam layer must never kill a session path
        }
    }

    public static ulong LocalSteamId => SteamClient.SteamId;

    /// <summary>Hook the overlay join event (idempotent). Called at mod load; the handler only
    /// stashes — the game thread picks the request up via <see cref="TryTakeJoinRequest"/>.</summary>
    public static void Install(Action<string> log)
    {
        if (_installed || !Available) return;
        _installed = true;
        SteamFriends.OnGameRichPresenceJoinRequested += OnJoinRequested;
        log("[steam] presence installed — overlay joins land in the MULTIPLAYER screen");
    }

    public static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { SteamFriends.OnGameRichPresenceJoinRequested -= OnJoinRequested; }
        catch (Exception) { /* Steam layer already torn down with the game */ }
        lock (Gate) _pendingJoin = null;
    }

    private static void OnJoinRequested(Friend friend, string connect)
    {
        if (!TryParseConnect(connect, out ulong host)) return;
        lock (Gate) _pendingJoin = host; // latest wins; background thread — never touch game state here
    }

    /// <summary>Drain the pending overlay-join request (main thread, once per Update).</summary>
    public static bool TryTakeJoinRequest(out ulong hostSteamId)
    {
        lock (Gate)
        {
            if (_pendingJoin is { } host)
            {
                _pendingJoin = null;
                hostSteamId = host;
                return true;
            }
        }
        hostSteamId = 0;
        return false;
    }

    /// <summary>Presence for a live session: the marker, a status line, and the connect string that
    /// lights up "Join Game" on our friends' side.</summary>
    public static void SetSessionPresence(ulong hostSteamId, bool hosting)
    {
        if (!Available) return;
        SteamFriends.SetRichPresence(MarkerKey, "1");
        SteamFriends.SetRichPresence("status", hosting ? "Hosting a LocoMP session" : "In a LocoMP session");
        SteamFriends.SetRichPresence(ConnectKey, ConnectPrefix + hostSteamId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Presence outside a session: still discoverable as "runs LocoMP", but not joinable.</summary>
    public static void SetIdlePresence()
    {
        if (!Available) return;
        SteamFriends.ClearRichPresence();
        SteamFriends.SetRichPresence(MarkerKey, "1");
        SteamFriends.SetRichPresence("status", "Derail Valley with LocoMP");
    }

    public static void ClearPresence()
    {
        if (!Available) return;
        try { SteamFriends.ClearRichPresence(); }
        catch (Exception) { /* leaving during game shutdown */ }
    }

    /// <summary>Scan the friends list for LocoMP players (the Active-Friends panel body). A friends
    /// list walk is a native call per friend — callers poll on a coarse cadence, never per frame.</summary>
    public static List<FriendEntry> ActiveFriends()
    {
        var result = new List<FriendEntry>();
        if (!Available) return result;
        foreach (Friend friend in SteamFriends.GetFriends())
        {
            if (!friend.IsPlayingThisGame) continue;
            if (friend.GetRichPresence(MarkerKey) is null) continue; // in DV, but not modded
            ulong? joinable = TryParseConnect(friend.GetRichPresence(ConnectKey), out ulong host)
                ? host : (ulong?)null;
            result.Add(new FriendEntry(friend.Id, friend.Name, joinable));
        }
        return result;
    }

    /// <summary>Overlay-invite a friend into the session hosted by <paramref name="hostSteamId"/>.</summary>
    public static bool Invite(ulong friendId, ulong hostSteamId)
    {
        if (!Available) return false;
        return new Friend(friendId).InviteToGame(
            ConnectPrefix + hostSteamId.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParseConnect(string? connect, out ulong hostSteamId)
    {
        hostSteamId = 0;
        if (connect is null || !connect.StartsWith(ConnectPrefix, StringComparison.Ordinal)) return false;
        return ulong.TryParse(connect.Substring(ConnectPrefix.Length).Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out hostSteamId) && hostSteamId != 0;
    }
}
