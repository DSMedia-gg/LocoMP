using System;
using System.Globalization;
using System.Reflection;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Shim;
using LocoMP.Transport;
using UnityEngine;

namespace LocoMP;

/// <summary>
/// The client-session state machine behind the UMM panel: Idle → Hosting (embedded NetServer over a
/// CompositeTransport of Loopback + UDP, own player = client #1 on the Loopback link, 03 §6) or
/// Idle → Joined (NetClient over UDP to someone else's host). All game access goes through the Shim
/// facade (hard rule 3); this class only pumps Core objects and draws IMGUI. The daily rig for
/// M1.3: host in-game, point tools/LocoMP.Bot at the logged coordinates, watch its avatar orbit.
/// </summary>
public sealed class SessionController
{
    private const double PoseSendIntervalSeconds = 1.0 / 20; // matches 02's presence rate + the bot's default
    private const double TimeSyncIntervalSeconds = 5.0;

    private enum Mode { Idle, Hosting, Joined }

    private readonly Action<string> _log;
    private readonly IClock _clock = new SystemClock();
    private readonly AvatarManager _avatars = new();

    private Mode _mode = Mode.Idle;
    private NetServer? _server;
    private CompositeTransport? _serverTransport;
    private LoopbackNetwork? _hub;
    private NetClient? _client;
    private ITransport? _clientTransport;

    private double _poseAccum;
    private double _timeAccum;
    private string _lastError = "";

    // IMGUI field state
    private string _playerName = Environment.UserName;
    private string _address = "127.0.0.1";
    private string _portText = NetDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string _password = "";

    public SessionController(Action<string> log) => _log = log;

    private static string ModVersion
    {
        get
        {
            string v = typeof(SessionController).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v.Substring(0, plus) : v;
        }
    }

    private static HandshakeRequest Identity() =>
        // modListHash deliberately empty for M1: single-mod era; real manifest hashing arrives with
        // the Mod API channel negotiation (04). The bot presents the same default, so they match.
        new(ProtocolVersion.Current, PresenceShim.GameBuild, ModVersion, "");

    /// <summary>Pump everything. Called from UMM OnUpdate while the mod is enabled.</summary>
    public void Update(double dt)
    {
        _server?.Poll();
        _client?.Poll();

        if (_client is { Joined: true })
        {
            _poseAccum += dt;
            if (_poseAccum >= PoseSendIntervalSeconds)
            {
                _poseAccum = 0;
                if (PresenceShim.TryCaptureLocalPose(out var pose)) _client.SendPose(pose);
            }
        }

        if (_mode == Mode.Hosting && _server != null)
        {
            _timeAccum += dt;
            if (_timeAccum >= TimeSyncIntervalSeconds)
            {
                _timeAccum = 0;
                _server.BroadcastTime();
            }
        }

        _avatars.Tick((float)dt);
    }

    /// <summary>The session panel, drawn inside UMM's mod options (Ctrl+F10 → LocoMP).</summary>
    public void OnGUI()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        switch (_mode)
        {
            case Mode.Idle:
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name", GUILayout.Width(70));
                _playerName = GUILayout.TextField(_playerName, GUILayout.Width(180));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(70));
                _portText = GUILayout.TextField(_portText, GUILayout.Width(80));
                GUILayout.Label("Password", GUILayout.Width(70));
                _password = GUILayout.TextField(_password, GUILayout.Width(120));
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Host session", GUILayout.Width(160))) Host();

                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Address", GUILayout.Width(70));
                _address = GUILayout.TextField(_address, GUILayout.Width(180));
                if (GUILayout.Button("Join", GUILayout.Width(80))) Join();
                GUILayout.EndHorizontal();
                break;

            case Mode.Hosting:
            case Mode.Joined:
                string role = _mode == Mode.Hosting ? $"Hosting on UDP {_portText}" : $"Joined {_address}:{_portText}";
                GUILayout.Label($"{role} — {(_client is { Joined: true } ? "connected" : "connecting…")}" +
                                (_mode == Mode.Hosting && _server != null ? $" — {_server.PlayerCount} player(s)" : ""));

