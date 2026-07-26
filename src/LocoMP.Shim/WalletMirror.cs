using System;
using DV.CashRegister;
using DV.Common;
using DV.InventorySystem;
using HarmonyLib;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// D14/M4: in a session the native money display IS the LocoMP wallet (host AND joined client — the
/// client mirror keeps money correct and its comms-radio affordability checks the right wallet; only
/// the host reports native register purchases as fees). The pre-session balance is
/// saved and restored on leave; in between, <see cref="Inventory.PlayerMoney"/> is reconciled to
/// the ledger balance, and every FINALIZED register purchase (career manager licenses and fees,
/// module shops) is reported to the server as an external fee — so the native "can I afford this"
/// gate and the ledger charge read the same number by construction. Buy() is the capture point
/// because deposits are refundable until it commits; reconciliation waits until no register holds
/// deposited cash, so a leftover-return can't be half-applied. Native income paths other than the
/// (already suppressed) wage printer are NOT mirrored — the periodic reconcile deliberately
/// reverts them, because the ledger is the single source of truth (03 §9).
/// </summary>
public sealed class WalletMirror : IDisposable
{
    private const double ReconcileIntervalSeconds = 0.75;

    /// <summary>Below this, PlayerMoney and the ledger agree (float dust from double dollars).</summary>
    private const double AgreementEpsilonDollars = 0.005;

    private static WalletMirror? _active;

    /// <summary>A real balance that could not be written yet because a cash register still holds the player's
    /// deposited cash. STATIC because it must outlive this instance: the session — and so the mirror — ends at
    /// Leave, but the escrow only clears when the player takes their wallet back out of the machine, which can
    /// be much later. Pumped from the session controller's Update, which runs whenever the mod is enabled.
    ///
    /// This exists because deposited cash has already been subtracted from <c>PlayerMoney</c> and is handed
    /// back on withdrawal. Writing the real balance while it is outstanding means the escrow lands ON TOP of
    /// it — observed in-game 2026-07-27: $1550 real + ~$1300 escrowed session cash = $2896.17, i.e. the
    /// session wallet laundered into the career, repeatable at will.
    ///
    /// Waiting is the fix rather than arithmetic (<c>saved - escrow</c>): that cannot be clamped honestly when
    /// the escrow exceeds the real balance, and it silently confiscates the deposit if the transaction
    /// completes instead of returning it. Waiting is correct in every case, because once the cash is back in
    /// <c>PlayerMoney</c> a plain overwrite discards exactly the session money and keeps exactly the real
    /// balance.</summary>
    private static double? _pendingRealBalance;
    private static Action<string>? _pendingLog;

    private readonly NetClient _client;
    private readonly Action<string> _log;
    private readonly bool _isHost;
    private readonly double _savedNativeMoney;
    private readonly bool _saved;
    /// <summary>Set once the player's real money is back — a terminal latch, not a toggle. Blocks any
    /// further reconcile so the session wallet can never be written over the restored balance.</summary>
    private bool _restored;
    /// <summary>Keeps the "world already gone" note to one line — both the unload callback and Dispose call
    /// the restore, and on that path both take the same branch.</summary>
    private bool _notedWorldGone;
    private bool _haveCareer;
    private double _reconcileAccum;

    public static void Install(Harmony harmony, Action<string> log)
    {
        // Buy() is virtual and Harmony hooks one method BODY — patch each override that finalizes
        // a purchase (both derive straight from CashRegisterBase; neither shadows the other).
        var prefix = new HarmonyMethod(typeof(WalletMirror), nameof(BuyPrefix));
        var postfix = new HarmonyMethod(typeof(WalletMirror), nameof(BuyPostfix));
        harmony.Patch(AccessTools.Method(typeof(CashRegisterCareerManager), nameof(CashRegisterCareerManager.Buy)),
            prefix: prefix, postfix: postfix);
        harmony.Patch(AccessTools.Method(typeof(CashRegisterWithModules), nameof(CashRegisterWithModules.Buy)),
            prefix: prefix, postfix: postfix);
        log("[career] wallet mirror hooks installed (engage while hosting)");
    }

    /// <summary>The cost must be read BEFORE Buy() runs — a committed transaction clears it.</summary>
    private static void BuyPrefix(CashRegisterBase __instance, out double __state) =>
        __state = __instance.GetTotalCost();

    private static void BuyPostfix(CashRegisterBase __instance, bool __result, double __state)
    {
        if (__result && __state > 0) _active?.OnNativePurchase(__instance, __state);
    }

