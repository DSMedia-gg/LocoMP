using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Presence;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;

namespace LocoMP.Bot;

/// <summary>
/// The M3.5c "remote player" rig: exercises the flows a joined friend would drive, headless.
/// Career — claim the first available board job, then retry "Report delivery" on an interval
/// (refusals log the host world's verdict; once the HOST physically delivers the cars, the next
/// report pays THIS bot — the full remote-claim loop on one PC) and/or abandon after a delay
/// (external jobs die everywhere; the host log should show the native abandon). Cab — find a
/// loco the host simulates, request its control grant, and push the throttle over the wire:
/// the host should watch their own lever move. Rebinds cleanly after churn/reconnect.
/// </summary>
public sealed class RemoteActor
{
    private readonly BotOptions _opts;
    private readonly string _name;
    private readonly Action<string> _log;

    private NetClient? _bound;
    private int _pendingClaimJobId = -1;
    private int _claimedJobId = -1;
    private double _claimRetryAccum;
    private bool _noneClaimableLogged;
    private readonly HashSet<int> _refusedJobs = new();
    private readonly HashSet<int> _skipLogged = new();
    private double _sinceClaim;
    private double _sinceReport;
    private int _driveCarId = -1;
    private bool _driveStarted;
    private bool _driveDone;
    private bool _driveTargetLogged;
    private double _driveElapsed;
    private readonly Dictionary<(int car, byte control), float> _loggedControls = new();
    private int _heldItemId = -1;       // the world item we currently hold (-1 = none)
    private int _pendingPickupId = -1;  // a pickup request is in flight
    private double _holdElapsed;
    private double _grabScanAccum;
    private readonly HashSet<int> _refusedItems = new();
    private bool _buyRequested;         // --buy: the purchase has been sent
    private bool _commsSent;            // --rerail/--clear: the comms action has been sent
    private double _commsWaitAccum;

    public RemoteActor(BotOptions opts, string name, Action<string> log)
    {
        _opts = opts;
        _name = name;
        _log = log;
    }

    /// <summary>Advance by one tick. Wired to <see cref="BotClient.SessionTick"/>.</summary>
    public void Tick(NetClient client, double dt)
    {
        if (!ReferenceEquals(_bound, client)) Bind(client);
        TickCareer(client, dt);
        TickDrive(client, dt);
        TickItems(client, dt);
        TickComms(client, dt);
    }

    /// <summary>M4 comms radio: as the "remote player", ask the host to rerail/delete one of its cars
    /// once it's on the wire. The host performs the real action and charges OUR wallet — the one-PC
    /// proof of the remote-action loop (a real client's comms radio would send the same request).</summary>
    private void TickComms(NetClient client, double dt)
    {
        if (_commsSent || (_opts.RerailCar.Length == 0 && _opts.ClearCar.Length == 0)) return;
        if (!client.Joined) return;
        _commsWaitAccum += dt;
        if (_commsWaitAccum < 1.0) return; // let the world burst land first
        _commsWaitAccum = 0;

        string plate = _opts.RerailCar.Length > 0 ? _opts.RerailCar : _opts.ClearCar;
        int carId = FindCarByPlate(client, plate);
        if (carId < 0) return; // not on the wire yet — try again next second

        _commsSent = true;
        if (_opts.RerailCar.Length > 0)
        {
            var dest = new Pose(_opts.Center.Px, _opts.Center.Py, _opts.Center.Pz, 0f, 0f, 0f, 1f);
            _log($"[{_name}] asking the host to rerail car {carId} ({plate}) to {_opts.Center.Px:F0},{_opts.Center.Py:F0},{_opts.Center.Pz:F0} — watch your wallet");
            client.Trains.RequestCommsAction(CommsActionKind.Rerail, carId, dest);
        }
        else
        {
            _log($"[{_name}] asking the host to delete car {carId} ({plate}) — watch it vanish and your wallet drop");
            client.Trains.RequestCommsAction(CommsActionKind.Delete, carId, Pose.Identity);
        }
    }

    private static int FindCarByPlate(NetClient client, string plate)
    {
        foreach (TrainsetDef set in client.Trains.View.Sets.Values)
            foreach (CarDef car in set.Cars)
                if (string.Equals(car.GameId, plate, StringComparison.OrdinalIgnoreCase))
                    return car.Id;
        return -1;
    }

