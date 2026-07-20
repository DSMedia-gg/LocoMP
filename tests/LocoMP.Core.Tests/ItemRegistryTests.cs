using System;
using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Presence;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The item authority in isolation (game-free, 03 §11): mint/pickup/drop/despawn enforce the
/// SINGLE-LOCATION invariant (world XOR one scope's possession), the item-count conservation oracle
/// never lies, possession routes through the progression policy (shared career pools inventory), and
/// a capture → restore round-trip resumes the exact world.
/// </summary>
public class ItemRegistryTests
{
    private static ItemRegistry Registry(ProgressionPreset preset = ProgressionPreset.PerPlayer) =>
        new(new ProgressionPolicy(preset));

    private static Pose At(float x, float z) => new(x, 0f, z, 0f, 0f, 0f, 1f);

    [Fact]
    public void Spawn_in_world_and_in_possession_mint_distinct_located_ids()
    {
        ItemRegistry reg = Registry();
        ItemRecord world = reg.SpawnInWorld("lantern", At(10, 20), "");
        ItemRecord held = reg.SpawnInPossession("key-alice", "lantern", "");

        Assert.NotEqual(world.Def.Id, held.Def.Id);
        Assert.Equal(ItemLocationKind.World, world.Location);
        Assert.Equal(At(10, 20), world.WorldPose);
        Assert.Equal(ItemLocationKind.Possessed, held.Location);
        Assert.Equal("key-alice", held.OwnerScope);
        Assert.Equal(2, reg.Items.Count);
        Assert.True(reg.ItemConservationHolds);
    }

    [Fact]
    public void Pickup_moves_world_to_possession_and_is_exclusive()
    {
        ItemRegistry reg = Registry();
        int id = reg.SpawnInWorld("lantern", At(0, 0), "").Def.Id;

        Assert.True(reg.TryPickUp("key-alice", id, out ItemRecord? rec, out _));
        Assert.Equal(ItemLocationKind.Possessed, rec!.Location);
        Assert.Equal("key-alice", rec.OwnerScope);

        // A physical item is picked up once: a second pickup (even by the holder) is refused.
        Assert.False(reg.TryPickUp("key-bob", id, out _, out string? reason));
        Assert.Contains("already held", reason);
        Assert.True(reg.ItemConservationHolds);
    }

    [Fact]
    public void A_locked_world_item_refuses_pickup_from_everyone()
    {
        ItemRegistry reg = Registry();
        int id = reg.SpawnInWorld("Map", At(3, 4), "", locked: true).Def.Id; // a set-down personal essential

        // Look, but don't touch: no scope may take it (the owner reclaims it natively, never a request).
        Assert.False(reg.TryPickUp("key-alice", id, out _, out string? reason));
        Assert.Contains("personal item", reason);
        Assert.Equal(ItemLocationKind.World, reg.Items[id].Location); // stays put
        Assert.True(reg.Items[id].WorldLocked);
        Assert.True(reg.ItemConservationHolds);
    }

    [Fact]
    public void Locked_flag_survives_capture_and_restore()
    {
        ItemRegistry reg = Registry();
        int locked = reg.SpawnInWorld("wallet", At(1, 1), "", locked: true).Def.Id;
        int free = reg.SpawnInWorld("lantern", At(2, 2), "").Def.Id;

        ItemRegistry restored = new(new ProgressionPolicy(ProgressionPreset.PerPlayer), reg.Capture());
        Assert.True(restored.Items[locked].WorldLocked);
        Assert.False(restored.Items[free].WorldLocked);
        Assert.False(restored.TryPickUp("key-alice", locked, out _, out _)); // still look-but-don't-touch
        Assert.True(restored.TryPickUp("key-alice", free, out _, out _));     // the lantern is still free
    }

    [Fact]
    public void Drop_returns_possession_to_the_world_only_for_the_holder()
    {
        ItemRegistry reg = Registry();
        int id = reg.SpawnInPossession("key-alice", "lantern", "").Def.Id;

        Assert.False(reg.TryDrop("key-bob", id, At(5, 5), out _, out string? reason)); // not bob's
        Assert.Contains("not holding", reason);

        Assert.True(reg.TryDrop("key-alice", id, At(5, 5), out ItemRecord? rec, out _));
        Assert.Equal(ItemLocationKind.World, rec!.Location);
        Assert.Equal(At(5, 5), rec.WorldPose);
        Assert.Equal("", rec.OwnerScope);
    }

    [Fact]
    public void Unknown_item_ids_are_refused_everywhere()
    {
        ItemRegistry reg = Registry();
        Assert.False(reg.TryPickUp("key-alice", 99, out _, out _));
        Assert.False(reg.TryDrop("key-alice", 99, At(0, 0), out _, out _));
        Assert.False(reg.TryDespawn(99, out _, out _));
    }

    [Fact]
    public void Despawn_removes_the_item_and_conservation_tracks_it()
    {
        ItemRegistry reg = Registry();
        int id = reg.SpawnInWorld("lantern", At(0, 0), "").Def.Id;

        Assert.True(reg.TryDespawn(id, out _, out _));
        Assert.False(reg.Items.ContainsKey(id));
        Assert.Empty(reg.Items);
        Assert.Equal(1, reg.TotalSpawned);
        Assert.Equal(1, reg.TotalDespawned);
        Assert.True(reg.ItemConservationHolds);
    }

