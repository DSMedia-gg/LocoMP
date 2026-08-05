using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// D20: pristine-profile eviction at grace lapse — the fix for the 24 h soak FAIL (unbounded
/// per-player-key career retention under join churn: Connect mints, nothing removed, heap and
/// save grow ~315 B per never-seen key forever). A profile indistinguishable from the fresh one
/// Connect would mint on return (balance == starting grant, licenses == starting floor, no claims,
/// no possessions) evicts invisibly; the ledger burns the grant so conservation stays the single
/// minted − burned == Σ accounts invariant. Anyone who ever earned or spent persists (D3).
/// </summary>
public class ProfileEvictionTests
{
    private static CareerConfig Config(ProgressionPreset preset = ProgressionPreset.PerPlayer)
    {
        return new CareerConfig
        {
            Preset = preset,
            StartingBalanceCents = 500_00,
            ClaimTtlMs = 60_000,
            ReconnectGraceMs = 10_000,
            TargetAvailableJobs = 4,
            JobSeed = 7,
            Stations = new[] { "SM", "GF", "HB" },
            JobTypes = new[] { new JobTypeSpec("FH", "steel", 100_00, 2, 4, null) },
            LicensePrices = new Dictionary<string, long> { ["hazmat"] = 150_00 },
        };
    }

    private static (CareerRegistry career, ManualClock clock) Fresh(CareerConfig? config = null)
    {
        var clock = new ManualClock();
        var career = new CareerRegistry(config ?? Config(), clock);
        return (career, clock);
    }

    private static void Lapse(CareerRegistry career, ManualClock clock)
    {
        clock.Advance(10_000);
        CareerTick tick = career.Tick();
        foreach (string key in tick.LapsedPlayers) career.TryEvictPristine(key);
    }

