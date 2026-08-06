# Pin Matrix — working notes for Claude

Client-side-only Vintage Story mod (`"side": "Client"`): spreadsheet-style bulk waypoint
manager. No assets, no recipes, no server code — the zip is `modinfo.json` + `PinMatrix.dll`.

## Build

The system `dotnet` is SDK 9 and refuses the net10.0 game references. Build with the
user-scoped SDK:

```
& "$env:USERPROFILE\.dotnet\dotnet.exe" build PinMatrix\PinMatrix.csproj -c Release
```

Game references resolve from `%APPDATA%\Vintagestory` (override with `-p:VintageStoryPath=...`).

## Compat regression test — run it, always

**After any code change and before any release or commit, run:**

```
.\tools\compat-test.ps1
```

It builds the zip into `dist/`, then boots a headless dedicated server
(`%APPDATA%\Vintagestory\VintagestoryServer.exe --dataPath <temp>`) once per combo — solo,
+each companion mod, all together — and fails on any `[Error]`/`[Warning]`, a wrong mod
count/load order, or a violated marker (details below). `-SkipBuild` reuses the packaged
zip; `-ServerExe <dir>\VintagestoryServer.exe` points at an extracted per-version server
package (`https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_<ver>.zip`) for
game-version coverage. Companion zips are cached in `tools/compat-cache/` (gitignored;
sourced live-Mods-folder-first, else mod DB API) — delete the cache to re-source.

Companion set (derived from this mod's real interaction surface — map dialogs, the
waypoint layer, HUD corners, chat — not copied from other projects): `waypointer`,
`translocatorpaths`, `prospecttogether`, `boatautopilot`, `statushudcont`.

## Compat invariants (what the test pins, and why)

- **Total server-side silence.** Pin Matrix must contribute *exactly one* line to
  `server-main.log` on a dedicated server: its entry in the `Mods, sorted by dependency:`
  line. That holds in **every** combo, companions present or not. A second mention means
  server-side code started running or logging — e.g. someone weakened
  `ShouldLoad(EnumAppSide.Client)` or `"side": "Client"` in modinfo — and the test fails.
- **The DLL must still load server-side.** Even though it never runs there, the server
  unpacks the zip and loads the assembly; `server-debug.log` must show
  `[pinmatrix] Loaded assembly` and `Instantiate mod systems for pinmatrix`. This is what
  catches an assembly that no longer loads against a game version.
- **No conditional compat registration exists today.** All cross-mod behavior is dynamic
  and nameless: map-button placement scans *whatever* dialogs are open (`PositionMapButton`),
  chat share links parse only our own `[Pin Matrix]` line format. There is deliberately no
  `api.ModLoader.IsModEnabled(...)` branch anywhere. **If you ever add one**, also add an
  exact-count `Notification` log line at the registration site (e.g.
  `"[pinmatrix] X detected: N somethings registered"`) and pin it in `compat-test.ps1` as a
  `require` marker for combos with X and a `forbid` marker for combos without X — that way
  an upstream change that silently breaks the integration changes the count and fails the
  test.
- **Cross-mod grid recipes trap (from herty-cups; N/A here today).** This mod has no
  assets folder at all — keep it that way unless there's a strong reason. If assets are
  ever added: cross-mod grid recipes must NOT go in `recipes/grid/` — the vanilla loader
  logs an `[Error]` when an ingredient's mod is missing. Register them from code, gated on
  `api.ModLoader.IsModEnabled(...)`, with a count marker as above.

## What the headless test cannot see

Client visuals and in-world interaction: map-button placement/dodging, the editor GUI,
chat-link clicking, waypoint layer access. The manual checklist for those lives in
README.md ("Compat regression testing" section). Run it before releases that touch GUI
or sharing code.

## Release flow

Stage `dist/pinmatrix_X.Y.Z.zip` into `%APPDATA%\VintagestoryData\Mods\` (remove older
pinmatrix zips) for local/friend testing first. Publish only on explicit go-ahead: dated
CHANGELOG entry, README version refs, commit, tag `vX.Y.Z`, push,
`gh release create vX.Y.Z dist\pinmatrix_X.Y.Z.zip --title "Pin Matrix X.Y.Z"`. ModDB
upload is manual. **Run `.\tools\compat-test.ps1` before every release.**
