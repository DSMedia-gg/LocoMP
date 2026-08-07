using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.World;
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
    private double _lostCountdown; // > 0: the server link dropped; grace before declaring it dead
    private bool _sessionLost;     // declared dead — panel shows the leave-to-restore prompt
    private bool _hostEndedSession; // the drop was ANNOUNCED (M5.2 Save & Stop) — clean end, not a loss

    // M3 career state
    private string? _playerKey;
    private Autosaver? _autosaver;
    private JobCapture? _jobCapture;
    private LicenseSync? _licenseSync;
    private WalletMirror? _walletMirror;
    private ItemSync? _itemSync;
    private CommsRadioSync? _commsRadio;
    private ManualServiceSync? _manualService;
    private WorldTimeSync? _worldTime;
    private HandbrakeSync? _handbrakes;
    private CouplerHardwareSync? _couplerHardware;
    private PauseSync? _pauseSync;
    private string _careerToast = "";

    // IMGUI field state
    private string _playerName = Environment.UserName;
    private string _address = "127.0.0.1";
    private string _portText = NetDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string _password = "";
    private bool _sharedCareer;
    private bool _freshCareer;
    private bool _autoGrant;
    // M5.1 host options with no IMGUI field: mirrored here anyway so the panel's Host() adapter
    // re-hosts with the last uGUI-set values instead of silently resetting them (the no-drift rule).
    private int _maxPlayers = 32;
    private int _autosaveSeconds = 120;

    // Interest management (D10). OFF by default: a friend-scale session is well inside the bandwidth
    // budget, so this is for bigger sessions and slow uplinks. Gating railed trains is the real win
    // (~96% of steady-state traffic) and needs world geometry, which the host extracts live below.
    private bool _interest;
    private bool _showShop;
    private bool _showItemShop;
    private bool _showGrant;
    private int _grantTarget;
    private Vector2 _jobsScroll;
    private Vector2 _grantScroll;
    private Vector2 _itemShopScroll;

    public SessionController(Action<string> log) => _log = log;

    // ── M5.0 public seam ─────────────────────────────────────────────────────────────────────
    // The observable surface SessionViewModel (and the retained IMGUI panel) binds to. Events
    // fire from Update()/the Core callbacks, i.e. on the Unity main thread (UMM pumps OnUpdate),
    // so handlers may touch GameObjects directly.

    /// <summary>Current lifecycle phase; transitions raise <see cref="PhaseChanged"/>.</summary>
    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;

    /// <summary>Last user-facing error ("" when clear). Set → <see cref="ErrorRaised"/>.</summary>
    public string LastError => _lastError;

    /// <summary>Last career/items toast ("" when clear). Set → <see cref="CareerToast"/>.</summary>
    public string LastToast => _careerToast;

    /// <summary>The session's client half (host included — the host is client #1). UI reads
    /// Players/Career/Items from here; commands still go through this controller.</summary>
    public NetClient? Client => _client;

    /// <summary>Host-only surfaces (PlayerCount, Career.AutoGrantHostLicenses…); null unless hosting.</summary>
    public NetServer? Server => _server;

    public event Action<SessionPhase>? PhaseChanged;
    public event Action? PlayersChanged;
    public event Action<string>? ErrorRaised;
    public event Action<string>? CareerToast;

    // ── M5.1 join progress + structured refusal ──────────────────────────────────────────────

    /// <summary>Where the join burst is (<see cref="Core.Session.JoinStage.None"/> when no client
    /// exists). The loading interstitial's stage feed; its gate clears on <see cref="JoinSettled"/>.</summary>
    public JoinStage JoinStage => _client?.Stage ?? JoinStage.None;

    /// <summary>The join burst is fully delivered — the ONLY signal a join readiness gate may
    /// clear on (never a timer, never an inferred stage).</summary>
    public bool JoinSettled => _client?.JoinSettled ?? false;

    /// <summary>The last structured join refusal (kind + have/need); null until a reject arrives,
    /// cleared by the next Host/Join command. The mismatch screen branches on this.</summary>
    public RejectInfo? LastReject { get; private set; }

    /// <summary>Join-burst stage transitions, re-raised from the client on the main thread.</summary>
    public event Action<JoinStage>? JoinStageChanged;

    /// <summary>Our admission-queue place (D18): 1-based while the server holds us for a slot,
    /// 0 otherwise. The stage stays Connecting throughout — admission is an ordinary accept.</summary>
    public int QueuePosition => _client?.QueuePosition ?? 0;

    /// <summary>Queue state transitions, re-raised from the client: (position, total); (0, 0) =
    /// no longer queued. Feeds the interstitial's "waiting for a free slot" line.</summary>
    public event Action<int, int>? QueueChanged;

    /// <summary>A join was refused, with structure. Fires after <see cref="ErrorRaised"/> (which
    /// still carries the prose reason for the panel/status line).</summary>
    public event Action<RejectInfo>? JoinRejected;

    /// <summary>The one spot phase transitions are detected. Called from Update() (async
    /// transitions: admission, session-lost, world unload) and at the end of every command.</summary>
    private void SyncPhase()
    {
        SessionPhase phase = _mode switch
        {
            Mode.Idle => SessionPhase.Idle,
            _ when _sessionLost => SessionPhase.SessionLost,
            Mode.Hosting => SessionPhase.Hosting,
            _ => _client is { Joined: true } ? SessionPhase.Joined : SessionPhase.Connecting,
        };
        if (phase == Phase) return;
        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    private void SetError(string message)
    {
        _lastError = message;
        if (message.Length > 0) ErrorRaised?.Invoke(message);
    }

    private void Toast(string message)
    {
        _careerToast = message;
        if (message.Length > 0) CareerToast?.Invoke(message);
    }

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
        // Deliberately OUTSIDE every session branch and before the early returns: a real balance deferred
        // because a cash register still held the player's deposited money must be written once that cash comes
        // back, and that can happen long after the session ended. See WalletMirror.PumpPendingRestore.
        WalletMirror.PumpPendingRestore();

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
                _server.BroadcastRoster();   // M5.2: roles + per-player ping for everyone's player list
                _server.BroadcastWorldTime(); // v18: restate the shared sun (no-op until anchored)
            }
        }

        _trains?.Tick(dt);
        _cabControls?.Tick((float)dt);
        _worldTime?.Tick((float)dt);
        _handbrakes?.Tick((float)dt);
        _couplerHardware?.Tick((float)dt);
        _pauseSync?.Tick((float)dt);
        _walletMirror?.Tick(dt);
        _itemSync?.Tick(dt);
        _commsRadio?.Tick(dt);
        _autosaver?.Tick();
        if (_worldUnloaded)
        {
            // Flagged from inside the tick; tear down afterwards so we never dispose mid-callback.
            _worldUnloaded = false;
            _log("[session] game world unloaded — session closed (host again once the new world is up)");
            SetError("world unloaded — session closed");
            Leave();
            return;
        }
        if (_lostCountdown > 0 && _mode == Mode.Joined)
        {
            // A dropped link can self-heal (the transport re-handshakes after a load freeze —
            // observed as id 2 → id 3), so give it a moment before declaring the session dead.
            // Deliberately NO auto-Leave: Leave() re-enables native saving, and doing that
            // unattended in a session-mangled world is the exact leak SaveSuppressor blocks.
            if (_client is { Joined: true })
            {
                _lostCountdown = 0;
                _log("[session] connection recovered — session continues");
            }
            else if ((_lostCountdown -= dt) <= 0)
            {
                _sessionLost = true;
                SetError("session lost — the host is gone. Leave, then reload your save.");
                _log("[session] connection to the host lost — the session is over. Press Leave to " +
                     "restore your world, then reload your save (native saving stays blocked until you leave).");
            }
        }
        _avatars.Tick((float)dt);
        SyncPhase();
    }

    /// <summary>The game world is going away (quit to menu, load another save, exit). Teardown itself is
    /// deferred to <see cref="Tick"/> so we never dispose mid-callback.
    ///
    /// The money restore is attempted here as well as from Dispose, but note it is BEST-EFFORT on this path,
    /// not the fix: this event is raised from a poll that notices the world registry has already died, so
    /// <c>Inventory</c> is gone by now and the restore no-ops (harmlessly — DV writes no save during that
    /// teardown). It stays because the call is idempotent and a future DV build could tear the two down in
    /// the other order. The restore that actually matters runs from Dispose on the Leave path, where the
    /// world is still alive and a later autosave would otherwise persist the session wallet.</summary>
    private void OnWorldUnloaded()
    {
        _walletMirror?.RestoreNativeMoney();
        _worldUnloaded = true;
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
                _autoGrant = GUILayout.Toggle(_autoGrant, "Auto-grant my licenses to joining players");
                _interest = GUILayout.Toggle(_interest, "Only stream nearby trains/players (saves bandwidth)");

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
                if (_sessionLost)
                    GUILayout.Label(_hostEndedSession
                        ? "The host ended the session. Leave to restore your world, then reload your save."
                        : "⚠ SESSION LOST — the host is gone. Leave to restore your world, then reload your save.");
                else
                    GUILayout.Label($"{role} — {(_client is { Joined: true } ? "connected" : "connecting…")}" +
                                    (_mode == Mode.Hosting && _server != null ? $" — {_server.PlayerCount} player(s)" : ""));
                if (_client is { WorldPaused: true })
                    GUILayout.Label("⏸ HOST PAUSED — the world resumes when the host does (D19)");

                if (_client != null)
                {
                    foreach (var p in _client.Players.Values)
                    {
                        string badge = _client.RoleOf(p.Id) switch
                        {
                            PlayerRole.Owner => " [host]",
                            PlayerRole.Admin => " [admin]",
                            _ => "",
                        };
                        string ping = _client.PingOf(p.Id) is int ms ? $" — {ms} ms" : "";
                        GUILayout.Label($"  • {p.Name} (id {p.Id}){badge}{ping} @ {p.Pose}");
                    }
                    int worldItems = _client.Items.Items.Values.Count(i => i.Location == LocoMP.Core.Items.ItemLocationKind.World);
                    int heldItems = _client.Items.Items.Count - worldItems;
                    if (_client.Items.Items.Count > 0)
                        GUILayout.Label($"  Items — {worldItems} in the world, {heldItems} carried");
                }
                DrawCareer();
                DrawShop();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Leave", GUILayout.Width(100))) Leave();
                if (_mode == Mode.Hosting && GUILayout.Button("Save & stop session", GUILayout.Width(160)))
                    SaveAndStop();
                GUILayout.EndHorizontal();
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

    /// <summary>Host-admin license grants (M3.5c, hardened per D15): a fresh guest on a mature
    /// world faces a board of license-gated jobs with a starting wallet that can't buy any of
    /// them — the host hands out what's needed, charge-free and explicit. The list offers only
    /// licenses the host itself HOLDS (the server enforces the same gate — grants share
    /// progression, they never mint it), and the auto-grant toggle hands the whole set to every
    /// joining player. Only sends proposals; the server commits and the grantee's own client
    /// confirms via its license state.</summary>
    private void DrawHostGrants(ClientCareer career)
    {
        if (_mode != Mode.Hosting || _client is null) return;

        bool autoGrant = GUILayout.Toggle(_autoGrant, "Auto-grant my licenses to joining players");
        if (autoGrant != _autoGrant)
        {
            _autoGrant = autoGrant;
            if (_server != null) _server.Career.AutoGrantHostLicenses = autoGrant;
            _log($"[career] auto-grant {(autoGrant ? "ON — connected and joining players inherit your licenses" : "off")}");
        }

        if (_client.Players.Count == 0 || career.Licenses.Count == 0) return;
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
        foreach (string licenseId in career.Licenses.OrderBy(l => l).ToList())
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Grant", GUILayout.Width(60)))
            {
                career.GrantExternalLicense(licenseId, _grantTarget);
                string who = _client.Players[_grantTarget].Name;
                Toast($"granted {licenseId} to {who}");
                _log($"[career] host grant: {licenseId} → {who} (peer {_grantTarget})");
            }
            GUILayout.Label(licenseId);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    /// <summary>The M4 shop: what's for sale (the catalog read from the host's live world and fed
    /// down the join burst) with a Buy button each, plus the items I'm carrying with a "Drop here"
    /// button. Buying debits MY wallet and mints the item into my possession (02 §4 win condition);
    /// dropping places it in the world at my feet, where every player sees it and can pick it up
    /// (M4.2). Only ever SENDS proposals — the server commits and the state comes back (03 §3).</summary>
    private void DrawShop()
    {
        if (_client is not { Joined: true }) return;
        ClientItems items = _client.Items;
        int? myId = _client.LocalId;

        // Items I'm holding — offer to drop each into the world at my current position. This is what
        // lets a joined client complete the buy → drop → someone-picks-it-up loop entirely from the
        // panel (the headless bot does the same over the wire via --buy/--drop-after).
        var carried = items.Items.Values
            .Where(i => i.Location == ItemLocationKind.Possessed && i.OwnerPeerId == myId)
            .OrderBy(i => i.Def.Id).ToList();
        foreach (ClientItem it in carried)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Drop here", GUILayout.Width(90)) && PresenceShim.TryCaptureLocalPose(out var pose))
                items.RequestDrop(it.Def.Id, pose);
            GUILayout.Label($"carrying [{it.Def.Id}] {it.Def.PrefabName}");
            GUILayout.EndHorizontal();
        }

        if (items.ShopCatalog.Count == 0) return;
        _showItemShop = GUILayout.Toggle(_showItemShop, $"Shop ({items.ShopCatalog.Count})");
        if (!_showItemShop) return;
        _itemShopScroll = GUILayout.BeginScrollView(_itemShopScroll, GUILayout.Height(180));
        foreach (var entry in items.ShopCatalog.OrderBy(kv => kv.Key).ToList())
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Buy", GUILayout.Width(50))) items.Purchase(entry.Key);
            GUILayout.Label($"{entry.Key} — {Money(entry.Value)}");
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private static string Describe(JobDef def)
    {
        string needs = def.RequiredLicenses.Count > 0 ? $" (needs {string.Join("+", def.RequiredLicenses.ToArray())})" : "";
        return $"[{def.Id}] {def.JobType} {def.Origin}→{def.Destination}  {def.CarCount}× {def.CargoKind}  {Money(def.PayoutCents)}{needs}";
    }

    /// <summary>The progression preset the panel currently has selected — the career exporter
    /// stamps it into the <c>.lmpc</c> (the file is authoritative on the server, preset included).</summary>
    public ProgressionPreset HostPreset =>
        _sharedCareer ? ProgressionPreset.SharedCareer : ProgressionPreset.PerPlayer;

    /// <summary>IMGUI adapter: builds a <see cref="HostOptions"/> from the panel's field state and
    /// calls the shared entry point — the dev panel and the uGUI host screen ride one code path.</summary>
    private void Host() => HostSession(new HostOptions
    {
        PlayerName = _playerName,
        Port = ParsePort(),
        Password = _password.Length > 0 ? _password : null,
        Preset = _sharedCareer ? ProgressionPreset.SharedCareer : ProgressionPreset.PerPlayer,
        FreshCareer = _freshCareer,
        AutoGrantLicenses = _autoGrant,
        InterestFiltering = _interest,
        MaxPlayers = _maxPlayers,
        AutosaveIntervalSeconds = _autosaveSeconds,
    });

    /// <summary>Host a session (M5.0 seam entry point). Mirrors the options back into the IMGUI
    /// field state so the two UIs can never drift about what the live session was started with.</summary>
    public void HostSession(HostOptions o)
    {
        try
        {
            _lastError = "";
            LastReject = null;
            _hostEndedSession = false;
            int port = o.Port is > 0 and < 65536 ? o.Port : NetDefaults.Port;
            _playerName = o.PlayerName;
            _portText = port.ToString(CultureInfo.InvariantCulture);
            _password = o.Password ?? "";
            _sharedCareer = o.Preset == ProgressionPreset.SharedCareer;
            _freshCareer = o.FreshCareer;
            _autoGrant = o.AutoGrantLicenses;
            _interest = o.InterestFiltering;
            _maxPlayers = o.MaxPlayers is >= 1 and <= 256 ? o.MaxPlayers : 32;
            // 15 s floor: an accidental tiny interval must not hammer the disk; ≤0 means "default".
            _autosaveSeconds = o.AutosaveIntervalSeconds <= 0 ? 120 : Math.Max(15, o.AutosaveIntervalSeconds);

            // M3 career: real map data in, saved career back (host-mode resume restores the CAREER
            // half only — the host's live game world is the physical truth and re-registers its
            // consists fresh; restoring saved trainsets here would duplicate them as ghosts. The
            // full-world restore is the dedicated server's path, M6).
            ProgressionPreset preset = o.Preset;
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
            // Host-native items (D13 posture): the host's real world items ARE the world source, so
            // the server must accept its registrations. No proximity gate for now (0 = off). The shop
            // catalog is read from the live world (M4 shops): a client's purchase debits its OWN
            // wallet and mints the item — an unlisted prefab is refused.
            var itemConfig = new ItemConfig
            {
                AcceptExternalItems = true,
                ShopPrices = ShopCatalogBuilder.Build(_log),
            };
            // D10 Burst 2: interest management needs the rail network's world geometry to place a
            // railed train, and the host has the live world right here — so build the topology in
            // memory rather than making the operator extract an .lmpw first. Only when the toggle is
            // on: walking every track is real work, and a session that won't filter has no use for it.
            // A failure here is never fatal — the server just runs unfiltered, as it always has.
            WorldTopology? topology = null;
            if (_interest)
            {
                try
                {
                    topology = TopologyExtractor.Build(_log);
                }
                catch (Exception e)
                {
                    _log($"[session] could not read the track network ({e.Message}) — hosting without interest filtering");
                }
            }
            var interestConfig = new InterestConfig
            {
                Enabled = _interest,
                FilterPlayers = _interest,
            };

            _server = new NetServer(_serverTransport,
                new ServerConfig(Identity(), _password.Length > 0 ? _password : null, maxPlayers: _maxPlayers,
                                 career: careerConfig, items: itemConfig, interest: interestConfig),
                _clock, restore, topology);
            if (_interest)
            {
                string trains = topology is { HasGeometry: true } t
                    ? t.GeometryEdgeCount == t.Edges.Count
                        ? "filtered by distance"
                        : $"filtered on {t.GeometryEdgeCount}/{t.Edges.Count} placeable edge(s) (bare edges broadcast)"
                    : "broadcast (no world geometry)";
                _log($"[session] interest management ON — trains {trains}, players filtered");
            }
            _server.PlayerAdmitted += p => _log($"[session] admitted {p.Name} (id {p.Id}) — {_server!.PlayerCount} player(s)");
            _server.PlayerRemoved += id => _log($"[session] removed id {id} — {_server!.PlayerCount} player(s)");
            // Server-side refusals go to the requesting PEER; without these lines a remote
            // player's rejection (e.g. a bot's claim) is invisible in the host log.
            _server.Career.RequestRejected += (peer, reason) => _log($"[server] career refused (peer {peer}): {reason}");
            _server.Trains.ProposalRejected += (peer, reason) => _log($"[server] trains refused (peer {peer}): {reason}");
            // D15: joining players inherit the host's licenses (and live acquisitions) while on.
            _server.Career.AutoGrantHostLicenses = _autoGrant;
            _autosaver = new Autosaver(_clock, intervalMs: _autosaveSeconds * 1000, storage,
                () => SaveCodec.Write(_server!.CaptureSave()));
            _autosaver.SaveFailed += e =>
                _log($"[career] save FAILED — {e.Message} (changes since the last good save are not on disk)");
            // M5.2 save-now: a remote admin's SaveNow verb lands here — the server has already
            // authorised it, the host process owns the file. Failure logs through SaveFailed above.
            _server.SaveRequested += () =>
            {
                if (_autosaver != null && _autosaver.SaveNow()) _log("[career] career saved (admin save-now)");
            };

            _client = MakeClient(_hub.Connect(out _)); // the host is just client #1, zero latency
            _trains = new TrainSync(_client, isHost: true, _log);
            _trains.WorldUnloaded += OnWorldUnloaded;
            // Keep remote players'/bots' replica cars OUT of the host's SP save — otherwise an autosave
            // mid-session bakes the foreign consists in and they persist across re-hosts (2026-08-06).
            CarSaveFilter.IsReplica = _trains.Remote.IsRemoteCar;
            _cabControls = new CabControlSync(_client, _trains, _log);
            // D13: the HOST keeps DV's native generation running — JobCapture mirrors every
            // generated job onto the server board. Only joining CLIENTS suppress.
            JobGenSuppressor.Active = false;
            _jobCapture = new JobCapture(_client, _log);
            _jobCapture.TakeRefused += Toast;
            // D14: the native career manager is the shop and native money is the wallet's view —
            // licenses sync both ways, register purchases burn through the ledger.
            _licenseSync = new LicenseSync(_client, _log);
            _walletMirror = new WalletMirror(_client, isHost: true, _log);
            // M4.2: mirror the host's real world items onto the session; materialize remote-dropped
            // items back as real DV items. Host is the world source (registers native world items).
            _itemSync = new ItemSync(_client, isHost: true, _log);
            // M4 comms radio: capture rerail/delete/summon fees through the wallet, remove deleted
            // cars everywhere, and execute the comms actions remote players route to the host.
            _commsRadio = new CommsRadioSync(_client, _trains, isHost: true, _log);
            // M4 manual service: bill the buy-button-bypassing RefillAll/RepairAll shortcuts so a bay
            // can never hand out a free full service in-session (host-only — the metered valve+Buy path
            // already rides D14's WalletMirror, so it needs nothing here).
            _manualService = new ManualServiceSync(_client, isHost: true, _log);
            // v18 (02 §3): the host's sky is the session's time truth — heartbeat + jump reports.
            _worldTime = new WorldTimeSync(_client, isHost: true, _log);
            // v18 (02 §1): per-car handbrakes over the control-state machinery (id 200).
            _handbrakes = new HandbrakeSync(_client, _trains, _log);
            // v18 (02 §1): hoses/anglecocks/MU as server-validated discrete state; the reconcile
            // tick also SEEDS the host's pre-connected consists into the session.
            _couplerHardware = new CouplerHardwareSync(_client, _trains, _log);
            // D19: the host's native ESC pause becomes a session state every peer freezes with.
            _pauseSync = new PauseSync(_client, (paused, reason) => _server?.SetWorldPaused(paused, reason), _log);
            _mode = Mode.Hosting;

            _log($"[session] hosting on UDP {port} (game reports version '{PresenceShim.ReportedGameVersion}', handshake build '{PresenceShim.GameBuild}')");
            if (PresenceShim.TryCaptureLocalPose(out var here))
                _log($"[session] your absolute position: --at {here.Px:F0},{here.Py:F0},{here.Pz:F0}  ← paste into LocoMP.Bot");
        }
        catch (Exception e)
        {
            SetError($"host failed: {e.Message}");
            _log("[session] " + _lastError);
            Leave();
        }
        SyncPhase();
    }

    /// <summary>IMGUI adapter for <see cref="JoinSession"/> — same single-code-path deal as Host().</summary>
    private void Join() => JoinSession(new JoinOptions
    {
        PlayerName = _playerName,
        Address = _address,
        Port = ParsePort(),
        Password = _password.Length > 0 ? _password : null,
    });

    /// <summary>Join a session (M5.0 seam entry point). Options mirror back into the IMGUI fields,
    /// same as HostSession.</summary>
    public void JoinSession(JoinOptions o)
    {
        try
        {
            _lastError = "";
            LastReject = null;
            _hostEndedSession = false;
            int port = o.Port is > 0 and < 65536 ? o.Port : NetDefaults.Port;
            _playerName = o.PlayerName;
            _address = o.Address;
            _portText = port.ToString(CultureInfo.InvariantCulture);
            _password = o.Password ?? "";

            _clientTransport = LiteNetLibTransport.ConnectClient(o.Address, port, NetDefaults.ConnectKey);
            _client = MakeClient(_clientTransport);
            _trains = new TrainSync(_client, isHost: false, _log);
            _trains.WorldUnloaded += OnWorldUnloaded;
            _cabControls = new CabControlSync(_client, _trains, _log);
            // Symmetric with the host arm: on a client SaveSuppressor blocks the save before this runs,
            // but wiring it keeps the guard correct if that ever changes (every host car is a replica here).
            CarSaveFilter.IsReplica = _trains.Remote.IsRemoteCar;
            JobGenSuppressor.Active = true;            // clients never generate either (02 §4)
            JobGenSuppressor.StopAll(_log);
            // M3.5b: the joined world is session-modified (own cars cleared, host's spawned in) —
            // native saves are blocked until Leave so it can't leak into the player's SP save.
            SaveSuppressor.Active = true;
            // M4.2: spawn replicas of the host's world items (a joined client is not the world
            // source, so it only materializes — never registers).
            _itemSync = new ItemSync(_client, isHost: false, _log);
            // M4: mirror the LocoMP wallet onto native money so the client's money display and its
            // comms-radio affordability are correct (it never reports its own register purchases).
            _walletMirror = new WalletMirror(_client, isHost: false, _log);
            // M4 comms radio: a joined player's rerail/delete on a host-owned car is intercepted and
            // routed to the host (remote summon is banked).
            _commsRadio = new CommsRadioSync(_client, _trains, isHost: false, _log);
            // Constructed for a symmetric lifecycle; on a client the guard stays disarmed (the only
            // serviceable cars in a session are the host's, and a self-scope fee bills the host).
            _manualService = new ManualServiceSync(_client, isHost: false, _log);
            // v18 (02 §3): follow the session's sun — correct the local sky only past the drift
            // threshold, so steady state never visibly snaps.
            _worldTime = new WorldTimeSync(_client, isHost: false, _log);
            // v18 (02 §1): per-car handbrakes over the control-state machinery (id 200).
            _handbrakes = new HandbrakeSync(_client, _trains, _log);
            // v18 (02 §1): hoses/anglecocks/MU discrete state — replicas rig up from the mirror.
            _couplerHardware = new CouplerHardwareSync(_client, _trains, _log);
            // D19: freeze/unfreeze with the host's native pause (via DV's own pause-request system).
            _pauseSync = new PauseSync(_client, setServerPaused: null, _log);
            _mode = Mode.Joined;
            _log($"[session] joining {_address}:{_portText}…");
        }
        catch (Exception e)
        {
            SetError($"join failed: {e.Message}");
            _log("[session] " + _lastError);
            Leave();
        }
        SyncPhase();
    }

    private NetClient MakeClient(ITransport transport)
    {
        _playerKey ??= PlayerKeyStore.GetOrCreate(_log);
        var client = new NetClient(transport, Identity(),
            _playerName.Length > 0 ? _playerName : "Player", _clock,
            _password.Length > 0 ? _password : null, _playerKey);
        client.Accepted += id => _log($"[session] joined as id {id} (server offset {client.ServerTimeOffsetMs} ms)");
        client.Rejected += reason =>
        {
            LastReject = client.RejectDetail;
            SetError(reason);
            _log($"[session] REJECTED: {reason}");
            if (client.RejectDetail is { } detail) JoinRejected?.Invoke(detail);
        };
        client.JoinStageChanged += stage => JoinStageChanged?.Invoke(stage);
        client.QueueChanged += (position, total) =>
        {
            _log(position > 0
                ? $"[session] server full — queued for a slot at position {position} of {total}"
                : "[session] left the admission queue");
            QueueChanged?.Invoke(position, total);
        };
        // Only meaningful for JOINED sessions: the host's own loopback link can't drop. The
        // countdown (not an immediate declare) lets a transport re-handshake absorb load freezes.
        client.Disconnected += () =>
        {
            if (_mode != Mode.Joined || _sessionLost) return;
            if (_hostEndedSession)
            {
                // The end was ANNOUNCED (Save & Stop), so the drop is expected — no self-heal grace,
                // declare it now with the clean wording. Restore still requires Leave (native saving
                // stays blocked until then), exactly like an unannounced loss.
                _sessionLost = true;
                SetError("session ended by the host — leave to restore your world.");
                _log("[session] the host ended the session — press Leave to restore your world, then reload your save.");
            }
            else if (_lostCountdown <= 0) _lostCountdown = 3.0;
        };
        // M5.2: the roster status (roles + ping) repaints the same list PlayersChanged drives.
        client.RosterChanged += () => PlayersChanged?.Invoke();
        client.AdminNotice += (kind, arg) =>
        {
            if (kind != AdminNoticeKind.SessionEnded) return;
            _hostEndedSession = true;
            _log("[session] the host ended the session" + (arg.Length > 0 ? $" — {arg}" : ""));
        };
        client.PlayerJoined += p =>
        {
            _avatars.AddOrUpdate(p.Id, p.Name, p.Pose);
            _log($"[session] player joined: {p.Name} (id {p.Id})");
            PlayersChanged?.Invoke();
        };
        client.PlayerLeft += id =>
        {
            _avatars.Remove(id);
            _log($"[session] player left: id {id}");
            PlayersChanged?.Invoke();
        };
        client.PlayerMoved += (id, pose) => _avatars.Move(id, pose);
        // D10 interest management: the server hid a player who left our spatial relevance set. Keep the
        // avatar object (a later Move re-shows it) — unlike PlayerLeft, they are still in the session.
        client.PlayerHidden += id => _avatars.Hide(id);

        client.Career.RequestRejected += (r, _) => { Toast(r); _log("[career] refused: " + r); };
        // Item proposal refusals (a doomed purchase/pickup/drop) surface as the same panel toast;
        // ItemSync already writes the log line, so this only feeds the UI.
        client.Items.RequestRejected += (r, _) => Toast(r);
        client.Career.EconomyEventReceived += (kind, cents, reason) =>
        {
            Toast($"{kind}: {Money(cents)} — {reason}");
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

    /// <summary>M5.2 Save &amp; Stop (host only): announce the clean end to every joined player —
    /// their screen says "session ended by the host" instead of inferring a dead link — give the
    /// notice a moment to flush, then run the ordinary <see cref="Leave"/> (final career save +
    /// native-world restore). Falls through to a plain Leave when not hosting, so the uGUI overlay
    /// can bind it unconditionally.</summary>
    public void SaveAndStop()
    {
        if (_mode == Mode.Hosting && _server != null)
        {
            _server.AnnounceSessionEnd("the host ended the session");
            _server.Poll();                        // the loopback leg delivers immediately
            _client?.Poll();
            System.Threading.Thread.Sleep(150);    // let LiteNetLib flush the notice before the links die
            _log("[session] save & stop — session end announced to all players");
        }
        Leave();
    }

    /// <summary>Tear the whole session down (also called on mod toggle-off). Safe when idle.</summary>
    public void Leave()
    {
        if (_autosaver != null && _server != null)
        {
            // SaveNow no longer throws (Autosaver guards storage) — gate the success line on the
            // RESULT, because "[career] career saved" is a log line the runbooks grep for.
            if (_autosaver.SaveNow())
                _log("[career] career saved");
            else
                _log("[career] final save FAILED: " + (_autosaver.LastSaveError?.Message ?? "unknown"));
        }
        _autosaver = null;
        _jobCapture?.Dispose();
        _jobCapture = null;
        _walletMirror?.Dispose();                      // restores the pre-session native money
        _walletMirror = null;
        _licenseSync?.Dispose();
        _licenseSync = null;
        _itemSync?.Dispose();                          // removes replicas we spawned; leaves host natives
        _itemSync = null;
        _commsRadio?.Dispose();                        // clears the comms-radio hook filters
        _commsRadio = null;
        _manualService?.Dispose();                     // clears the manual-service hook filters
        _manualService = null;
        _worldTime?.Dispose();                         // unhooks the TimeJump capture
        _worldTime = null;
        _handbrakes?.Dispose();                        // unhooks every BrakeSystem watch
        _handbrakes = null;
        _couplerHardware?.Dispose();                   // unhooks the static hose/MU seams + cock hooks
        _couplerHardware = null;
        _pauseSync?.Dispose();                         // releases a held pause request (never leave a world frozen)
        _pauseSync = null;
        JobGenSuppressor.Active = false;               // DV's own generation resumes outside sessions
        SaveSuppressor.Active = false;                 // native saving resumes outside sessions
        CarSaveFilter.IsReplica = null;                // SP saves are unfiltered again (cleared before _trains dies)
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
        _sessionLost = false;
        _hostEndedSession = false;
        _lostCountdown = 0;
        _mode = Mode.Idle;
        SyncPhase();
    }

    private int ParsePort() =>
        int.TryParse(_portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536
            ? p
            : NetDefaults.Port;
}