    private void Bind(NetClient client)
    {
        _bound = client;
        _pendingClaimJobId = -1;
        _claimedJobId = -1;
        _noneClaimableLogged = false;
        _refusedJobs.Clear();
        _skipLogged.Clear();
        _driveCarId = -1;
        _driveStarted = false;
        _driveDone = false;
        _driveTargetLogged = false;
        _loggedControls.Clear();
        _heldItemId = -1;
        _pendingPickupId = -1;
        _holdElapsed = 0;
        _grabScanAccum = 0;
        _refusedItems.Clear();
        _buyRequested = false;
        _commsSent = false;
        _commsWaitAccum = 0;

        client.Career.CareerStateReceived += () =>
            _log($"[{_name}] career: ${client.Career.BalanceCents / 100.0:F2}, licenses: {LicenseList(client)}");
        client.Career.JobAdded += _ => _noneClaimableLogged = false; // fresh board entry — re-scan
        client.Career.LicenseGranted += _ => _noneClaimableLogged = false;
        client.Career.JobChanged += job =>
        {
            if (job.ClaimantPeerId == client.LocalId && job.State == JobLifecycle.Claimed && _claimedJobId < 0)
            {
                _pendingClaimJobId = -1;
                _claimedJobId = job.Def.Id;
                _sinceClaim = 0;
                _sinceReport = 0;
                _log($"[{_name}] claimed job {job.Def.Id} ({job.Def.JobType} {job.Def.Origin}→{job.Def.Destination}, ${job.Def.PayoutCents / 100.0:F0})");
                if (job.Def.Tasks.Count > 0 && job.Def.Tasks[0].Param.Length > 0)
                    _log($"[{_name}]   route: {job.Def.Tasks[0].Param}");
            }
            else if (job.Def.Id == _claimedJobId &&
                     (job.State == JobLifecycle.Completed || job.State == JobLifecycle.Expired))
            {
                _log($"[{_name}] my job {job.Def.Id} → {job.State}");
                _claimedJobId = -1;
            }
        };
        client.Career.WalletChanged += balance => _log($"[{_name}] wallet: ${balance / 100.0:F2}");
        client.Career.EconomyEventReceived += (kind, cents, reason) =>
            _log($"[{_name}] economy: {kind} ${cents / 100.0:F2} — {reason}");
        client.Career.RequestRejected += (reason, jobId) =>
        {
            _log($"[{_name}] refused{(jobId != 0 ? $" (job {jobId})" : "")}: {reason}");
            // A refused claim attempt: blacklist that job and let the scan try the next one.
            if (jobId != 0 && jobId == _pendingClaimJobId)
            {
                _refusedJobs.Add(jobId);
                _pendingClaimJobId = -1;
            }
        };

        if (_opts.BuyPrefab.Length > 0)
        {
            client.Items.ItemAdded += item =>
            {
                // Our purchase committed server-side: the freshly minted item lands in OUR possession
                // (ItemSpawned, not the pickup's ItemMoved). The WalletState debit is sent first, so
                // the balance we log here is already the post-purchase one — and it's OUR wallet.
                if (_buyRequested && _heldItemId < 0 &&
                    item.OwnerPeerId == client.LocalId && item.Location == ItemLocationKind.Possessed)
                {
                    _heldItemId = item.Def.Id;
                    _holdElapsed = 0;
                    _log($"[{_name}] bought item {item.Def.Id} ({item.Def.PrefabName}) — wallet now " +
                         $"${client.Career.BalanceCents / 100.0:F2} (the host's wallet is untouched); " +
                         $"dropping in {_opts.DropAfterSeconds:F0}s");
                }
            };
        }

        if (_opts.GrabItems)
        {
            client.Items.ItemMoved += item =>
            {
                // Our pickup committed server-side: the item is now in our possession.
                if (item.OwnerPeerId == client.LocalId && item.Location == ItemLocationKind.Possessed &&
                    _heldItemId != item.Def.Id)
                {
                    _pendingPickupId = -1;
                    _heldItemId = item.Def.Id;
                    _holdElapsed = 0;
                    _log($"[{_name}] picked up item {item.Def.Id} ({item.Def.PrefabName}) — " +
                         $"it should vanish from the host's world; dropping in {_opts.DropAfterSeconds:F0}s");
                }
            };
            client.Items.ItemRemoved += id => { if (id == _heldItemId) _heldItemId = -1; };
            client.Items.RequestRejected += (reason, itemId) =>
            {
                _log($"[{_name}] item refused{(itemId != 0 ? $" (item {itemId})" : "")}: {reason}");
                if (itemId != 0 && itemId == _pendingPickupId) { _refusedItems.Add(itemId); _pendingPickupId = -1; }
            };
        }

        if (_opts.Drive)
        {
            client.Trains.GrantChanged += (carId, holder) =>
            {
                if (carId == _driveCarId)
                    _log($"[{_name}] grant on car {carId}: {(holder == 0 ? "released" : holder == client.LocalId ? "MINE" : $"player {holder}")}");
            };
            client.Trains.ControlStateReceived += (carId, controlId, value) =>
            {
                // The owner's committed control state coming back — log meaningful moves only.
                if (_loggedControls.TryGetValue((carId, controlId), out float last) && Math.Abs(last - value) < 0.05f) return;
                _loggedControls[(carId, controlId)] = value;
                _log($"[{_name}] control state: car {carId} {ControlName(controlId)} = {value:F2}");
            };
        }
    }

