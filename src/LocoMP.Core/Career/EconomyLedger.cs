using System;
using System.Collections.Generic;

namespace LocoMP.Core.Career;

/// <summary>
/// Server-authoritative money store. Amounts are integer cents so conservation is EXACT: at all
/// times sum(balances) == TotalMinted − TotalBurned (the 07 §M3 economy invariant). In M3 money
/// only enters the world by minting (job payouts, starting grants) and only leaves by burning
/// (fees) — there is no other path, and clients never supply deltas (03 §9: economy deltas are
/// server-computed). The career layer computes every movement; this class enforces the arithmetic.
/// </summary>
public sealed class EconomyLedger
{
    private readonly Dictionary<string, long> _accounts = new(StringComparer.Ordinal);

    /// <summary>Every account and its balance in cents. Account keys are player keys (per-player
    /// preset) or <see cref="ProgressionPolicy.SharedAccount"/> — the policy layer decides.</summary>
    public IReadOnlyDictionary<string, long> Accounts => _accounts;

    public long TotalMinted { get; private set; }
    public long TotalBurned { get; private set; }

    public long BalanceOf(string account) => _accounts.TryGetValue(account, out long b) ? b : 0;

    /// <summary>New money enters the world (job payout, starting grant).</summary>
    public void Mint(string account, long amountCents)
    {
        if (amountCents < 0) throw new ArgumentOutOfRangeException(nameof(amountCents));
        _accounts[account] = BalanceOf(account) + amountCents;
        TotalMinted += amountCents;
    }

    /// <summary>Money leaves the world (license fee, service fee). Refuses overdrafts — a balance
    /// can never go negative, so "insufficient funds" is a validation reason, not a debt.</summary>
    public bool TryBurn(string account, long amountCents, out string? reason)
    {
        if (amountCents < 0) throw new ArgumentOutOfRangeException(nameof(amountCents));
        long balance = BalanceOf(account);
        if (balance < amountCents)
        {
            reason = $"insufficient funds: have {balance}, need {amountCents}";
            return false;
        }
        _accounts[account] = balance - amountCents;
        TotalBurned += amountCents;
        reason = null;
        return true;
    }

    /// <summary>Sum of every balance. With the minted/burned totals this is the conservation
    /// oracle the M3 exit tests and the economy fuzz assert after every operation.</summary>
    public long SumOfBalances
    {
        get
        {
            long sum = 0;
            foreach (long b in _accounts.Values) sum += b;
            return sum;
        }
    }

    public bool ConservationHolds => SumOfBalances == TotalMinted - TotalBurned;

    /// <summary>Replace all state from a save (persistence v1).</summary>
    internal void Restore(IReadOnlyDictionary<string, long> accounts, long minted, long burned)
    {
        _accounts.Clear();
        foreach (KeyValuePair<string, long> kv in accounts) _accounts[kv.Key] = kv.Value;
        TotalMinted = minted;
        TotalBurned = burned;
    }
}