                if (_client != null)
                {
                    foreach (var p in _client.Players.Values)
                        GUILayout.Label($"  • {p.Name} (id {p.Id}) @ {p.Pose}");
                }
                if (GUILayout.Button("Leave", GUILayout.Width(100))) Leave();
                break;
        }

        if (_lastError.Length > 0) GUILayout.Label("⚠ " + _lastError);
        GUILayout.EndVertical();
    }

    private void Host()
    {
        try
        {
            _lastError = "";
            int port = ParsePort();
            _hub = new LoopbackNetwork();
            var udp = LiteNetLibTransport.StartServer(port, NetDefaults.ConnectKey);
            _serverTransport = new CompositeTransport(_hub.Server, udp);
            _server = new NetServer(_serverTransport,
                new ServerConfig(Identity(), _password.Length > 0 ? _password : null), _clock);
            _server.PlayerAdmitted += p => _log($"[session] admitted {p.Name} (id {p.Id}) — {_server!.PlayerCount} player(s)");
            _server.PlayerRemoved += id => _log($"[session] removed id {id} — {_server!.PlayerCount} player(s)");

            _client = MakeClient(_hub.Connect(out _)); // the host is just client #1, zero latency
            _mode = Mode.Hosting;

            _log($"[session] hosting on UDP {port} (game reports version '{PresenceShim.ReportedGameVersion}', handshake build '{PresenceShim.GameBuild}')");
            if (PresenceShim.TryCaptureLocalPose(out var here))
                _log($"[session] your absolute position: --at {here.Px:F0},{here.Py:F0},{here.Pz:F0}  ← paste into LocoMP.Bot");
        }
        catch (Exception e)
        {
            _lastError = $"host failed: {e.Message}";
            _log("[session] " + _lastError);
            Leave();
        }
    }

    private void Join()
    {
        try
        {
            _lastError = "";
            _clientTransport = LiteNetLibTransport.ConnectClient(_address, ParsePort(), NetDefaults.ConnectKey);
            _client = MakeClient(_clientTransport);
            _mode = Mode.Joined;
            _log($"[session] joining {_address}:{_portText}…");
        }
        catch (Exception e)
        {
            _lastError = $"join failed: {e.Message}";
            _log("[session] " + _lastError);
            Leave();
        }
    }

    private NetClient MakeClient(ITransport transport)
    {
        var client = new NetClient(transport, Identity(),
            _playerName.Length > 0 ? _playerName : "Player", _clock,
            _password.Length > 0 ? _password : null);
        client.Accepted += id => _log($"[session] joined as id {id} (server offset {client.ServerTimeOffsetMs} ms)");
        client.Rejected += reason => { _lastError = reason; _log($"[session] REJECTED: {reason}"); };
        client.PlayerJoined += p => { _avatars.AddOrUpdate(p.Id, p.Name, p.Pose); _log($"[session] player joined: {p.Name} (id {p.Id})"); };
        client.PlayerLeft += id => { _avatars.Remove(id); _log($"[session] player left: id {id}"); };
        client.PlayerMoved += (id, pose) => _avatars.Move(id, pose);
        return client;
    }

    /// <summary>Tear the whole session down (also called on mod toggle-off). Safe when idle.</summary>
    public void Leave()
    {
        if (_client is { Joined: true }) { _client.Leave(); _client.Poll(); }
        _client?.Dispose();
        _clientTransport?.Dispose();
        _server?.Dispose();
        _serverTransport?.Dispose(); // composite disposes the hub's server endpoint + the UDP socket
        _client = null;
        _clientTransport = null;
        _server = null;
        _serverTransport = null;
        _hub = null;
        _avatars.Clear();
        _mode = Mode.Idle;
    }

    private int ParsePort() =>
        int.TryParse(_portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536
            ? p
            : NetDefaults.Port;
}