    private void TickCareer(NetClient client, double dt)
    {
        if (_claimedJobId >= 0)
        {
            _sinceClaim += dt;
            _sinceReport += dt;

            if (_opts.AbandonAfterSeconds > 0 && _sinceClaim >= _opts.AbandonAfterSeconds)
            {
                _log($"[{_name}] abandoning job {_claimedJobId} (--abandon-after) — external jobs die everywhere");
                client.Career.AbandonJob(_claimedJobId);
                _claimedJobId = -1;
                return;
            }
            if (_opts.ReportIntervalSeconds > 0 && _sinceReport >= _opts.ReportIntervalSeconds &&
                client.Career.Jobs.TryGetValue(_claimedJobId, out ClientJob? mine))
            {
                _sinceReport = 0;
                _log($"[{_name}] reporting delivery on job {_claimedJobId} (host world will verify)");
                client.Career.ReportTask(_claimedJobId, mine.NextTaskIndex);
            }
            return;
        }

        if (!_opts.ClaimFirst || _pendingClaimJobId >= 0) return; // an attempt is in flight
        _claimRetryAccum += dt;
        if (_claimRetryAccum < 2.0) return; // scan at most every 2 s — no claim spam
        _claimRetryAccum = 0;

        foreach (ClientJob job in client.Career.Jobs.Values.OrderBy(j => j.Def.Id))
        {
            if (job.State != JobLifecycle.Available || _refusedJobs.Contains(job.Def.Id)) continue;

            // Only claim what we are actually eligible for — a fresh profile holds just the
            // starting-license floor, and the server refuses anything beyond it.
            string? missing = job.Def.RequiredLicenses.FirstOrDefault(l => !client.Career.Licenses.Contains(l));
            if (missing != null)
            {
                if (_skipLogged.Add(job.Def.Id))
                    _log($"[{_name}] skipping job {job.Def.Id} ({job.Def.JobType} {job.Def.Origin}→{job.Def.Destination}) " +
                         $"— needs license '{missing}' (I hold: {LicenseList(client)})");
                continue;
            }

            _pendingClaimJobId = job.Def.Id;
            _log($"[{_name}] claiming job {job.Def.Id} from the board…");
            client.Career.ClaimJob(job.Def.Id);
            return;
        }

        if (!_noneClaimableLogged)
        {
            _noneClaimableLogged = true;
            _log($"[{_name}] no claimable job on the board (licenses held: {LicenseList(client)}) — " +
                 "waiting; new jobs and license grants re-trigger the scan");
        }
    }

    private static string LicenseList(NetClient client) =>
        client.Career.Licenses.Count == 0 ? "none" : string.Join(",", client.Career.Licenses.OrderBy(l => l));