    public WalletMirror(NetClient client, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _isHost = isHost;
        _log = log;
        _active = this;

        // A previous session may have left a real balance pending on escrow. That pending figure IS the
        // player's true pre-session money, and it takes precedence over PlayerMoney — which right now still
        // shows the OLD session's mirrored value, since the restore never got to run. Reading PlayerMoney
        // here instead would bake the previous session's wallet in as "real".
        if (_pendingRealBalance != null)
        {
            _savedNativeMoney = _pendingRealBalance.Value;
            _saved = true;
            _pendingRealBalance = null;
            _pendingLog = null;
            _log($"[career] carrying your real balance (${_savedNativeMoney:F2}) forward — the previous " +
                 "session could not restore it while a register held your cash");
        }
        else
        {
            Inventory? inventory = Inventory.Instance;
            if (inventory != null)
            {
                _savedNativeMoney = inventory.PlayerMoney;
                _saved = true;
            }
        }

        _client.Career.CareerStateReceived += OnCareerState;
        _client.Career.WalletChanged += OnWalletChanged;
        _client.Career.RequestRejected += OnRejected;
        // M3.5b (closes the D14 flagged debt): a mid-session native save must not persist the
        // MIRRORED balance into the SP save — swap the real SP amount back in just before the save
        // data is captured; the periodic reconcile re-mirrors within a second afterwards.
        SaveGameManager.AboutToSave += OnAboutToSave;
    }

    private void OnAboutToSave(SaveType _)
    {
        if (!_saved || !_haveCareer) return;
        Inventory? inventory = Inventory.Instance;
        if (inventory == null) return;
        inventory.SetMoney(_savedNativeMoney);
        _reconcileAccum = 0;
        _log($"[career] native save detected — SP balance (${_savedNativeMoney:F2}) written to the save; re-mirroring");
    }

    /// <summary>Pump from the session update loop: periodic reconcile catches leftover-deposit
    /// returns and any unmirrored native income, whichever order the game applied them in.</summary>
    public void Tick(double dt)
    {
        _reconcileAccum += dt;
        if (_reconcileAccum < ReconcileIntervalSeconds) return;
        _reconcileAccum = 0;
        Reconcile();
    }

    private void OnCareerState()
    {
        _haveCareer = true;
        Reconcile();
        _log($"[career] native money now mirrors the LocoMP wallet " +
             $"(${_client.Career.BalanceCents / 100.0:F2}; ${_savedNativeMoney:F2} restored on leave)");
    }

    private void OnWalletChanged(long _) => Reconcile();

    /// <summary>A fee refusal means the mirror drifted from the ledger (should be unreachable —
    /// the native UI gates affordability against the mirrored balance). Resync loudly.</summary>
    private void OnRejected(string reason, int _)
    {
        if (!reason.StartsWith("fee:", StringComparison.Ordinal)) return;
        _log($"[career] EXTERNAL FEE REFUSED ({reason}) — wallet mirror drifted; resynchronizing");
        Reconcile();
    }

    private void OnNativePurchase(CashRegisterBase register, double cost)
    {
        // Only the HOST is the world source, so only it reports native register purchases as fees.
        // On a joined client the wallet is mirrored for display + comms-radio affordability, but its
        // native register interactions are not the session's economy (the host's shops are).
        if (!_isHost) return;
        long cents = (long)Math.Round(cost * 100.0);
        string label = register is CashRegisterCareerManager ? "career manager" : "shop";
        _log($"[career] native purchase captured: ${cost:F2} at the {label}");
        _client.Career.ReportExternalFee(cents, label);
    }

    private void Reconcile()
    {
        // Once the real balance is back, the session's economy is over: re-mirroring would write the
        // SESSION wallet over the player's restored money. This matters because the world-unload event
        // fires from TrainSync.Tick, which runs BEFORE WalletMirror.Tick in the same frame — so without
        // this guard the restore is immediately clobbered by the very next reconcile.
        if (!_haveCareer || _restored) return;
        Inventory? inventory = Inventory.Instance;
        if (inventory == null || AnyRegisterHoldsCash()) return;
        double target = _client.Career.BalanceCents / 100.0;
        if (Math.Abs(inventory.PlayerMoney - target) > AgreementEpsilonDollars)
            inventory.SetMoney(target);
    }

    /// <summary>Deposited cash is money already subtracted from PlayerMoney but not yet spent or
    /// returned — reconciling while any register holds some would double-count the difference.</summary>
    private static bool AnyRegisterHoldsCash()
    {
        var registers = CashRegisterBase.allCashRegisters;
        if (registers == null) return false;
        foreach (CashRegisterBase register in registers)
        {
            if (register == null) continue;
            if (register.DepositedCash > 0 || register.IsProcessingTransaction) return true;
        }
        return false;
    }

