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

    private readonly NetClient _client;
    private readonly Action<string> _log;
    private readonly bool _isHost;
    private readonly double _savedNativeMoney;
    private readonly bool _saved;
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

        Inventory? inventory = Inventory.Instance;
        if (inventory != null)
        {
            _savedNativeMoney = inventory.PlayerMoney;
            _saved = true;
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
        if (!_haveCareer) return;
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

    public void Dispose()
    {
        _active = null;
        _client.Career.CareerStateReceived -= OnCareerState;
        _client.Career.WalletChanged -= OnWalletChanged;
        _client.Career.RequestRejected -= OnRejected;
        SaveGameManager.AboutToSave -= OnAboutToSave;
        if (!_saved) return;
        try
        {
            Inventory? inventory = Inventory.Instance;
            if (inventory != null)
            {
                inventory.SetMoney(_savedNativeMoney);
                _log($"[career] native money restored to ${_savedNativeMoney:F2}");
            }
        }
        catch (Exception e)
        {
            _log("[career] native money restore failed (world unloading?): " + e.Message);
        }
    }
}
