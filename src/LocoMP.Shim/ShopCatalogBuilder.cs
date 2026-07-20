using System;
using System.Collections.Generic;
using DV.Shops;

namespace LocoMP.Shim;

/// <summary>
/// Builds the host's shop catalog (<see cref="LocoMP.Core.Items.ItemConfig.ShopPrices"/>) from the
/// LIVE game world — the item analog of <see cref="CareerConfigBuilder"/>. DV's own
/// <see cref="GlobalShopController"/> already holds every purchasable item and its price, so this is
/// a pure read: <c>itemPrefabName → price in cents</c>. Core stays game-free (hard rule 3); the
/// dictionary is the only thing crossing the boundary.
///
/// The purchase transaction itself lives entirely in Core (M4.1 charge-then-mint) — a client's
/// buy debits the CLIENT's wallet and mints the item into its possession. This catalog is only the
/// menu: it tells the server what is for sale (an unlisted prefab's purchase is refused) and rides
/// the join burst so a client can render its Buy buttons.
///
/// Deliberately NOT covered this slice (banked): live stock (<c>ItemsInStock</c>/restock) replication,
/// and routing a client's purchase through the host's real shelf. A client's buy is a LocoMP mint,
/// independent of the host's stock, which is correct for the win condition and keeps this a read-only
/// builder — no Harmony, no new game reference (GlobalShopController lives in Assembly-CSharp).
/// </summary>
public static class ShopCatalogBuilder
{
    /// <summary>Read the live shop shelf into a prefab→cents map. Empty (never null) if the shop
    /// controller isn't up yet — the session just hosts with nothing for sale, like an empty job
    /// board. Non-career game modes zero non-career prices and disable career-only items, which DV
    /// has already applied to this data by host time — so the catalog reflects the active mode.</summary>
    public static IReadOnlyDictionary<string, long> Build(Action<string> log)
    {
        var prices = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            GlobalShopController gsc = GlobalShopController.Instance;
            if (gsc == null || gsc.shopItemsData == null)
            {
                log("[shop] no shop controller yet — hosting with nothing for sale");
                return prices;
            }

            int skipped = 0;
            foreach (ShopItemData d in gsc.shopItemsData)
            {
                if (d == null || d.item == null || string.IsNullOrEmpty(d.item.ItemPrefabName))
                {
                    skipped++;
                    continue;
                }
                if (d.unavailableDueToGameMode) continue; // career-only items outside a Career session

                // basePrice is in dollars; the ledger is integer cents. Round to dodge float drift
                // ($49.99 → 4999, not 4998). Duplicate prefabs across shops collapse to one entry —
                // DV's own GetShopItemData(name) resolves by first match, so the price is the same.
                prices[d.item.ItemPrefabName] = (long)Math.Round(d.basePrice * 100.0);
            }

            int shops = gsc.globalShopList != null ? gsc.globalShopList.Count : 0;
            log($"[shop] catalog: {prices.Count} item(s) for sale from {shops} shop(s)" +
                (skipped > 0 ? $" ({skipped} malformed entr{(skipped == 1 ? "y" : "ies")} skipped)" : ""));
        }
        catch (Exception e)
        {
            log($"[shop] catalog build failed ({e.Message}) — hosting with nothing for sale");
            prices.Clear();
        }
        return prices;
    }
}
