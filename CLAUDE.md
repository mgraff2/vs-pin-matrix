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
`translocatorpaths`, `prospecttogether`, `boatautopilot`, `statushudcont`, `tallybook`.

## Game-version sweep — run it before every release

```
.\tools\version-sweep.ps1
```

`modinfo.json` declares `"game": "1.22.0"`, which is a promise to every player on every patch
release — and the DLL is compiled against exactly one game version's references. This keeps
the promise honest: it builds the zip **once**, then runs that same artifact through the whole
compat matrix against a real dedicated server for **1.22.0 through 1.22.7**, each downloaded
from `https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_<ver>.zip` and cached
extracted in `tools/server-cache/` (gitignored, ~300MB per version). One artifact, N servers.
`-Versions 1.22.0,1.22.7` checks just the endpoints while iterating; `-KeepGoing` reports every
version instead of stopping at the first failure.

When a new patch ships, append it to the `-Versions` default. The CDN 404s on versions that
don't exist, which is how you find the current latest. A cache seeded by copying another VS
mod repo's `tools/server-cache/` is fine — verify the per-version file count matches the source
(~9630 files), since the `.extract-complete` stamp copies across and would otherwise vouch for
a truncated copy.

`SETUP` in the summary means "could not test this version", which is a different fact from
`FAIL` ("the mod is broken on this version") — don't conflate them.

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

## Window layout (snap zones) — the load-bearing facts

Added in 1.5.0. `LayoutManager` / `LayoutZones` / `LayoutHudSampler` / `HudLayoutOverlay`.
Four things about it are easy to break and expensive to rediscover:

- **The whole feature rests on `IGuiAPI.SetDialogPosition` / `GetDialogPosition`** — public API,
  persisted per composer `DialogName` in `clientsettings.json`, and exactly what vanilla's own
  movable title bars use. Moving a window live is the same three lines
  `GuiElementDialogTitleBar` runs on drag (Alignment = None, write fixedX/fixedY,
  `CalcWorldBounds`). No Harmony. `SetDialogPosition(name, null)` is vanilla's un-stick and is
  what reset must call — clearing our own record is not enough, because the title bar reads
  `GetDialogPosition` on its first compose and would silently restore our position.
- **"Movable" and "stays where you put it" are the same switch — you cannot have one without the
  other.** Asked for in Aug 2026 ("when zones are hidden, put every window back to Fixed"),
  investigated, and found impossible within this mod's constraints. Decompiled
  `GuiElementDialogTitleBar` (1.22.7): `movable` is a **private field** exposed only as
  `public bool Movable => movable` — no setter. It is set in exactly two places, both inside the
  private `SetUpMovableState(val)`: at first compose (`didInit`) it reads `GetDialogPosition` and
  goes movable if an entry exists, and from the private title-bar list menu. Selecting **Fixed**
  there runs `val == "auto"`, which restores `parentBoundsBefore` — the window's *original*
  alignment, offsets and margins — sets `movable = false` and calls
  `SetDialogPosition(name, null)`. So vanilla's definition of Fixed is literally "forget the stored
  position and go back where you started"; the window jumping home is the feature, not a side
  effect. There is no public path to "fixed but still over there", and reaching one would need
  private-field reflection re-applied every compose (a movable title bar also rewrites its own
  stored position in `RenderInteractiveElements` on first frame) or Harmony — both against the
  rules above. Leave it alone.
- **Our own button windows need their movable state seeded; other mods' do not.**
  `MakeOpenDialogsMovable` skips them twice over (they are `EnumDialogType.HUD`, and they are in
  `OwnDialogs`), so the `AddDialogTitleBar("Move", …)` they grow in layout mode is inert until a
  position exists under their `DialogName` — the same `GetDialogPosition`-on-first-compose rule as
  above. `HudPinMatrixButtonWindow.SyncTitleBarMovable` seeds it on compose and clears it when the
  bar goes, which is safe *only* because we re-place ourselves from `posX/posY` every compose and
  never rely on vanilla restoring us. Two consequences that are easy to get wrong: placement must
  ask `TryGetPlayerPosition`/`PlayerPlaced` rather than `GetDialogPosition != null` (a seed is not a
  placement), and clearing a seed must flush, since the layout manager writes the whole
  `dialogPositions` dictionary the moment the zones are shown.
