using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
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
    private TrainSync? _trains;
    private CabControlSync? _cabControls;

    private double _poseAccum;
    private double _timeAccum;
    private string _lastError = "";
    private bool _worldUnloaded;

    // M3 career state
    private string? _playerKey;
    private Autosaver? _autosaver;
    private JobCapture? _jobCapture;
    private LicenseSync? _licenseSync;
    private WalletMirror? _walletMirror;
    private string _careerToast = "";

    // IMGUI field state
    private string _playerName = Environment.UserName;
    private string _address = "127.0.0.1";
    private string _portText = NetDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string _password = "";
    private bool _sharedCareer;
    private bool _freshCareer;
    private bool _showShop;
    private bool _showGrant;
    private int _grantTarget;
    private Vector2 _jobsScroll;
    private Vector2 _grantScroll;

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

        _trains?.Tick(dt);
        _cabControls?.Tick((float)dt);
        _walletMirror?.Tick(dt);
        _autosaver?.Tick();
        if (_worldUnloaded)
        {
            // Flagged from inside the tick; tear down afterwards so we never dispose mid-callback.
            _worldUnloaded = false;
            _log("[session] game world unloaded — session closed (host again once the new world is up)");
            _lastError = "world unloaded — session closed";
            Leave();
            return;
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

                GUILayout.BeginHorizontal();
                _sharedCareer = GUILayout.Toggle(_sharedCareer, "Shared career (classic co-op)");
                _freshCareer = GUILayout.Toggle(_freshCareer, "Fresh career (ignore saved)");
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
                DrawCareer();
                if (GUILayout.Button("Leave", GUILayout.Width(100))) Leave();
                break;
        }

        if (_lastError.Length > 0) GUILayout.Label("⚠ " + _lastError);
        GUILayout.EndVertical();
    }

    /// <summary>The M3 career section: wallet + licenses, my claims with a report button for the
    /// next step, the board, and the license shop. Everything here only SENDS proposals — all the
    /// state it draws came back from the server (03 §3).</summary>
    private void DrawCareer()
    {
        if (_client is not { Joined: true }) return;
        ClientCareer career = _client.Career;

        GUILayout.Space(4);
        string licenses = career.Licenses.Count == 0 ? "none" : string.Join(", ", career.Licenses.ToArray());
        string preset = career.Preset == ProgressionPreset.SharedCareer ? "shared career" : "per-player careers";
        GUILayout.Label($"Wallet: {Money(career.BalanceCents)}   Licenses: {licenses}   ({preset})");

        int myId = _client.LocalId!.Value;
        foreach (ClientJob job in career.Jobs.Values.Where(j => j.State == JobLifecycle.Claimed && j.ClaimantPeerId == myId).ToList())
        {
            JobTaskDef task = job.Def.Tasks[Math.Min(job.NextTaskIndex, job.Def.Tasks.Count - 1)];
            GUILayout.BeginHorizontal();
            if (job.Def.GameId.Length > 0 && _mode == Mode.Hosting)
            {
                // Host-claimed captured job: the booklet is the claim and the validator is the
                // turn-in — the panel only mirrors it (D13 native UX).
                GUILayout.Label($"MY JOB {Describe(job.Def)} — turn in at the {job.Def.Destination} validator");
            }
            else if (job.Def.GameId.Length > 0)
            {
                // Remote claim on a captured job (M3.5c): the report becomes a completion query
                // the host answers from the game's own task tree. The task param carries the
                // booklet's essence — the actual tracks — captured from the native job.
                GUILayout.Label($"MY JOB {Describe(job.Def)} — {task.Param}");
                if (GUILayout.Button("Report delivery", GUILayout.Width(130)))
                    career.ReportTask(job.Def.Id, job.NextTaskIndex);
            }
            else
            {
                GUILayout.Label($"MY JOB {Describe(job.Def)} — next: {task.Kind} @ {task.Param}");
                if (GUILayout.Button($"Report {task.Kind}", GUILayout.Width(130)))
                    career.ReportTask(job.Def.Id, job.NextTaskIndex);
            }
            if (GUILayout.Button("Abandon", GUILayout.Width(80)))
                career.AbandonJob(job.Def.Id);
            GUILayout.EndHorizontal();
        }

        var available = career.Jobs.Values.Where(j => j.State == JobLifecycle.Available)
            .OrderBy(j => j.Def.Id).ToList();
        var others = career.Jobs.Values.Where(j => j.State == JobLifecycle.Claimed && j.ClaimantPeerId != myId).ToList();
        GUILayout.Label($"Job board — {available.Count} available:");
        _jobsScroll = GUILayout.BeginScrollView(_jobsScroll, GUILayout.Height(200));
        foreach (ClientJob job in available)
        {
            GUILayout.BeginHorizontal();
            if (job.Def.GameId.Length > 0 && _mode == Mode.Hosting)
            {
                // On the host, captured jobs are claimed the native way (booklet → validator) —
                // the game IS the UX (D13). Remote players claim from the panel (M3.5c): the host
                // takes the job natively on their behalf when the claim commits.
                GUILayout.Label($"      {Describe(job.Def)}  [claim at the {job.Def.Origin} validator]");
            }
            else
            {
                if (GUILayout.Button("Claim", GUILayout.Width(60))) career.ClaimJob(job.Def.Id);
                GUILayout.Label(Describe(job.Def));
            }
            GUILayout.EndHorizontal();
        }
        foreach (ClientJob job in others)
        {
            string who = job.ClaimantName.Length > 0 ? job.ClaimantName : "?";
            // Captured jobs show their route here too: in a shared cab the OTHER crew often does
            // the physical haul for the claimant (the one-PC rig's A4 flow literally is that).
            string route = job.Def.GameId.Length > 0 && job.Def.Tasks.Count > 0 ? $" — {job.Def.Tasks[0].Param}" : "";
            GUILayout.Label($"     {Describe(job.Def)} — claimed by {who}{(job.ClaimantPeerId == 0 ? " (offline)" : "")}{route}");
        }
        GUILayout.EndScrollView();

        if (career.LicenseCatalog.Count > 0)
        {
            _showShop = GUILayout.Toggle(_showShop, $"License shop ({career.LicenseCatalog.Count})");
            if (_showShop)
            {
                foreach (var entry in career.LicenseCatalog.OrderBy(kv => kv.Key).ToList())
                {
                    if (career.Licenses.Contains(entry.Key)) continue;
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Buy", GUILayout.Width(50))) career.PurchaseLicense(entry.Key);
                    GUILayout.Label($"{entry.Key} — {Money(entry.Value)}");
                    GUILayout.EndHorizontal();
                }
            }
        }

        DrawHostGrants(career);

        if (_careerToast.Length > 0) GUILayout.Label("» " + _careerToast);
    }

    /// <summary>Host-admin license grants (M3.5c): a fresh guest on a mature world faces a board
    /// of license-gated jobs with a starting wallet that can't buy any of them — the host hands
    /// out what's needed, charge-free and explicit. Only sends proposals; the server commits and
    /// the grantee's own client confirms via its license state.</summary>
    private void DrawHostGrants(ClientCareer career)
    {
        if (_mode != Mode.Hosting || _client is null || _client.Players.Count == 0 || career.LicenseCatalog.Count == 0)
            return;

        _showGrant = GUILayout.Toggle(_showGrant, "Grant licenses to a player (host)");
        if (!_showGrant) return;

        GUILayout.BeginHorizontal();
        GUILayout.Label("To:", GUILayout.Width(30));
        foreach (var p in _client.Players.Values.OrderBy(p => p.Id).ToList())
        {
            if (GUILayout.Toggle(_grantTarget == p.Id, $"{p.Name} (id {p.Id})", GUI.skin.button, GUILayout.Width(140)))
                _grantTarget = p.Id;
        }
        GUILayout.EndHorizontal();

        if (_grantTarget == 0 || !_client.Players.ContainsKey(_grantTarget)) return;
        _grantScroll = GUILayout.BeginScrollView(_grantScroll, GUILayout.Height(150));
        foreach (var entry in career.LicenseCatalog.OrderBy(kv => kv.Key).ToList())
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Grant", GUILayout.Width(60)))
            {
                career.GrantExternalLicense(entry.Key, _grantTarget);
                string who = _client.Players[_grantTarget].Name;
                _careerToast = $"granted {entry.Key} to {who}";
                _log($"[career] host grant: {entry.Key} → {who} (peer {_grantTarget})");
            }
            GUILayout.Label(entry.Key);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private static string Describe(JobDef def)
    {
        string needs = def.RequiredLicenses.Count > 0 ? $" (needs {string.Join("+", def.RequiredLicenses.ToArray())})" : "";
        return $"[{def.Id}] {def.JobType} {def.Origin}→{def.Destination}  {def.CarCount}× {def.CargoKind}  {Money(def.PayoutCents)}{needs}";
    }

    private void Host()
    {
        try
        {
            _lastError = "";
            int port = ParsePort();

            // M3 career: real map data in, saved career back (host-mode resume restores the CAREER
            // half only — the host's live game world is the physical truth and re-registers its
            // consists fresh; restoring saved trainsets here would duplicate them as ghosts. The
            // full-world restore is the dedicated server's path, M6).
            ProgressionPreset preset = _sharedCareer ? ProgressionPreset.SharedCareer : ProgressionPreset.PerPlayer;
            CareerConfigBuilder.TryBuild(preset, out CareerConfig careerConfig, _log);
            var storage = new FileSaveStorage(CareerSavePath(preset));
            ServerSaveData? restore = null;
            if (_freshCareer)
            {
                _log("[career] fresh career requested — ignoring any saved one");
            }
            else
            {
                try
                {
                    byte[]? saved = storage.TryLoad();
                    if (saved != null)
                    {
                        restore = new ServerSaveData(SaveCodec.Read(saved).Career, new TrainsSaveData());
                        _log("[career] resumed saved career (wallets, licenses, board, claims)");
                    }
                }
                catch (Exception e)
                {
                    _log($"[career] saved career unreadable ({e.Message}) — starting fresh (backups sit beside it)");
                }
            }

            _hub = new LoopbackNetwork();
            var udp = LiteNetLibTransport.StartServer(port, NetDefaults.ConnectKey);
            _serverTransport = new CompositeTransport(_hub.Server, udp);
            _server = new NetServer(_serverTransport,
                new ServerConfig(Identity(), _password.Length > 0 ? _password : null, career: careerConfig),
                _clock, restore);
            _server.PlayerAdmitted += p => _log($"[session] admitted {p.Name} (id {p.Id}) — {_server!.PlayerCount} player(s)");
            _server.PlayerRemoved += id => _log($"[session] removed id {id} — {_server!.PlayerCount} player(s)");
            // Server-side refusals go to the requesting PEER; without these lines a remote
            // player's rejection (e.g. a bot's claim) is invisible in the host log.
            _server.Career.RequestRejected += (peer, reason) => _log($"[server] career refused (peer {peer}): {reason}");
            _server.Trains.ProposalRejected += (peer, reason) => _log($"[server] trains refused (peer {peer}): {reason}");
            _autosaver = new Autosaver(_clock, intervalMs: 120_000, storage,
                () => SaveCodec.Write(_server!.CaptureSave()));

            _client = MakeClient(_hub.Connect(out _)); // the host is just client #1, zero latency
            _trains = new TrainSync(_client, isHost: true, _log);
            _trains.WorldUnloaded += () => _worldUnloaded = true;
            _cabControls = new CabControlSync(_client, _trains, _log);
            // D13: the HOST keeps DV's native generation running — JobCapture mirrors every
            // generated job onto the server board. Only joining CLIENTS suppress.
            JobGenSuppressor.Active = false;
            _jobCapture = new JobCapture(_client, _log);
            _jobCapture.TakeRefused += reason => _careerToast = reason;
            // D14: the native career manager is the shop and native money is the wallet's view —
            // licenses sync both ways, register purchases burn through the ledger.
            _licenseSync = new LicenseSync(_client, _log);
            _walletMirror = new WalletMirror(_client, _log);
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
            _trains = new TrainSync(_client, isHost: false, _log);
            _trains.WorldUnloaded += () => _worldUnloaded = true;
            _cabControls = new CabControlSync(_client, _trains, _log);
            JobGenSuppressor.Active = true;            // clients never generate either (02 §4)
            JobGenSuppressor.StopAll(_log);
            // M3.5b: the joined world is session-modified (own cars cleared, host's spawned in) —
            // native saves are blocked until Leave so it can't leak into the player's SP save.
            SaveSuppressor.Active = true;
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
        _playerKey ??= PlayerKeyStore.GetOrCreate(_log);
        var client = new NetClient(transport, Identity(),
            _playerName.Length > 0 ? _playerName : "Player", _clock,
            _password.Length > 0 ? _password : null, _playerKey);
        client.Accepted += id => _log($"[session] joined as id {id} (server offset {client.ServerTimeOffsetMs} ms)");
        client.Rejected += reason => { _lastError = reason; _log($"[session] REJECTED: {reason}"); };
        client.PlayerJoined += p => { _avatars.AddOrUpdate(p.Id, p.Name, p.Pose); _log($"[session] player joined: {p.Name} (id {p.Id})"); };
        client.PlayerLeft += id => { _avatars.Remove(id); _log($"[session] player left: id {id}"); };
        client.PlayerMoved += (id, pose) => _avatars.Move(id, pose);

        client.Career.RequestRejected += (r, _) => { _careerToast = r; _log("[career] refused: " + r); };
        client.Career.EconomyEventReceived += (kind, cents, reason) =>
        {
            _careerToast = $"{kind}: {Money(cents)} — {reason}";
            _log($"[career] {kind}: {Money(cents)} — {reason}");
        };
        client.Career.JobChanged += job =>
        {
            if (job.State == JobLifecycle.Completed && job.ClaimantPeerId == client.LocalId)
                _log($"[career] job {job.Def.Id} DELIVERED — payout incoming");
        };
        return client;
    }

    private static string Money(long cents) => "$" + (cents / 100.0).ToString("N2", CultureInfo.InvariantCulture);

    private static string CareerSavePath(ProgressionPreset preset) =>
        // Per-preset files: wallet migration between presets is undefined, so they never collide.
        Path.Combine(Application.persistentDataPath, $"locomp-career-{preset}.lmps");

    /// <summary>Tear the whole session down (also called on mod toggle-off). Safe when idle.</summary>
    public void Leave()
    {
        if (_autosaver != null && _server != null)
        {
            try
            {
                _autosaver.SaveNow();
                _log("[career] career saved");
            }
            catch (Exception e)
            {
                _log("[career] final save FAILED: " + e.Message);
            }
        }
        _autosaver = null;
        _jobCapture?.Dispose();
        _jobCapture = null;
        _walletMirror?.Dispose();                      // restores the pre-session native money
        _walletMirror = null;
        _licenseSync?.Dispose();
        _licenseSync = null;
        JobGenSuppressor.Active = false;               // DV's own generation resumes outside sessions
        SaveSuppressor.Active = false;                 // native saving resumes outside sessions
        _careerToast = "";

        if (_client is { Joined: true }) { _client.Leave(); _client.Poll(); }
        _cabControls?.Dispose();
        _cabControls = null;
        _trains?.Dispose();
        _trains = null;
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