    [Fact]
    public void Pristine_profile_evicts_at_grace_lapse_and_burns_its_grant()
    {
        var (career, clock) = Fresh();
        career.Connect("drifter", "Drifter");
        career.Disconnect("drifter");

        clock.Advance(10_000);
        CareerTick tick = career.Tick();
        Assert.Contains("drifter", tick.LapsedPlayers);
        Assert.True(career.TryEvictPristine("drifter"));

        Assert.False(career.Profiles.ContainsKey("drifter"));
        Assert.Equal(0, career.BalanceFor("drifter"));
        Assert.Equal(500_00, career.Ledger.TotalBurned);           // burn-on-evict
        Assert.True(career.Ledger.ConservationHolds);

        // A return is a fresh first sight: a new grant mints, and the books still balance.
        career.Connect("drifter", "Drifter");
        Assert.Equal(500_00, career.BalanceFor("drifter"));
        Assert.Equal(1000_00, career.Ledger.TotalMinted);
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_profile_that_ever_earned_or_spent_survives_lapse()
    {
        var (career, clock) = Fresh();

        // Earned: a completed job moved the balance above the grant.
        career.Connect("earner", "Earner");
        career.Tick();
        JobRecord job = career.Jobs.Values.First(j => j.State == JobLifecycle.Available);
        Assert.True(career.TryClaim("earner", job.Def.Id, out _, out _));
        for (int i = 0; i < job.Def.Tasks.Count; i++)
            Assert.True(career.TryReportTask("earner", job.Def.Id, i, out _, out _, out _, out _));

        // Spent: a license purchase moved the balance below the grant.
        career.Connect("spender", "Spender");
        Assert.True(career.TryPurchaseLicense("spender", "hazmat", out _, out _));

        // Progressed without spending: a charge-free native grant put a license above the floor
        // while the wallet still reads exactly the grant — licenses alone must block eviction.
        career.Connect("granted", "Granted");
        Assert.True(career.TryGrantExternal("granted", "DE2", out _, out _));

        foreach (string key in new[] { "earner", "spender", "granted" }) career.Disconnect(key);
        Lapse(career, clock);

        Assert.True(career.Profiles.ContainsKey("earner"));
        Assert.True(career.Profiles.ContainsKey("spender"));
        Assert.True(career.Profiles.ContainsKey("granted"));
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_claim_released_by_the_lapse_no_longer_blocks_eviction()
    {
        // The lapse releases the claim (progress reset, job back on the board) BEFORE eviction is
        // judged — so a claimant who walked away mid-job and never earned is still pristine: on
        // return they would find the same fresh profile either way. This pins the D20 semantics
        // that "no claims" is evaluated after the lapse release, not before.
        var (career, clock) = Fresh();
        career.Connect("walker", "Walker");
        career.Tick();
        JobRecord job = career.Jobs.Values.First(j => j.State == JobLifecycle.Available);
        Assert.True(career.TryClaim("walker", job.Def.Id, out _, out _));
        career.Disconnect("walker");

        clock.Advance(10_000);
        CareerTick tick = career.Tick();
        Assert.Contains(job, tick.ReleasedJobs);
        Assert.True(career.TryEvictPristine("walker"));
        Assert.False(career.Profiles.ContainsKey("walker"));
        Assert.Equal(JobLifecycle.Available, job.State);
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void Online_unknown_and_still_claimed_keys_never_evict()
    {
        var (career, _) = Fresh();
        career.Connect("alice", "Alice");
        Assert.False(career.TryEvictPristine("alice"));      // online — Connect cancels any hold
        Assert.False(career.TryEvictPristine("nobody"));     // never seen

        // A job still naming the key (claim TTL not yet due, grace forced by EnsureGrace) blocks.
        career.Tick();
        JobRecord job = career.Jobs.Values.First(j => j.State == JobLifecycle.Available);
        Assert.True(career.TryClaim("alice", job.Def.Id, out _, out _));
        career.Disconnect("alice");
        Assert.False(career.TryEvictPristine("alice"));      // lapse never reached on purpose
        Assert.True(career.Profiles.ContainsKey("alice"));
    }

    [Fact]
    public void Shared_career_member_evicts_without_touching_the_communal_wallet()
    {
        var (career, clock) = Fresh(Config(ProgressionPreset.SharedCareer));
        career.Connect("alice", "Alice");
        career.Connect("bob", "Bob");
        Assert.Equal(500_00, career.Ledger.BalanceOf(ProgressionPolicy.SharedAccount));

        career.Disconnect("bob");
        Lapse(career, clock);

        Assert.False(career.Profiles.ContainsKey("bob"));
        Assert.True(career.Profiles.ContainsKey("alice"));
        Assert.Equal(500_00, career.Ledger.BalanceOf(ProgressionPolicy.SharedAccount)); // untouched
        Assert.Equal(0, career.Ledger.TotalBurned);          // nothing personal existed to burn
        Assert.True(career.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_restored_save_bloated_with_pristine_profiles_drains_at_grace()
    {
        // The 24 h soak's save grew to 3.6 MB of never-returning GUID profiles. A pre-D20 save has
        // no grace entries for them (their holds lapsed long ago) — restore must put every offline
        // profile back under a hold so the normal lapse → evict path drains the dead weight.
        var (career, _) = Fresh();
        for (int i = 0; i < 5; i++) career.Connect($"ghost-{i}", $"Ghost {i}");
        career.Connect("veteran", "Veteran");
        Assert.True(career.TryPurchaseLicense("veteran", "hazmat", out _, out _));
        CareerSaveData save = career.Capture();
        save.GraceRemainingMs.Clear();                        // the pre-D20 shape: no holds at all

        var clock2 = new ManualClock();
        var restored = new CareerRegistry(Config(), clock2, save);
        Assert.Equal(6, restored.Profiles.Count);

        clock2.Advance(10_000);
        CareerTick tick = restored.Tick();
        Assert.Equal(6, tick.LapsedPlayers.Count);
        foreach (string key in tick.LapsedPlayers) restored.TryEvictPristine(key);

        Assert.Single(restored.Profiles);                     // only the veteran earned their keep
        Assert.True(restored.Profiles.ContainsKey("veteran"));
        Assert.True(restored.Ledger.ConservationHolds);

        // And the NEXT capture is the shrunken save — evicted profiles are simply absent.
        Assert.Single(restored.Capture().Profiles);
    }

    // ── session level: the NetServer wiring (possession gate + event) ──

    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static ItemConfig ItemShop() => new()
    {
        ShopPrices = new Dictionary<string, long> { ["flyer"] = 0, ["lantern"] = 50_00 },
        AcceptExternalItems = true,
    };

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Join_churn_evicts_pristine_profiles_and_the_roster_stays_bounded()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = new ServerConfig(Identity, career: Config(), items: ItemShop());
        var server = new NetServer(hub.Server, config, clock);
        var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "key-host");
        Pump(server, new[] { host });

        var evicted = new List<string>();
        server.ProfileEvicted += evicted.Add;

        for (int i = 0; i < 10; i++)
        {
            var drifter = new NetClient(hub.Connect(out _), Identity, $"Drifter{i}",
                clock, playerKey: $"drift-{i}");
            Pump(server, new[] { host, drifter });
            drifter.Leave();
            Pump(server, new[] { host });
        }
        Assert.Equal(11, server.Career.Registry.Profiles.Count); // all still under grace

        clock.Advance(10_000);
        Pump(server, new[] { host });

        // The churn evicted in full; the live host survives; the books balance.
        Assert.Equal(10, evicted.Count);
        Assert.Single(server.Career.Registry.Profiles);
        Assert.True(server.Career.Registry.Profiles.ContainsKey("key-host"));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void A_lapsed_holder_of_a_possession_is_not_evicted()
    {
        // A FREE shop item (price 0 — the catalog supports it) is the isolation lever: the wallet
        // still reads exactly the starting grant, so ONLY the possession gate can block eviction.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var config = new ServerConfig(Identity, career: Config(), items: ItemShop());
        var server = new NetServer(hub.Server, config, clock);
        var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "key-host");
        var carrier = new NetClient(hub.Connect(out _), Identity, "Carrier", clock, playerKey: "key-carrier");
        Pump(server, new[] { host, carrier });

        carrier.Items.Purchase("flyer");
        Pump(server, new[] { host, carrier });
        Assert.Equal(500_00, server.Career.Registry.BalanceFor("key-carrier")); // wallet pristine

        carrier.Leave();
        Pump(server, new[] { host });
        clock.Advance(10_000);
        Pump(server, new[] { host });

        // The minted possession rode grace and released at the lapse — but it was still held when
        // pristine was judged, so the profile persists (their dropped flyer is out there).
        Assert.True(server.Career.Registry.Profiles.ContainsKey("key-carrier"));
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }
}
