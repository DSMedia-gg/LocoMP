using System;
using System.Linq;
using DV;
using DV.ThingTypes;
using LocoMP.Core.Session;

namespace LocoMP.Shim;

/// <summary>
/// D14: keeps the HOST's native license set and the LocoMP career in lockstep, both directions.
/// Forward: LicenseManager's acquired events fire on every native grant — career manager purchase,
/// game logic, anything — and each is mirrored to the server as a charge-free external grant (the
/// money side rides <see cref="WalletMirror"/>'s register-fee capture, so nothing bills twice).
/// A join-time sweep mirrors licenses the native save already held, for the same reason JobCapture
/// sweeps pre-session jobs: the host's world IS the world, progression included. Reverse: server
/// license commits (panel shop, starting-license floor, resumed careers) are applied back into the
/// native LicenseManager, so DV's own validator and UI can never disagree with the board about
/// what the local player may claim. Host-mode only — applying grants natively on a JOINED client
/// would write into that player's own single-player save (M3.5b territory).
/// </summary>
public sealed class LicenseSync : IDisposable
{
    private readonly NetClient _client;
    private readonly Action<string> _log;
    private bool _applying; // reentry guard: native applies we initiate must not echo back as captures

    public LicenseSync(NetClient client, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _log = log;

        LicenseManager? manager = LicenseManager.Instance;
        if (manager == null)
        {
            _log("[career] license sync unavailable — no LicenseManager in this world");
            return;
        }
        manager.JobLicenseAcquired += OnNativeJobLicense;
        manager.LicenseAcquired += OnNativeGeneralLicense;

        _client.Career.LicenseGranted += ApplyNative;
        _client.Career.CareerStateReceived += OnCareerState;
        _client.Accepted += OnAccepted;
        if (_client.Joined) SweepNativeLicenses();
    }

    private void OnAccepted(int _) => SweepNativeLicenses();

    /// <summary>Server career state landed (join burst or rejoin): make the native set agree with
    /// everything the career already holds, then mirror up anything native-only.</summary>
    private void OnCareerState()
    {
        foreach (string id in _client.Career.Licenses) ApplyNative(id);
    }

    /// <summary>Mirror every natively-held license into the career (a mature save's progression
    /// counts, exactly like its jobs and consists do). Server-side the grant is idempotent.</summary>
    private void SweepNativeLicenses()
    {
        LicenseManager? manager = LicenseManager.Instance;
        if (manager == null) return;
        int offered = 0;
        foreach (JobLicenseType_v2 lic in manager.GetAcquiredJobLicenses())
        {
            if (lic is null || _client.Career.Licenses.Contains(lic.id)) continue;
            _client.Career.GrantExternalLicense(lic.id);
            offered++;
        }
        foreach (GeneralLicenseType_v2 lic in manager.GetGeneralAcquiredLicenses())
        {
            if (lic is null || _client.Career.Licenses.Contains(lic.id)) continue;
            _client.Career.GrantExternalLicense(lic.id);
            offered++;
        }
        if (offered > 0) _log($"[career] mirrored {offered} natively-held license(s) to the career");
    }

    private void OnNativeJobLicense(JobLicenseType_v2 license)
    {
        if (_applying || license is null || _client.Career.Licenses.Contains(license.id)) return;
        _log($"[career] native license grant captured: {license.id}");
        _client.Career.GrantExternalLicense(license.id);
    }

    private void OnNativeGeneralLicense(GeneralLicenseType_v2 license)
    {
        if (_applying || license is null || _client.Career.Licenses.Contains(license.id)) return;
        _log($"[career] native license grant captured: {license.id}");
        _client.Career.GrantExternalLicense(license.id);
    }

    /// <summary>Apply a server-granted license to the native LicenseManager (no-op when already
    /// held). Ids the game doesn't know — e.g. synthetic dedicated-server licenses — are skipped.</summary>
    private void ApplyNative(string licenseId)
    {
        LicenseManager? manager = LicenseManager.Instance;
        if (manager == null) return;
        DVObjectModel? types = Globals.G != null ? Globals.G.Types : null;
        if (types == null) return;

        _applying = true;
        try
        {
            if (types.TryGetJobLicense(licenseId, out JobLicenseType_v2 job))
            {
                if (!manager.IsJobLicenseAcquired(job)) manager.AcquireJobLicense(job);
            }
            else if (types.TryGetGeneralLicense(licenseId, out GeneralLicenseType_v2 general))
            {
                if (!manager.IsGeneralLicenseAcquired(general)) manager.AcquireGeneralLicense(general);
            }
        }
        catch (Exception e)
        {
            _log($"[career] native license apply failed for {licenseId}: {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dispose()
    {
        LicenseManager? manager = LicenseManager.Instance;
        if (manager != null)
        {
            manager.JobLicenseAcquired -= OnNativeJobLicense;
            manager.LicenseAcquired -= OnNativeGeneralLicense;
        }
        _client.Career.LicenseGranted -= ApplyNative;
        _client.Career.CareerStateReceived -= OnCareerState;
        _client.Accepted -= OnAccepted;
    }
}