    [Fact]
    public void Shared_career_pools_inventory_so_anyone_can_drop_a_communal_item()
    {
        ItemRegistry reg = Registry(ProgressionPreset.SharedCareer);
        int id = reg.SpawnInWorld("lantern", At(0, 0), "").Def.Id;

        // Alice picks it up; under the shared preset it lands in the ONE shared scope...
        Assert.True(reg.TryPickUp("key-alice", id, out ItemRecord? rec, out _));
        Assert.Equal(ProgressionPolicy.SharedAccount, rec!.OwnerScope);
        // ...so Bob may drop it — the item is "freely shared" (02 §6 inventory row).
        Assert.True(reg.TryDrop("key-bob", id, At(9, 9), out _, out _));
    }

    [Fact]
    public void Per_player_keeps_inventory_private_between_players()
    {
        ItemRegistry reg = Registry(ProgressionPreset.PerPlayer);
        int id = reg.SpawnInPossession("key-alice", "lantern", "").Def.Id;
        Assert.False(reg.TryDrop("key-bob", id, At(0, 0), out _, out _)); // bob can't touch alice's
        Assert.True(reg.TryDrop("key-alice", id, At(0, 0), out _, out _));
    }

    [Fact]
    public void State_update_keeps_identity_and_location()
    {
        ItemRegistry reg = Registry();
        ItemRecord rec = reg.SpawnInWorld("lantern", At(1, 2), "off");
        int id = rec.Def.Id;

        Assert.True(reg.TryUpdateState(id, "lit", out ItemRecord? updated, out _));
        Assert.Equal("lit", updated!.Def.State);
        Assert.Equal(id, updated.Def.Id);
        Assert.Equal(ItemLocationKind.World, updated.Location);
        Assert.Equal(At(1, 2), updated.WorldPose);
    }

    [Fact]
    public void Capture_and_restore_resume_the_exact_world()
    {
        ItemRegistry reg = Registry();
        ItemRecord w = reg.SpawnInWorld("crate", At(100, 200), "sealed");
        reg.SpawnInPossession("key-alice", "lantern", "lit");
        reg.TryPickUp("key-bob", w.Def.Id, out _, out _); // now a crate held by bob

        ItemRegistry restored = new(new ProgressionPolicy(ProgressionPreset.PerPlayer), reg.Capture());

        Assert.Equal(2, restored.Items.Count);
        ItemRecord crate = restored.Items[w.Def.Id];
        Assert.Equal(ItemLocationKind.Possessed, crate.Location);
        Assert.Equal("key-bob", crate.OwnerScope);
        Assert.Equal("sealed", crate.Def.State);
        Assert.True(restored.ItemConservationHolds);

        // The id counter survived: a fresh spawn does not collide with a restored id.
        ItemRecord fresh = restored.SpawnInWorld("shovel", At(0, 0), "");
        Assert.DoesNotContain(fresh.Def.Id, new[] { w.Def.Id });
    }

    [Fact]
    public void Fuzz_preserves_single_location_and_item_conservation_across_saves()
    {
        var rng = new Random(20260719);
        string[] scopes = { "key-a", "key-b", "key-c" };
        var reg = Registry();

        for (int op = 0; op < 2_000; op++)
        {
            switch (rng.Next(5))
            {
                case 0:
                    reg.SpawnInWorld("item", At(rng.Next(-500, 500), rng.Next(-500, 500)), "");
                    break;
                case 1:
                    reg.SpawnInPossession(scopes[rng.Next(scopes.Length)], "item", "");
                    break;
                case 2: // pick up a random world item as a random player (may legitimately no-op)
                {
                    int[] world = reg.Items.Values.Where(i => i.Location == ItemLocationKind.World)
                        .Select(i => i.Def.Id).ToArray();
                    if (world.Length > 0)
                        reg.TryPickUp(scopes[rng.Next(scopes.Length)], world[rng.Next(world.Length)], out _, out _);
                    break;
                }
                case 3: // drop a random held item as its OWNER (so it commits) at a random pose
                {
                    ItemRecord[] held = reg.Items.Values.Where(i => i.Location == ItemLocationKind.Possessed).ToArray();
                    if (held.Length > 0)
                    {
                        ItemRecord pick = held[rng.Next(held.Length)];
                        reg.TryDrop(pick.OwnerScope, pick.Def.Id, At(rng.Next(-9, 9), rng.Next(-9, 9)), out _, out _);
                    }
                    break;
                }
                case 4: // despawn a random live item
                {
                    int[] ids = reg.Items.Keys.ToArray();
                    if (ids.Length > 0) reg.TryDespawn(ids[rng.Next(ids.Length)], out _, out _);
                    break;
                }
            }

            // Oracles after EVERY op: item conservation exact, and the single-location invariant
            // holds structurally (a world item carries no scope; a held item carries one).
            Assert.True(reg.ItemConservationHolds);
            foreach (ItemRecord rec in reg.Items.Values)
            {
                if (rec.Location == ItemLocationKind.World) Assert.Equal("", rec.OwnerScope);
                else Assert.NotEqual("", rec.OwnerScope);
            }

            if (op % 250 == 249)
            {
                var round = new ItemRegistry(new ProgressionPolicy(ProgressionPreset.PerPlayer), reg.Capture());
                Assert.Equal(reg.Items.Count, round.Items.Count);
                Assert.True(round.ItemConservationHolds);
                reg = round; // keep fuzzing the restored world — persistence proven under live state
            }
        }
    }
}
