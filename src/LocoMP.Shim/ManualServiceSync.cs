using System;
using DV.ThingTypes;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// M4 manual service: bills the buy-button-bypassing <c>PitStopStation.RefillAll()</c> /
/// <c>RepairAll()</c> paths through the LocoMP wallet, so they can never hand out a free full service
/// inside a session. The metered manual-service loop needs nothing here — its <c>Buy()</c> already
/// rides D14's WalletMirror; only these two direct-apply shortcuts skip the register.
///
/// HOST-ONLY. The only serviceable cars in a session are the host's real ones (remote cars are
/// kinematic ghosts with no pit-stop interaction), and a self-scope <c>FeeExternal</c> (target 0)
/// bills the world source's own wallet — which is the host's. A joined client servicing something is
/// not a reachable case, so we don't arm the hook there.
///
/// The equivalent cost is read straight off the bay's own resource modules: each module exposes the
/// refill deficit (<c>BuyMaxLimit</c>) and the game's own per-unit price (<c>Data.pricePerUnit</c>,
/// already set for the car in the bay), so we bill exactly what the metered path would have — no
/// re-implemented price formula. It's best-effort: a module with an unknown/zero price is skipped
/// rather than guessed, and we log what was billed (or that a deficit went unpriced) so the batched
/// smoke pass can see the guard fire.
/// </summary>
public sealed class ManualServiceSync : IDisposable
{
    private readonly NetClient _client;
    private readonly bool _isHost;
    private readonly Action<string> _log;

    public ManualServiceSync(NetClient client, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _isHost = isHost;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        if (_isHost)
        {
            ManualServiceHook.RefillAll = station => Bill(station, ResourceTypes.ConsumableResources, "refuel");
            ManualServiceHook.RepairAll = station => Bill(station, ResourceTypes.DamageableResources, "repair");
        }
    }

    /// <summary>Sum the cost of servicing the given resource set on the bay car and burn it from the
    /// host's own wallet. Called from the prefix, before the native apply zeroes the deficits.</summary>
    private void Bill(PitStopStation station, ResourceType[] set, string kind)
    {
        LocoResourceModule[]? modules = station.locoResourceModules?.resourceModules;
        if (modules == null) return;

        double dollars = 0.0;
        bool unpriced = false;
        foreach (LocoResourceModule module in modules)
        {
            if (module == null || Array.IndexOf(set, module.resourceType) < 0) continue;
            float deficit;
            float pricePerUnit;
            try
            {
                deficit = module.BuyMaxLimit;               // AbsoluteMaxValue − PreviouslyOwnedUnits
                pricePerUnit = module.Data.pricePerUnit;    // set for the bay car; −1 = unknown
            }
            catch { continue; }                             // a module without a live car contributes nothing
            if (deficit <= 0f) continue;
            if (pricePerUnit <= 0f) { unpriced = true; continue; }
            dollars += (double)deficit * pricePerUnit;
        }

        long cents = (long)Math.Round(dollars * 100.0);
        if (cents > 0)
        {
            _client.Career.ReportExternalFee(cents, $"manual service ({kind})", 0);
            _log($"[service] free {kind} at the bay — billed ${dollars:F2} to your wallet");
        }
        else if (unpriced)
        {
            _log($"[service] free {kind} at the bay: no priced deficit to bill (nothing charged)");
        }
    }

    public void Dispose()
    {
        if (_isHost)
        {
            ManualServiceHook.RefillAll = null;
            ManualServiceHook.RepairAll = null;
        }
    }
}