- **Nothing is ever resized, our own window included.** No resizable-dialog concept exists in the
  engine. Zones move windows and anchor them by their top-left corner; they never change a size.
  An earlier version did rebuild our own table at a different row count to fill its zone
  (`TryFitToZone`) — it is gone, and the docs that still advertised it were corrected before the
  1.5.0 release. Do not reintroduce it without also fixing README/CHANGELOG/ModDB copy.
- **Never move a HUD.** They re-assert their own position every tick; they are obstacles, not
  targets. Blocked cells are sample-and-hold (see the sampler's class comment) — do not
  "simplify" that to a live scan, or cells will flicker as the vanilla location readout resizes
  itself every 250ms while the player walks.
- **No global hotkey, on purpose.** The overlay is toggled by the "Layout Zones (Z)" button stacked
  under "Pin Matrix Editor (P)" in `HudPinMatrixButtonWindow`, and `Z` is a map-screen-only shortcut
  guarded exactly like `P` (map focused, no text field focused). Do not "promote" it to a
  registered hotkey — the user's mod list is large and every global bind is a collision.
- **The whole feature is opt-in** (`LayoutEnabled`, default false, toggled on the editor's Map
  options screen). While off, the Zones button is filtered out of `VisibleButtons()`, `Z` is dead,
  and `SetLayoutPinned` refuses to pin — all three gates on the same flag, so the button, the
  shortcut and the overlay can never tell different stories.
- **The overlay must stay out of the input path.** `ShouldReceiveMouseEvents() => false`, drags
  detected by polling composer positions against an at-press baseline. Do not "improve" this into
  event interception.

Still no `IsModEnabled` branch anywhere — HUD detection is `EnumDialogType.HUD`, persistence is
keyed by `DialogName`. If that ever changes, the count-marker rule above applies.

## Map layer registration (1.5.0) — the one exception to "no registrations"

`PinMatrixModSystem.StartClientSide` calls
`WorldMapManager.RegisterMapLayer<TranslocatorPathLayer>("pinmatrixtl", 1.1)`. Safe timing:
WorldMapManager instantiates registered layers at `LevelFinalize`, well after `StartClientSide`.

The compat invariant above asks for an exact-count `Notification` marker at any registration site,
pinned in `compat-test.ps1`. **That rule cannot apply here, and it is worth knowing why rather than
quietly skipping it:** `compat-test.ps1` reads *server* logs, and this mod never loads server-side
(`ShouldLoad(EnumAppSide.Client)`), so a client-side log line could never appear there. The
registration is also unconditional and nameless — no `IsModEnabled` branch — so there is no
per-companion count that could silently drift. What the headless test still proves is what it
always proved: the assembly loads and the client-only gate holds.

If a *server-side* registration is ever added, the count-marker rule applies in full.

## Settings screens — explanations go in tooltips, not on the screen

Two screens have now been rebuilt for the same reason (Window layout, then Map options): a
paragraph of explanation beside each control buries the two or three settings anyone actually
changes, and the dialog autosizes to its children, so it grows until it overruns the screen at
higher GUI scales. The pattern that works, and that both screens now use:

- One switch and its settings on **one row**; the row's prose lives in `AddHoverText` attached to
  that row's bounds (`EB(...).FlatCopy()`, unique key per tooltip).
- Sections separated by `c.AddInset(EB(4, y, DW - 8, 2), 2)` with a `WhiteSmallishText` header.
- Repeated rows go in **columns** when they fit — the nine trader colours were a third of the
  screen as one stack, and are five rows as two columns (`TraderCols`/`TraderColW`, column-major so
  the names still read alphabetically downwards).
- Labels are sized to their text: `AddStaticText` **wraps** rather than ellipsizing when it is too
  narrow, which is how a label overran the table header in 1.3.1. Budget ~9.5 unscaled px per
  character plus padding.
- Keep custom-draw elements narrow enough not to overlap neighbouring interactive elements — the
  trader swatches are one strip per column, not one wide surface spanning both.

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
upload is manual. **Run `.\tools\compat-test.ps1` and `.\tools\version-sweep.ps1` before
every release.**