    /// <summary>Put the player's REAL money back, and never fail quietly about it. Idempotent and safe to
    /// call from several teardown points.
    ///
    /// The path this exists for is <b>Leave-while-playing</b>: the world survives, the player carries on in
    /// single-player, and the next autosave persists whatever <c>Inventory</c> holds. Before this method
    /// existed, <see cref="Dispose"/> dropped the <c>AboutToSave</c> guard and restored nothing, so the
    /// SESSION wallet became the player's real money — observed in-game 2026-07-25, a $10,000 career came
    /// back holding the $2,000 session balance. That is the regression this must keep closed.
    ///
    /// On quit-to-menu it cannot run at all (see the null-Inventory branch) and does not need to: no save is
    /// written during that teardown, so the mirrored value dies with the world. Do not "fix" that branch by
    /// hunting an earlier hook — the unload signal is polled, so on that path there is no earlier hook to
    /// find.</summary>
    public bool RestoreNativeMoney()
    {
        if (!_saved || _restored) return _restored;
        try
        {
            Inventory? inventory = Inventory.Instance;
            if (inventory == null)
            {
                // The world is already gone, so there is nothing left to write to — and nothing at risk.
                // This is the quit-to-menu / load-another-save path, where the unload is detected by POLLING
                // (RailTrackRegistryBase dies, TrainSync notices next tick), so Inventory has always been
                // destroyed by the time we get here — there is no earlier moment to catch on this path.
                // It is safe because DV writes no save during that teardown (verified 2026-07-27: the final
                // save predates the unload, and AboutToSave had already swapped the real balance in), so the
                // mirrored in-memory value is simply discarded with the world; the balance on disk is real.
                // The path that DID lose money is Leave-while-playing, where the world survives and a later
                // autosave persists whatever Inventory holds — that one restores below, and must keep working.
                if (!_notedWorldGone)
                {
                    _notedWorldGone = true;
                    _log($"[career] world already gone — native money left as-is (your real ${_savedNativeMoney:F2} " +
                         "is what the save holds; nothing to restore on a quit-to-menu)");
                }
                return false;
            }
            // Escrow outstanding: writing now would let the machine hand the session's cash back ON TOP of
            // the real balance. Hand the figure to the static pump and say so plainly — the player is the
            // only one who can clear it, by taking their wallet out.
            if (AnyRegisterHoldsCash())
            {
                if (_pendingRealBalance == null)
                {
                    _pendingRealBalance = _savedNativeMoney;
                    _pendingLog = _log;
                    _log($"[career] a cash register still holds your deposited cash — your real balance " +
                         $"(${_savedNativeMoney:F2}) will be restored the moment you take your wallet back " +
                         "out of the machine. Don't save until then.");
                }
                return false;
            }

            inventory.SetMoney(_savedNativeMoney);
            _restored = true;   // also stops Reconcile from re-mirroring the session wallet over it
            _log($"[career] native money restored to ${_savedNativeMoney:F2}");
            return true;
        }
        catch (Exception e)
        {
            _log("[career] native money restore FAILED: " + e.Message);
            return false;
        }
    }

    /// <summary>Complete a restore that was deferred because a register held the player's deposited cash.
    /// Static and driven from the session controller's Update so it keeps running AFTER the session (and this
    /// mirror) are gone — the escrow can outlast the session by as long as the player leaves their wallet in
    /// the machine. Cheap no-op when nothing is pending, which is the normal case.</summary>
    public static void PumpPendingRestore()
    {
        if (_pendingRealBalance == null) return;

        if (!PresenceShim.WorldAlive)
        {
            // The world went away before the cash came back. Nothing to write and nothing at risk: DV writes
            // no save during that teardown, so the balance on disk is still the real one.
            double dropped = _pendingRealBalance.Value;
            Action<string>? log = _pendingLog;
            _pendingRealBalance = null;
            _pendingLog = null;
            log?.Invoke($"[career] world unloaded before your deposited cash came back — nothing to restore " +
                        $"(your save already holds your real ${dropped:F2})");
            return;
        }

        if (AnyRegisterHoldsCash()) return;    // still in the machine — keep waiting

        Inventory? inventory = Inventory.Instance;
        if (inventory == null) return;         // mid-load; try again next frame

        double target = _pendingRealBalance.Value;
        _pendingRealBalance = null;
        Action<string>? done = _pendingLog;
        _pendingLog = null;
        inventory.SetMoney(target);
        done?.Invoke($"[career] deposited cash returned — native money restored to ${target:F2}");
    }

    public void Dispose()
    {
        _active = null;
        _client.Career.CareerStateReceived -= OnCareerState;
        _client.Career.WalletChanged -= OnWalletChanged;
        _client.Career.RequestRejected -= OnRejected;
        // Restore BEFORE releasing the AboutToSave guard, never after: that guard is the only thing that
        // writes the real balance if DV saves, so dropping it while native money still holds the session
        // value is precisely what let the session wallet be persisted as the player's money.
        RestoreNativeMoney();
        SaveGameManager.AboutToSave -= OnAboutToSave;
    }
}
