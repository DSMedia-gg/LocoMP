using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M3.5c end-to-end flows over the Loopback hub: identity/cargo surviving registration (the v4
/// wire bug), owner-authoritative control + cargo state with join-burst replay, couple/uncouple
/// requests routed to the sim owner, and remote claim parity on captured jobs — the deferred
/// completion query the world source answers from its native task tree, plus the "released
/// external jobs die everywhere" rule (DV cannot re-shelve a taken job).
/// </summary>
public class RemoteParityTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    /// <summary>External-jobs-only career: no stations/types, so the deterministic generator is
    /// off and every job on the board came from the world source (the host-capture shape).</summary>
    private static CareerConfig ExternalCareer() => new()
    {
        StartingBalanceCents = 500_00,
        ReconnectGraceMs = 10_000,
        AcceptExternalJobs = true,
    };

    private static JobDef ExternalJob(string gameId = "SM-FH-01", string[]? licenses = null) =>
        new(0, "FH", "SM", "GF", "cars", 2, 250_00, licenses ?? System.Array.Empty<string>(),
            new[] { new JobTaskDef(JobTaskKind.Haul, "GF") }, gameId);

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    /// <summary>Host A (world source, first admitted) + client B, external-jobs career.</summary>
    private static (LoopbackNetwork hub, ManualClock clock, NetServer server, NetClient a, NetClient b) Session()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity, career: ExternalCareer()), clock);
        var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        Pump(server, new[] { a }); // A admitted FIRST — it is the world source
        var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "key-bob");
        Pump(server, new[] { a, b });
        return (hub, clock, server, a, b);
    }

    // ── registration identity (the v4 bug) ──

    [Fact]
    public void Registration_preserves_identity_and_cargo_end_to_end()
    {
        var (_, _, server, a, b) = Session();

        a.Trains.RegisterTrainset(1, new[]
        {
            new CarDef(99, "LocoS282A", gameId: "L-014", gameGuid: "guid-loco"),
            new CarDef(98, "GondolaRed", gameId: "G-123", gameGuid: "guid-gondola", cargoId: "Coal", cargoAmount: 40f),
        });
        Pump(server, new[] { a, b });

        TrainsetDef def = server.Trains.Registry.Sets.Values.Single();
        Assert.NotEqual(99, def.Cars[0].Id);              // ids are still server-assigned
        Assert.Equal("L-014", def.Cars[0].GameId);
        Assert.Equal("guid-gondola", def.Cars[1].GameGuid);
        Assert.Equal("Coal", def.Cars[1].CargoId);
        Assert.Equal(40f, def.Cars[1].CargoAmount);

        TrainsetDef mirrored = b.Trains.View.Sets.Values.Single();
        Assert.Equal("G-123", mirrored.Cars[1].GameId);   // and the mirror carries them too
        Assert.Equal("Coal", mirrored.Cars[1].CargoId);
    }

    // ── control state (owner-authoritative, join-burst replayed) ──

    [Fact]
    public void Control_state_relays_from_the_owner_and_replays_to_late_joiners()
    {
        var (hub, clock, server, a, b) = Session();
        a.Trains.RegisterTrainset(1, new[] { new CarDef(0, "loco") });
        Pump(server, new[] { a, b });
        int carId = server.Trains.Registry.Sets.Values.Single().Cars[0].Id;

        var seen = new List<(int car, byte ctrl, float value)>();
        b.Trains.ControlStateReceived += (car, ctrl, value) => seen.Add((car, ctrl, value));

        a.Trains.SendControlState(carId, controlId: 1, value: 0.75f); // throttle
        b.Trains.SendControlState(carId, controlId: 1, value: 0.10f); // NOT the owner — dropped
        Pump(server, new[] { a, b });

        Assert.Equal((carId, (byte)1, 0.75f), seen.Single());

        var c = new NetClient(hub.Connect(out _), Identity, "Cara", clock, playerKey: "key-cara");
        var replayed = new List<float>();
        c.Trains.ControlStateReceived += (_, _, value) => replayed.Add(value);
        Pump(server, new[] { a, b, c });

        Assert.Equal(0.75f, replayed.Single()); // the join burst carries the committed value
    }

    [Fact]
    public void Grant_holder_input_routes_to_the_owner_whose_state_echo_reaches_the_holder()
    {
        var (_, _, server, a, b) = Session();
        a.Trains.RegisterTrainset(1, new[] { new CarDef(0, "loco") });
        Pump(server, new[] { a, b });
        int carId = server.Trains.Registry.Sets.Values.Single().Cars[0].Id;

        b.Trains.RequestControlGrant(carId);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, a.Trains.Grants[carId]);

        (int car, byte ctrl, float value)? input = null;
        a.Trains.ControlInputReceived += (car, ctrl, value) => input = (car, ctrl, value);
        b.Trains.SendControlInput(carId, controlId: 1, value: 0.6f);
        Pump(server, new[] { a, b });
        Assert.Equal((carId, (byte)1, 0.6f), input);      // routed to the sim owner

        // The owner applies it and reports the committed state — the holder's replica mirrors it.
        float? echoed = null;
        b.Trains.ControlStateReceived += (_, _, value) => echoed = value;
        a.Trains.SendControlState(carId, 1, 0.6f);
        Pump(server, new[] { a, b });
        Assert.Equal(0.6f, echoed);
    }

    // ── cargo state ──

    [Fact]
    public void Cargo_update_folds_into_the_def_and_reaches_everyone_including_late_joiners()
    {
        var (hub, clock, server, a, b) = Session();
        a.Trains.RegisterTrainset(1, new[] { new CarDef(0, "gondola", cargoId: "Coal", cargoAmount: 40f) });
        Pump(server, new[] { a, b });
        int carId = server.Trains.Registry.Sets.Values.Single().Cars[0].Id;

        (string cargo, float amount)? seen = null;
        b.Trains.CargoChanged += (_, cargo, amount) => seen = (cargo, amount);

        a.Trains.SendCargoState(carId, "", 0f);           // unloaded at the warehouse
        b.Trains.SendCargoState(carId, "Oil", 99f);       // NOT the owner — dropped
        Pump(server, new[] { a, b });

        Assert.Equal(("", 0f), seen);
        Assert.Equal("", server.Trains.Registry.Sets.Values.Single().Cars[0].CargoId);
        Assert.Equal(1u, server.Trains.Registry.Sets.Values.Single().Epoch); // cargo is not membership

        var c = new NetClient(hub.Connect(out _), Identity, "Cara", clock, playerKey: "key-cara");
        Pump(server, new[] { a, b, c });
        Assert.Equal("", c.Trains.View.Sets.Values.Single().Cars[0].CargoId); // def carries the live load
    }

    // ── couple/uncouple requests ──

    [Fact]
    public void Couple_request_routes_to_the_owner_whose_native_proposal_then_commits()
    {
        var (_, clock, server, a, b) = Session();
        a.Trains.RegisterTrainset(1, new[] { new CarDef(0, "loco") });
        a.Trains.RegisterTrainset(2, new[] { new CarDef(0, "boxcar") });
        Pump(server, new[] { a, b });
        clock.Advance(3000);
        TrainsetDef[] sets = server.Trains.Registry.Sets.Values.OrderBy(s => s.Id).ToArray();
        int carA = sets[0].Cars[0].Id, carB = sets[1].Cars[0].Id;

        // The owner's Shim would perform the physical couple; here the test IS the Shim: translate
        // the routed request into the normal owner proposal.
        (int a, CoupleEnd ea, int b, CoupleEnd eb)? routed = null;
        a.Trains.CoupleRequested += (ca, ea, cb, eb) =>
        {
            routed = (ca, ea, cb, eb);
            a.Trains.ProposeCouple(ca, CoupleEnd.Rear, cb, CoupleEnd.Front, relV: 0.5f);
        };

        b.Trains.RequestCouple(carA, CoupleEnd.Rear, carB, CoupleEnd.Front);
        Pump(server, new[] { a, b });

        Assert.Equal((carA, CoupleEnd.Rear, carB, CoupleEnd.Front), routed);
        TrainsetDef merged = server.Trains.Registry.Sets.Values.Single();
        Assert.Equal(2, merged.Cars.Count);
        Assert.Equal(merged.Id, b.Trains.View.Sets.Values.Single().Id); // commit mirrored back
    }

    [Fact]
    public void Uncouple_request_routes_to_the_owner()
    {
        var (_, clock, server, a, b) = Session();
        a.Trains.RegisterTrainset(1, new[] { new CarDef(0, "loco"), new CarDef(0, "boxcar") });
        Pump(server, new[] { a, b });
        clock.Advance(3000);
        TrainsetDef set = server.Trains.Registry.Sets.Values.Single();

        (int car, CoupleEnd end)? routed = null;
        a.Trains.UncoupleRequested += (car, end) => routed = (car, end);

        b.Trains.RequestUncouple(set.Cars[0].Id, CoupleEnd.Rear);
        Pump(server, new[] { a, b });

        Assert.Equal((set.Cars[0].Id, CoupleEnd.Rear), routed);
    }

    // ── remote claim parity on captured jobs ──

    [Fact]
    public void Remote_claim_defers_completion_to_native_validation_and_pays_on_ok()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        int? queried = null;
        a.Career.CompleteQueryReceived += id => queried = id;

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        Assert.Equal(b.LocalId, a.Career.Jobs[jobId].ClaimantPeerId);

        b.Career.ReportTask(jobId, 0);
        Pump(server, new[] { a, b });
        Assert.Equal(jobId, queried);                     // the server asked the world source
        Assert.Equal(500_00, b.Career.BalanceCents);      // and did NOT pay yet

        a.Career.SendCompleteReply(jobId, ok: true, reason: "");
        Pump(server, new[] { a, b });

        Assert.Equal(500_00 + 250_00, b.Career.BalanceCents); // payout to the remote claimant
        Assert.Equal(500_00, a.Career.BalanceCents);
        Assert.False(b.Career.Jobs.ContainsKey(jobId));       // completed jobs leave the board
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void Native_refusal_bounces_the_report_with_the_verdict_and_keeps_the_claim()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        a.Career.CompleteQueryReceived += id => a.Career.SendCompleteReply(id, false, "cars are not at the destination");

        string? refusal = null;
        b.Career.RequestRejected += (reason, _) => refusal = reason;

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        b.Career.ReportTask(jobId, 0);
        Pump(server, new[] { a, b });

        Assert.Contains("cars are not at the destination", refusal);
        Assert.Equal(JobLifecycle.Claimed, b.Career.Jobs[jobId].State); // claim intact — haul on
        Assert.Equal(500_00, b.Career.BalanceCents);
    }

    [Fact]
    public void Complete_query_times_out_when_the_world_source_never_answers()
    {
        var (_, clock, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        string? refusal = null;
        b.Career.RequestRejected += (reason, _) => refusal = reason;

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        b.Career.ReportTask(jobId, 0);
        Pump(server, new[] { a, b });
        Assert.Null(refusal);

        clock.Advance(16_000);
        Pump(server, new[] { a, b });

        Assert.Contains("did not confirm", refusal);
        Assert.Equal(JobLifecycle.Claimed, b.Career.Jobs[jobId].State); // still claimed — retryable
    }

    [Fact]
    public void Abandoning_an_external_job_kills_it_everywhere()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        JobLifecycle? finalState = null;
        a.Career.JobChanged += job => { if (job.Def.Id == jobId) finalState = job.State; };

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        b.Career.AbandonJob(jobId);
        Pump(server, new[] { a, b });

        Assert.Equal(JobLifecycle.Expired, finalState);   // broadcast as dead, not re-offered
        Assert.False(a.Career.Jobs.ContainsKey(jobId));
        Assert.False(b.Career.Jobs.ContainsKey(jobId));
        Assert.False(server.Career.Registry.Jobs.ContainsKey(jobId));
    }

    [Fact]
    public void Grace_expiry_on_an_external_claim_kills_the_job_too()
    {
        var (_, clock, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });

        b.Leave();
        Pump(server, new[] { a, b });
        clock.Advance(11_000);                            // past ReconnectGraceMs
        Pump(server, new[] { a });

        Assert.False(a.Career.Jobs.ContainsKey(jobId));
        Assert.False(server.Career.Registry.Jobs.ContainsKey(jobId));
    }

    [Fact]
    public void Available_external_jobs_are_not_persisted_but_claimed_ones_are()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob("SM-FH-01"));
        a.Career.RegisterExternalJob(ExternalJob("SM-FH-02"));
        Pump(server, new[] { a, b });
        int claimedId = b.Career.Jobs.Values.Single(j => j.Def.GameId == "SM-FH-02").Def.Id;
        b.Career.ClaimJob(claimedId);
        Pump(server, new[] { a, b });

        // Available externals are live-world mirrors — the next session's sweep re-offers them;
        // persisting them manufactures ghost jobs after a world reload (run-A finding). Claimed
        // ones persist: the reconnect-grace story restores them exactly.
        CareerSaveData save = server.Career.Registry.Capture();
        JobSave persisted = Assert.Single(save.Jobs);
        Assert.Equal("SM-FH-02", persisted.Def.GameId);
        Assert.Equal(JobLifecycle.Claimed, persisted.State);
    }

    [Fact]
    public void Host_grant_unlocks_a_license_gated_job_for_the_remote_claimant()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob(licenses: new[] { "S1" }));
        Pump(server, new[] { a, b });
        int jobId = b.Career.Jobs.Keys.Single();

        string? refusal = null;
        b.Career.RequestRejected += (reason, _) => refusal = reason;
        string? granted = null;
        b.Career.LicenseGranted += lic => granted = lic;

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        Assert.Contains("missing license: S1", refusal);   // the gate holds for a fresh profile

        a.Career.GrantExternalLicense("S1", b.LocalId!.Value); // the host-admin grant (M3.5c)
        Pump(server, new[] { a, b });
        Assert.Equal("S1", granted);                       // grantee's client learned it
        Assert.DoesNotContain("S1", a.Career.Licenses);    // the HOST's own scope is untouched

        b.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        Assert.Equal(JobLifecycle.Claimed, b.Career.Jobs[jobId].State);
        Assert.Equal(b.LocalId, b.Career.Jobs[jobId].ClaimantPeerId);
        Assert.True(server.Career.Registry.Ledger.ConservationHolds); // grants are charge-free
    }

    [Fact]
    public void Grants_to_disconnected_peers_and_from_non_world_sources_are_refused()
    {
        var (_, _, server, a, b) = Session();

        string? refusedA = null, refusedB = null;
        a.Career.RequestRejected += (reason, _) => refusedA = reason;
        b.Career.RequestRejected += (reason, _) => refusedB = reason;

        a.Career.GrantExternalLicense("S1", 99);           // nobody there
        b.Career.GrantExternalLicense("S1", a.LocalId!.Value); // B is not the world source
        Pump(server, new[] { a, b });

        Assert.Contains("not connected", refusedA);
        Assert.Contains("only the world source", refusedB);
        Assert.DoesNotContain("S1", a.Career.Licenses);
    }

    [Fact]
    public void World_source_reports_keep_the_direct_path_with_no_query()
    {
        var (_, _, server, a, b) = Session();
        a.Career.RegisterExternalJob(ExternalJob());
        Pump(server, new[] { a, b });
        int jobId = a.Career.Jobs.Keys.Single();

        int queries = 0;
        a.Career.CompleteQueryReceived += _ => queries++;

        // The host claims its own captured job (native take mirrored) and reports after its game
        // validated the turn-in — the M3.5a flow, which must not start asking itself questions.
        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        a.Career.ReportTask(jobId, 0);
        Pump(server, new[] { a, b });

        Assert.Equal(0, queries);
        Assert.Equal(500_00 + 250_00, a.Career.BalanceCents);
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }
}
