# Regression guard for inter-mod compatibility. Boots a headless Vintage Story server for
# each mod combination and fails on any [Error]/[Warning] in the server log or any missing/
# unexpected marker.
#
# Pin Matrix is client-side only, so what a dedicated-server boot proves is narrower than
# for a universal mod — but still real: the server unpacks the zip, loads PinMatrix.dll and
# instantiates its ModSystems (visible in server-debug.log) before ShouldLoad gates them
# off. That catches a broken zip, a bad modinfo/dependency declaration, an assembly that no
# longer loads against the target game version, and any accidental loss of the client-only
# gate. What it can NOT catch is client-side GUI interaction (map button placement, chat
# link parsing, waypoint layer access) — that stays on the manual checklist in README.md.
#
# Invariants enforced per combo:
#   - server reaches "Dedicated Server now running"
#   - zero [Error]/[Warning] lines in server-main.log
#   - pinmatrix and every expected companion modid appear in the "Mods, sorted by
#     dependency:" line, and the "Found N mods (0 disabled)" count is exact
#   - server-debug.log shows our assembly loaded and mod systems instantiated
#   - total server-side silence: exactly ONE pinmatrix mention in server-main.log (the
#     dependency-sort line). A second mention means the mod started logging/running on the
#     server — e.g. the Client side gate was lost — and fails the combo.
#
#   .\tools\compat-test.ps1              -> builds the zip, runs the full matrix
#   .\tools\compat-test.ps1 -SkipBuild   -> reuse the already-packaged zip
#   .\tools\compat-test.ps1 -ServerExe <path>\VintagestoryServer.exe
#                                        -> test against a different game version, e.g. an
#                                           extracted per-version dedicated server package
#                                           (https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_<ver>.zip)
#
# Companion mod zips are cached in tools\compat-cache\ (gitignored): first found in the live
# Mods folder, otherwise downloaded from the mod DB API (latest release for that mod).
# Delete the cache to re-source (e.g. after updating your live mods).
param(
    [switch]$SkipBuild,
    # Build and pack the zip, then stop. The boot matrix now runs before a push rather than before
    # every commit, so this is how you get a fresh dist zip to drop into a live Mods folder while
    # iterating - packing is a few seconds, the matrix is several minutes.
    [switch]$PackOnly,
    [string]$ServerExe = "$env:APPDATA\Vintagestory\VintagestoryServer.exe",
    [int]$BootTimeoutSec = 180
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$cache = "$PSScriptRoot\compat-cache"
New-Item -ItemType Directory -Force $cache | Out-Null

$version = (Get-Content "$root\PinMatrix\modinfo.json" -Raw | ConvertFrom-Json).version
$ourZip = "$root\dist\pinmatrix_$version.zip"

if (-not $SkipBuild) {
    # System dotnet is SDK 9 and refuses the net10.0 game references; prefer the user-scoped SDK.
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    & $dotnet build "$root\PinMatrix\PinMatrix.csproj" -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $staging = "$env:TEMP\pinmatrix-pack"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item "$root\PinMatrix\modinfo.json" $staging
    Copy-Item "$root\PinMatrix\bin\Release\PinMatrix.dll" $staging
    # assets\ is lang files only, and only because the world map labels our map-layer tab with
    # Lang.Get("maplayer-" + LayerGroupCode) and shows the raw key when it cannot resolve one.
    # Anything beyond lang belongs under the cross-mod recipe warning in CLAUDE.md before it ships.
    if (Test-Path "$root\PinMatrix\assets") { Copy-Item -Recurse "$root\PinMatrix\assets" $staging }
    New-Item -ItemType Directory -Force "$root\dist" | Out-Null
    Compress-Archive -Path "$staging\*" -DestinationPath $ourZip -Force
}
if (-not (Test-Path $ourZip)) { throw "Mod zip not found: $ourZip" }

if ($PackOnly) {
    Write-Host "PACKED $ourZip ($((Get-Item $ourZip).Length) bytes)"
    exit 0
}

# Checked here, not at the top: packing a zip does not need a server, and -PackOnly should
# work on a machine that has never had one installed.
if (-not (Test-Path $ServerExe)) { throw "Server exe not found: $ServerExe" }

# Fetch a companion mod zip: cache -> live Mods folder -> mod DB API (latest release)
function Get-CompatMod([string]$modid, [string]$filePattern) {
    $cached = Get-ChildItem $cache -Filter $filePattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cached) { return $cached.FullName }

    $live = Get-ChildItem "$env:APPDATA\VintagestoryData\Mods" -Filter $filePattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($live) { Copy-Item $live.FullName $cache; return "$cache\$($live.Name)" }

    Write-Host "  downloading $modid from mod DB..."
    $info = Invoke-RestMethod "https://mods.vintagestory.at/api/mod/$modid"
    $release = $info.mod.releases | Select-Object -First 1
    $dest = "$cache\$($release.filename ?? "$modid.zip")"
    if (-not $dest.EndsWith(".zip")) { $dest = "$cache\$modid-$($release.modversion).zip" }
    Invoke-WebRequest ($release.mainfile -replace ' ', '%20') -OutFile $dest
    return $dest
}