    private void TickDrive(NetClient client, double dt)
    {
        if (!_opts.Drive || _driveDone) return;

        if (_driveCarId < 0)
        {
            // Explicit plate wins (--drive-car L-013); otherwise the first loco-looking car in a
            // consist someone ELSE simulates — which may not be the loco the host cares about.
            foreach (TrainsetDef set in client.Trains.View.Sets.Values.OrderBy(s => s.Id))
            {
                if (set.OwnerId == 0 || set.OwnerId == client.LocalId) continue;
                foreach (CarDef car in set.Cars)
                {
                    bool match = _opts.DriveCarId.Length > 0
                        ? string.Equals(car.GameId, _opts.DriveCarId, StringComparison.OrdinalIgnoreCase)
                        : car.Kind.IndexOf("loco", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                    _driveCarId = car.Id;
                    _log($"[{_name}] requesting control grant for car {car.Id} ({car.Kind}{(car.GameId.Length > 0 ? $", {car.GameId}" : "")})");
                    client.Trains.RequestControlGrant(car.Id);
                    return;
                }
            }
            if (_opts.DriveCarId.Length > 0 && !_driveTargetLogged)
            {
                _driveTargetLogged = true;
                _log($"[{_name}] car '{_opts.DriveCarId}' is not on the wire yet — waiting for it to register");
            }
            return;
        }

        bool granted = client.Trains.Grants.TryGetValue(_driveCarId, out int holder) && holder == client.LocalId;
        if (!granted) return;

        if (!_driveStarted)
        {
            _driveStarted = true;
            _driveElapsed = 0;
            _log($"[{_name}] driving car {_driveCarId}: throttle → {_opts.DriveValue:F2}, train brake → 0 " +
                 $"(for {_opts.DriveSeconds:F0}s — watch the host's levers)");
            client.Trains.SendControlInput(_driveCarId, ThrottleId, _opts.DriveValue);
            client.Trains.SendControlInput(_driveCarId, TrainBrakeId, 0f);
            return;
        }

        _driveElapsed += dt;
        if (_driveElapsed >= _opts.DriveSeconds)
        {
            _driveDone = true;
            _log($"[{_name}] drive test done: throttle → 0, releasing the grant");
            client.Trains.SendControlInput(_driveCarId, ThrottleId, 0f);
            client.Trains.ReleaseControlGrant(_driveCarId);
        }
    }

    /// <summary>M4.2 item rig: pick up world items as they appear (proposing a pickup the server
    /// validates), hold each for a beat, then drop it at the drop point — on the host you watch the
    /// item leave your world on pickup and re-materialize on drop. Exercises the full v6 pickup/drop
    /// loop the way a joined friend would, headless.</summary>
    private void TickItems(NetClient client, double dt)
    {
        if (!_opts.GrabItems && _opts.BuyPrefab.Length == 0) return;

        // --buy: the client-side win condition — buy the prefab once we're joined. The mint lands in
        // OUR possession (charged to OUR wallet); the ItemAdded handler above picks it up from there.
        if (_opts.BuyPrefab.Length > 0 && !_buyRequested && client.Joined)
        {
            _buyRequested = true;
            _log($"[{_name}] buying {_opts.BuyPrefab} from the shop…");
            client.Items.Purchase(_opts.BuyPrefab);
        }

        if (_heldItemId >= 0)
        {
            _holdElapsed += dt;
            if (_holdElapsed >= _opts.DropAfterSeconds)
            {
                // A couple of metres off the --at anchor so a re-spawn near the host is visible.
                var pose = new Pose(_opts.Center.Px + 2, _opts.Center.Py, _opts.Center.Pz + 2, 0f, 0f, 0f, 1f);
                _log($"[{_name}] dropping item {_heldItemId} at the drop point — watch it reappear in the host's world");
                client.Items.RequestDrop(_heldItemId, pose);
                _heldItemId = -1;
            }
            return;
        }

        if (!_opts.GrabItems) return;      // a buy-only bot doesn't scan the world for items to grab
        if (_pendingPickupId >= 0) return; // a pickup is in flight
        _grabScanAccum += dt;
        if (_grabScanAccum < 2.0) return;  // scan at most every 2 s — no request spam
        _grabScanAccum = 0;

        foreach (ClientItem item in client.Items.Items.Values.OrderBy(i => i.Def.Id))
        {
            if (item.Location != ItemLocationKind.World || _refusedItems.Contains(item.Def.Id)) continue;
            _pendingPickupId = item.Def.Id;
            _log($"[{_name}] picking up world item {item.Def.Id} ({item.Def.PrefabName})…");
            client.Items.RequestPickup(item.Def.Id);
            return;
        }
    }

    // Wire control ids mirror DV's InteriorControlsManager.ControlType (the Shim defines the
    // mapping; the bot only names the common ones for readable logs — it never sees game types).
    private const byte ThrottleId = 1;
    private const byte TrainBrakeId = 2;

    private static string ControlName(byte id) => id switch
    {
        1 => "throttle",
        2 => "train brake",
        3 => "reverser",
        4 => "independent brake",
        5 => "handbrake",
        6 => "sander",
        7 => "horn",
        _ => $"control {id}",
    };
}
