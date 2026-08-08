<#
.SYNOPSIS
    Exports metadata-only reference assemblies from a licensed Derail Valley install (05 §4, D26).

.DESCRIPTION
    Strips every managed assembly (method bodies removed, ALL type/member metadata kept - including
    internals and privates, which Krafs.Publicizer needs) using NStrip's default ThrowNull mode, the
    same convention Microsoft uses for official .NET reference assemblies. The output compiles the
    full LocoMP solution but can never run the game.

    The output tree mirrors the install layout (DerailValley_Data/Managed, incl. the UnityModManager
    subfolder with UMM + Harmony), plus a manifest.json recording the game build, tool version, and
    per-file SHA-256. It is pushed to the PRIVATE game-refs repo that CI checks out - reference use
    only, never committed to the public repo, never shipped in any artifact, removed on Altfuture's
    request. Hard rule 2 (as reworded by D26) governs.

    Refresh procedure (fires ~once per game update; the game is frozen on B99.7 until B100):
      1. Let Steam update the game, then run this script.
      2. In the stash repo: commit, tag "b<version>-<buildid>" (e.g. b99.7-20251481), push.
      3. Bump .ci/gamerefs.json "ref" in the public repo to the new tag - build.yml's API-compat
         check then itemizes any Shim breakage.

.PARAMETER DvInstallDir
    The Derail Valley install directory (must be a licensed copy).

.PARAMETER OutDir
    The stash working tree. Defaults to a "dv-gamerefs" directory next to this repo's parent.

.PARAMETER DvVersionLabel
    Human-readable game version for manifest.json (e.g. "B99.7").

.PARAMETER BuildId / DepotManifest
    Steam buildid and depot-588031 manifest id. Auto-read from appmanifest_588030.acf when it is
    reachable; pass explicitly otherwise.
#>
[CmdletBinding()]
param(
    [string]$DvInstallDir = 'C:\Program Files (x86)\Steam\steamapps\common\Derail Valley',
    [string]$OutDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '..\dv-gamerefs'),
    [string]$DvVersionLabel = 'B99.7',
    [string]$BuildId,
    [string]$DepotManifest
)

$ErrorActionPreference = 'Stop'

# --- Pinned stripping tool (never floats; bump deliberately and re-verify the hash) --------------
$NStripVersion = 'v1.4.1'
$NStripUrl     = "https://github.com/BepInEx/NStrip/releases/download/$NStripVersion/NStrip.exe"
$NStripSha256  = '726DC793E36980C0EE095089FD945F74F413785C4C978098CEBD6A8090EEDD80'

$Managed = Join-Path $DvInstallDir 'DerailValley_Data\Managed'
if (-not (Test-Path (Join-Path $Managed 'Assembly-CSharp.dll'))) {
    throw "No Derail Valley Managed folder at '$Managed' - pass -DvInstallDir."
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

# --- Game build identity (best effort from the .acf; explicit params win) ------------------------
$acf = Join-Path (Split-Path (Split-Path $DvInstallDir -Parent) -Parent) 'appmanifest_588030.acf'
if ((-not $BuildId -or -not $DepotManifest) -and (Test-Path $acf)) {
    $raw = [System.IO.File]::ReadAllText($acf)
    if (-not $BuildId) {
        $m = [regex]::Match($raw, '"buildid"\s+"(\d+)"')
        if ($m.Success) { $BuildId = $m.Groups[1].Value }
    }
    if (-not $DepotManifest) {
        $m = [regex]::Match($raw, '"588031"\s*\{[^}]*"manifest"\s+"(\d+)"', 'Singleline')
        if ($m.Success) { $DepotManifest = $m.Groups[1].Value }
    }
}
if (-not $BuildId) { throw 'Could not determine the game buildid - pass -BuildId.' }
if (-not $DepotManifest) { throw 'Could not determine the depot-588031 manifest - pass -DepotManifest.' }

# --- Acquire NStrip (cached; hash-verified every run) --------------------------------------------
$toolDir = Join-Path $env:LOCALAPPDATA 'LocoMP\refs-export'
$nstrip  = Join-Path $toolDir "NStrip-$NStripVersion.exe"
if (-not (Test-Path $nstrip)) {
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    Write-Host "Downloading NStrip $NStripVersion..."
    Invoke-WebRequest -Uri $NStripUrl -OutFile $nstrip
}
$hash = (Get-FileHash $nstrip -Algorithm SHA256).Hash
if ($hash -ne $NStripSha256) {
    Remove-Item $nstrip -Force
    throw "NStrip hash mismatch (got $hash, pinned $NStripSha256) - refusing to run it."
}

# --- Strip -----------------------------------------------------------------------------------------
$outManaged = Join-Path $OutDir 'DerailValley_Data\Managed'
if (Test-Path $outManaged) { Remove-Item $outManaged -Recurse -Force }
New-Item -ItemType Directory -Force $outManaged, (Join-Path $outManaged 'UnityModManager') | Out-Null

Write-Host "Stripping $Managed ..."
& $nstrip $Managed $outManaged | Out-Null
if ($LASTEXITCODE -ne 0) { throw "NStrip failed on the Managed folder (exit $LASTEXITCODE)." }
& $nstrip (Join-Path $Managed 'UnityModManager') (Join-Path $outManaged 'UnityModManager') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "NStrip failed on the UnityModManager folder (exit $LASTEXITCODE)." }

$srcCount = (Get-ChildItem "$Managed\*.dll").Count
$outFiles = Get-ChildItem "$outManaged\*.dll", "$outManaged\UnityModManager\*.dll"
if ((Get-ChildItem "$outManaged\*.dll").Count -ne $srcCount) {
    throw "Stripped count mismatch: $srcCount source DLLs vs $((Get-ChildItem "$outManaged\*.dll").Count) output."
}

# --- Manifest --------------------------------------------------------------------------------------
$files = foreach ($f in ($outFiles | Sort-Object FullName)) {
    [ordered]@{
        path   = $f.FullName.Substring($OutDir.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes  = $f.Length
    }
}
$manifest = [ordered]@{
    game          = 'Derail Valley'
    appId         = 588030
    depotId       = 588031
    buildId       = [long]$BuildId
    depotManifest = $DepotManifest
    dvVersion     = $DvVersionLabel
    tool          = "NStrip $NStripVersion (ThrowNull strip, no publicize - Publicizer runs at compile time)"
    generatedUtc  = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    fileCount     = @($files).Count
    files         = $files
}
[System.IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'),
    (ConvertTo-Json $manifest -Depth 4),
    [System.Text.UTF8Encoding]::new($false))

$mb = [math]::Round((($outFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("Done: {0} stripped DLLs, {1} MB -> {2}" -f @($files).Count, $mb, $OutDir)
Write-Host ("Suggested stash tag: b{0}-{1}" -f $DvVersionLabel.TrimStart('B').ToLowerInvariant(), $BuildId)