# Companion set derived from Pin Matrix's actual interaction surface (map dialogs, waypoint
# layer, HUD corners, chat) and from mods that caused real friend-reported issues:
#   waypointer        - other waypoint-manipulating mod, same WaypointMapLayer
#   translocatorpaths - auto-adds waypoints; shares the map/waypoint layer
#   prospecttogether  - universal mod; its map panels are what PositionMapButton dodges
#   boatautopilot     - live HUD readout; the reason the map button freezes after settling
#   statushudcont     - HUD elements occupying screen corners
#   tallybook         - another HUD-corner occupant; a named target of the window-layout
#                       grid, whose HUD rects decide which snap cells get disabled
Write-Host "Collecting companion mods..."
$mods = [ordered]@{
    waypointer        = Get-CompatMod "waypointer"        "Waypointer-*.zip"
    translocatorpaths = Get-CompatMod "translocatorpaths" "TranslocatorPaths*.zip"
    prospecttogether  = Get-CompatMod "prospecttogether"  "ProspectTogether-*.zip"
    boatautopilot     = Get-CompatMod "boatautopilot"     "boatautopilot_*.zip"
    statushudcont     = Get-CompatMod "statushudcont"     "statushudcont_*.zip"
    tallybook         = Get-CompatMod "tallybook"         "tallybook_*.zip"
}
$mods.GetEnumerator() | ForEach-Object { Write-Host "  $($_.Key): $(Split-Path $_.Value -Leaf)" }

# combos: solo, +each companion, all together. 'expect' = companion modids that must show
# up in the dependency-sort line alongside pinmatrix.
$combos = @(
    @{ name = "solo"; expect = @() }
)
foreach ($id in $mods.Keys) { $combos += @{ name = $id; expect = @($id) } }
$combos += @{ name = "all"; expect = @($mods.Keys) }

$results = @()
foreach ($combo in $combos) {
    $name = $combo.name
    Write-Host "== combo '$name' ..." -NoNewline
    # no "pinmatrix" in the dir name: the server logs the Mods search path into
    # server-main.log, which would trip the exactly-one-mention silence check below
    $dp = "$env:TEMP\pmx-compat-$name"
    if (Test-Path $dp) { Remove-Item -Recurse -Force $dp }
    New-Item -ItemType Directory -Force "$dp\Mods" | Out-Null
    Copy-Item $ourZip "$dp\Mods"
    foreach ($id in $combo.expect) { Copy-Item $mods[$id] "$dp\Mods" }

    $proc = Start-Process $ServerExe -ArgumentList "--dataPath", $dp -PassThru -WindowStyle Hidden
    $log = "$dp\Logs\server-main.log"
    $debugLog = "$dp\Logs\server-debug.log"
    $booted = $false
    $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep 2
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern "Dedicated Server now running" -Quiet)) { $booted = $true; break }
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep 1   # let file handles close before we read/delete

    $problems = @()
    if (-not $booted) { $problems += "server did not reach 'Dedicated Server now running' within ${BootTimeoutSec}s" }
    if (Test-Path $log) {
        $noise = Select-String -Path $log -Pattern "\[Error\]|\[Warning\]" | ForEach-Object Line
        if ($noise) { $problems += $noise }

        # base game contributes 3 mods (game, creative, survival); + ours + companions
        $expectedCount = 4 + $combo.expect.Count
        if (-not (Select-String -Path $log -SimpleMatch "Found $expectedCount mods (0 disabled)" -Quiet)) {
            $found = (Select-String -Path $log -Pattern "Found \d+ mods" | Select-Object -First 1).Line
            $problems += "expected 'Found $expectedCount mods (0 disabled)', got: $found"
        }

        $sortLine = (Select-String -Path $log -SimpleMatch "Mods, sorted by dependency:" | Select-Object -First 1).Line
        if (-not $sortLine) { $problems += "no 'Mods, sorted by dependency:' line" }
        foreach ($id in (@("pinmatrix") + $combo.expect)) {
            if ($sortLine -notmatch "[ ,]$id(,|`$| )") { $problems += "modid '$id' missing from load order: $sortLine" }
        }

        # server-side silence: the sort line must be the ONLY pinmatrix mention in the main log
        $mentions = @(Select-String -Path $log -SimpleMatch "pinmatrix")
        if ($mentions.Count -ne 1) {
            $problems += "expected exactly 1 pinmatrix mention in server-main.log, got $($mentions.Count):"
            $problems += ($mentions | ForEach-Object Line)
        }
    }
    if (Test-Path $debugLog) {
        foreach ($marker in @("[pinmatrix] Loaded assembly", "Instantiate mod systems for pinmatrix")) {
            if (-not (Select-String -Path $debugLog -SimpleMatch $marker -Quiet)) { $problems += "missing debug-log marker: $marker" }
        }
    } elseif ($booted) { $problems += "server-debug.log missing" }

    if ($problems.Count -eq 0) {
        Write-Host " PASS"
        Remove-Item -Recurse -Force $dp -ErrorAction SilentlyContinue
    } else {
        Write-Host " FAIL"
        $problems | ForEach-Object { Write-Host "    $_" }
        Write-Host "    (data path kept for inspection: $dp)"
    }
    $results += @{ name = $name; ok = ($problems.Count -eq 0) }
}

Write-Host ""
$failed = @($results | Where-Object { -not $_.ok })
if ($failed.Count -gt 0) {
    Write-Host "COMPAT TEST FAILED: $($failed.name -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "COMPAT TEST PASSED: all $($results.Count) combos boot clean" -ForegroundColor Green
# Explicit success exit: only native commands and `exit` set $LASTEXITCODE, so a programmatic
# caller (like Tallybook's version-sweep.ps1 pattern) would otherwise read a stale code from
# whatever ran earlier and report a fully passing matrix as FAIL. Found the hard way over in
# the Tallybook repo.
exit 0
