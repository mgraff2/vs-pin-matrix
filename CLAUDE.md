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

**Before every push and every release, run:**

Not before every commit. Commits inside a working session are cheap and frequent, and gating each
one on a four-minute headless boot matrix stops the session dead for nothing - the code has not
left the machine yet. The push is the line that matters: that is where the artifact becomes
something other people can get. (Asked for in Aug 2026, replacing "after any code change and
before any release or commit".)

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
- **Tab row = the whole row, and the row belongs to the bar.** `LayoutManager.ButtonRowRect()` is
  the bar's rect: full screen width inset by the zone padding, at `ButtonRowIndex * CellH`. Which row
  is *the row the bar was last dropped on* (`ButtonRowIndex` reads its own `ZoneAssignment`); the
  drop is a row choice, not a corner, which is why `TryLandingRect` previews the full band for it.
  An intermediate 1.6.0 design split the row into equal slots shared with everything else dropped
  there — the bar shrank as soon as it had company, and the slot edges fell at fractions of the
  screen rather than on grid lines, so it no longer lined up with the lattice it had just been
  dropped onto. It was rejected on sight. Anything else dropped on the tab row is **refused**: the
  bar is a full-width strip at DrawOrder 0.97 against the map dialog's 0.11, so a window landing under it
  is not merely overlapped but unclickable, recoverable only by a layout reset. The refusal lives
  in `WhyCannotSnap` — the single rule both `Snap` and `HudLayoutOverlay` read, so the overlay
  tints the hovered cell red from it and prints the same string on the drop, and a cell can never
  look droppable and then refuse. The bar arriving on an already-occupied row evicts what is there
  (`Snap` → `LastSnapEvicted`) rather than parking it underneath. `.pinmatrix unsnap [name]`
  releases one window by substring match; `resetlayout` stays the clear-everything hammer.
  `SetStretchWidth` takes the *inner* width — the window adds 8px of padding of its own, so callers pass `rect.W - 8` or the bar lands short.
- **Moving one of our own windows means pinning vanilla's store FIRST — every time, no exceptions.**
  `GuiElementDialogTitleBar` restores its window to `GetDialogPosition` on every compose, and moving
  a window recomposes it. So `SetPosition` alone loses: the store still holds wherever the drag left
  it, and the title bar puts the window back there on the very compose our move triggered. The
  window only obeys once the title bar goes away — which is what "it snaps into place after I hide
  zones" means, and it was reported twice, for the tab row and then again for Stacked / Parallel /
  Floating, because the fix was applied in one branch and not the others.
  `HudPinMatrixButtonWindow.PinPositionForTitleBar(x, y)` writes our position in **as the seed**, so
  the two agree and `TryGetPlayerPosition` still correctly does not read it as a placement. Call it
  immediately before every `SetPosition` on one of our windows: the snapped-zone branch, the tab-row
  strip, and the floating-mode dodge in `SpreadFloatingButtons` all need it. Skip the move entirely
  while the left mouse button is down, or the per-tick re-place erases the drag before
  `HudLayoutOverlay.TrackDrag` can see it and no drop is ever detected.
- **A snapped window is a fixed point for the floating spreader too.** Pinning the store makes the
  stored position ours rather than the player's, so `PlayerPlaced` goes false — `SpreadFloatingButtons`
  must therefore also treat "has a zone assignment" as fixed, or it shuffles a window straight out of
  the cell it was just dropped on.
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

## Pin sets (1.6.0) — saved filters as map buttons

`PinSets.cs` (`PinSet` + `PinSetService`), `GuiDialogPinMatrix.PinSets.cs` (three screens),
`HudPinSetPanel.cs` (the map-side panel), `IconGlyphComponent.cs`. Six things are load-bearing:

- **A set stores the question, never the answer.** Criteria only — title substring, icons, colours,
  pinned — re-evaluated on every press. That is the whole feature as asked for: pins marked after the
  set was made are covered by it. A stored key list would silently stop covering new pins with nothing
  on screen to explain why. The radius and visible/hidden filters are deliberately excluded: the
  radius means something different depending on where the player stands, and the hidden filter is
  what the button *controls*.
- **Sets surface as a panel, NOT as buttons and NOT as map-layer tabs.** Both alternatives were
  built or investigated and rejected on measurements, so do not re-litigate them without new ones:
  - *Map tab strip* (`GuiElementVerticalTabs`): one tab per distinct `MapLayer.LayerGroupCode`,
    25px tab + 5px spacing in a strip fixed at 545px ≈ 18 tabs, composed onto a surface sized to
    those bounds — tab nineteen is **silently clipped**, not scrolled, and there is no scrollbar.
    Labels go through `DrawTextLineAt`: plain Cairo text, no VTML, so no icons and no counts.
    Vanilla contributes ~4 and other map mods more, so sets would start vanishing unexplained.
  - *`MapLayer.ComposeDialogExtras`* is the sanctioned way to hang a panel off the map dialog, but
    reaching it means **being** a MapLayer, and every MapLayer adds a tab to that same strip.
  - So `HudPinSetPanel` is an ordinary window of ours, opened and closed with the full map by the
    existing watcher. The full map is `CenterMiddle` nudged left, which is the only reason the right
    of the screen is free for it; it measures the map's own composer rather than assuming a width —
    **on every watcher tick**, so it follows that edge when GUI scale or window size moves it.
  - **The panel is a drawer, and shut is the default.** `HandleW`/`HandleH` is a 16px pull that is
    the assembly's leftmost column in both states; the body composes to its right at `bodyX`, so
    opening grows rightward instead of sliding the pull out from under the click that opened it.
    `config.PinSetPanelExpanded` persists the choice. The pull re-colours from `handleFiltering` —
    **snapshotted at compose**, like the rows and for the same reason: `DrawHandle` is a dynamic
    custom draw and walking the set list mid-paint would let the strip and its tooltip disagree.
    That signal is not decoration: shut, it is the only thing on screen saying a filter is on, and
    pins missing from the map with nothing explaining why is the bug this whole feature exists to
    prevent.
  - **The panel is not a layout-grid citizen, on purpose.** It briefly grew a "Move" title bar in
    layout mode like the button windows. That was wrong twice over: it re-anchors itself to the map
    every tick, so a zone could never hold it (the handle only ever fought the anchor), and the space
    it occupies is free *because* the map does not use it. It has no title bar, no stored position,
    and `HudLayoutOverlay.TrackDrag` deliberately still admits only `HudPinMatrixButtonWindow` as a
    drag candidate among our HUDs. A stale assignment from the development build that did allow it
    is dropped once, at panel construction.
- **Toggle rule: any visible → "Hide".** "Show" only once every match is off. Majority-wins was
  considered and rejected — it flips the label under the player mid-way through hiding by hand.
- **Counts are cached per watcher tick, not per label.** `PinSetService.Recount()` walks the waypoint
  list once for every set; `ButtonLabel`/`ButtonTooltip`/`ButtonTint` read the cache. It is called from
  `OnMapWatchTick` (only while the map is open) and after every toggle. Do not make a label recompute
  its own count — that is one pass per set per label per tick.
- **Panel rows are drawn, not composed.** Nothing in the GUI toolkit paints a tintable waypoint icon
  (`AddIconButton` gives no control over colour), so a row is `AddDynamicCustomDraw` + `AddHoverText`
  and its clicks are routed by `HudPinSetPanel.OnMouseDown`, ordered exactly like the editor's:
  composers first (so the pager buttons and title bar keep their claim), then hit-zones, then the
  catch-all. The `rows` snapshot must be filled **before** the compose loop — the custom draw paints
  during compose and would otherwise use the previous state — and `rowBounds` must be cleared when
  the panel empties, or a click on bare screen toggles a set that is no longer listed.

Both VTML tags (`pmswatch`, `pmicon`) are re-registered on **every dialog open**, not once at
startup: `ClientMain.Dispose()` clears `VtmlUtil.TagConverters` on leaving a world. `WaypointIconAssets`
exists because a set's icon button can be the first thing in a session to paint a waypoint icon —
the editor need never have been opened — and vanilla only loads those SVGs lazily on first map open.

## Herty cups (1.7.0) — the first named cross-mod behaviour, and still no `IsModEnabled`

`HertyCupMarkers.cs`. Marks a cup when the player **places one or collects from one**. Three things
are load-bearing:

- **Ownership is sidestepped, not solved.** A client is never told who changed a block:
  `IClientEventAPI.BlockChanged` is `(BlockPos pos, Block oldBlock)` and nothing more, and
  `BEHertyCup.ToTreeAttributes` syncs spiles/pot size/rates with **no placer UID**. So "scan nearby
  chunks and mark my cups" is not implementable from here at any effort — on a server it marks
  everyone's. Both triggers are therefore *local player interactions*, which makes the marker yours
  by construction with no ownership data anywhere. If Herty Cups ever does record a placer, this
  does not need rewriting; it would only gain the ability to mark cups you have never touched.
- **The two triggers are one hook, and the enum value is a trap.** `IInputAPI.InWorldAction` — but
  the action to test is **`EnumEntityAction.InWorldRightMouseDown`, not `RightMouseDown`**. Decompiled
  1.22.7: `SystemMouseInWorldInteractions` raises `InWorldRightMouseDown` for a right-click on a
  block, while `EntityControls` routes its own actions (the plain `RightMouseDown` among them)
  through the same event because `SystemPlayerControl` sets `Controls.OnAction = TriggerInWorldAction`.
  Testing only the plain value — the obvious guess, and what this was first written as — means the
  whole feature silently never fires. Both are accepted; a duplicate event is harmless because the
  mark is deduped. Then: if the aimed-at block is a cup that is a collection, marked at
  once; otherwise the aim is remembered and `BlockChanged` confirms whether a cup actually appeared
  within 1.5s and 2 blocks. Confirming on the *block* rather than on the held item means this never
  has to know what a cup is held as. `handled` is never written — swallowing that right-click would
  stop the player collecting the resin they clicked for.
- **Named, but still no mod check.** There is deliberately no `api.ModLoader.IsModEnabled("hertycups")`
  branch: the condition that matters is whether a cup block exists, so `IsCup` tests the block code
  (`hertycups:hertycup*`) and everything falls through on its first comparison when the mod is absent.
  The count-marker rule from the compat invariants still cannot apply for the same reason the map
  layer's cannot — `compat-test.ps1` reads *server* logs and this mod never loads server-side. The
  tapped tree's wood comes out of block codes too (`Variant["side"]` gives the log direction,
  the log's own `Variant["wood"]` names the tree), so no reference to the Herty Cups assembly exists
  and none should be added.

## Translocator marching ants (1.7.0) — why the clip is not optional

`TranslocatorPathComponent.DrawAnts`. A recent path is drawn as alternating bands crawling from the
origin pad towards the destination. The band walk **must** run on the segment clipped to the map
frame, not on the whole segment: `BothBeyond` only discards lines wholly off one edge, so a zoomed-in
hop crossing the view is tens of thousands of pixels long and would cost thousands of draw calls a
frame. `MaxAntBands` is a backstop, not the bound — if it ever becomes the bound the line stops dead
mid-screen. Band boundaries are measured from the segment's own origin so panning moves the bands
with the line instead of sliding them along it, and the phase is accumulated from the render `dt`
(one advance per frame, shared by every path) rather than read off a clock. Only *recent* paths are
banded; every other line is still one quad.

## The layout tools window (1.7.0) — temporary tools are never grid citizens

`PmButton.Options` and everything after it are **layout-mode tools** (`PmButtons.LayoutOnly`), and
they live in one floating `HudPinMatrixButtonWindow` named `pinmatrix-layouttools`, not in the
permanent bar. Three facts:

- **It reuses the button-window class rather than being a new one.** Everything it needs — title-bar
  seeding, re-placing from `posX/posY` every compose, the empty-window crash guard — already exists
  there. The only new surface is a `Snappable` flag.
- **`Snappable = false` is what keeps it out of the grid**, and `HudLayoutOverlay.TrackDrag` is the
  one place that reads it. The tools appear *because* you are arranging windows, so they must never
  become another window to arrange, and must not sit in a cell you wanted. It is still in
  `OwnDialogs` (not a HUD obstacle) and `SelfPlaced` (`ApplyAll` leaves it alone), and
  `PositionButtons` short-circuits on `!Snappable` — no mode, no zone, no cell, only the position it
  was dragged to or its default.
- **It appears and vanishes with the zones for free.** `VisibleButtons` drops every `LayoutOnly`
  button when the zones are hidden, an empty window composes to nothing, and the existing
  `wanted = !b.IsEmpty` open/close rule does the rest. There is no separate lifecycle to keep in step.

Why it was moved out of the bar at all: Layout Options *inside* the permanent stack meant toggling
the zones changed the bar's button count, which resized it under the cursor — and in tab-row mode
re-stretched it across the row mid-arrangement. The bar is now `{Editor, Zones}` and its size no
longer depends on what the zones are doing. A zone remembered for the old floating-mode
`pinmatrix-btn-options` window is dropped once at construction, or it would name a window that no
longer exists forever.

## GUI scale (1.7.0) — setting it is genuinely just setting the number

`LayoutManager.SetGuiScale` / `AutoFitScale` / `FittedScale`, the slider on the Layout screen, the
switch on Map options. Decompiled 1.22.7, and worth not re-deriving:

- **`capi.Settings` *is* `ClientSettings.Inst`** (`ClientCoreAPI.Settings => ClientSettings.Inst`), so
  `capi.Settings.Float["guiScale"] = v` fires both watchers the game registers on that key:
  `ScreenManager`'s sets `RuntimeEnv.GUIScale = val`, `ClientMain`'s calls
  `MarkAllDialogsForRecompose()`. Vanilla's own `onGuiScaleChanged` does nothing else beyond
  updating its own button widths. There is no apply step to find.
- **Vanilla's range is 4-16 in eighths, or 4-24 when the screen is wider than 3000px**
  (`GuiCompositeSettings`). `MaxScaleStep()` matches it exactly so our slider can never reach a
  value the game's own would refuse to show.
- **`GuiElementSlider.TriggerOnlyOnMouseUp` is `internal`.** Vanilla uses it on this exact slider
  because every change recomposes every open dialog and a drag fires once per step. Mods cannot call
  it, and reflection is the thing this project has refused elsewhere — so `OnGuiScaleChanged`
  debounces instead: each change bumps a sequence number and schedules a 250ms callback, and only the
  last one applies. The readout is updated immediately so the number does not lag the handle.
- **Proportional scaling is what makes windows land where they were, and it is not a coincidence.**
  Every stored dialog position is in *unscaled* units (pixels / GUIScale). Scale proportionally with
  the resolution and the unscaled screen size is **invariant** — 2560x1440 at 1.0, 1280x720 at 0.5
  and 1920x1080 at 0.75 are all a 2560x1440 unscaled space. So nothing needs repositioning: in the
  coordinate space those positions are written in, nothing moved. Snapped windows are immune anyway
  (cells, re-derived). Three limits: an aspect change keeps the smaller ratio so one axis gains slack
  rather than overflowing; eighth-steps quantize the intermediate answer; and a ratio outside
  vanilla's range clamps, which is the one case where the unscaled space really does shrink and
  windows can fall off — hence the second chat line pointing at `.pinmatrix rescue`. Note that rescue
  *overwrites* stored positions, so it is a repair, not routine housekeeping: using it at B means B's
  positions are what comes back to A.
- **A scale set outside this mod becomes the reference, via `ISettings.AddWatcher<float>("guiScale")`.**
  Without the watcher the honouring is only half true: `AutoFitScale` already ignores scale changes
  (it answers resolution changes only), so a manual change survives — until the next resolution change
  recomputes from the stale reference and silently discards it. Three things about the watcher:
  `ClientSettings` is a **process-wide singleton** and `ISettings` has **no RemoveWatcher**, so it is
  registered exactly once (`scaleWatcherRegistered`) and re-pointed at the current manager
  (`scaleWatchTarget`) — per-world registration would leave a dead watcher per world joined, each
  holding a stale config. It must ignore our own writes (`settingOurOwnScale`), or every automatic fit
  would re-capture itself as the reference, which is the compounding-drift bug arrived at from the
  other side. And it must capture **the value the watcher was handed**, not `RuntimeEnv.GUIScale` —
  ScreenManager's watcher is what assigns that field and nothing promises it runs first.
- **Auto-fit derives from a fixed reference, never from the current scale.**
  `LayoutBaseScale` + `LayoutBaseScreenW/H` is "the scale you chose, and the screen you chose it on".
  Scaling the *current* value by a ratio compounds: bounce between two machines a few times and the
  size drifts away from anything anyone picked. From a fixed base, 2560x1440 at 1.0 gives exactly 0.5
  at 1280x720 and exactly 1.0 on return. The base is re-captured **only** when the player sets the
  scale themselves (slider or `.pinmatrix guiscale`) — auto-fit must never re-capture, or it becomes
  the compounding version again.
- **It answers a resolution change, not a scale change.** `Tick` snapshots whether the frame size
  moved *before* `RebuildGrid` overwrites `lastFrameW/H`, and only then calls `AutoFitScale`. Reacting
  to a scale change would fight the player's own slider — and since setting the scale is itself a
  scale change, it would never settle.
- **Not gated on `LayoutEnabled`**, unlike the zones button, the `Z` shortcut and the overlay. It
  lives on Map options and answers a problem that has nothing to do with snap zones. Its own switch
  is the only gate.

`RescueOffscreen` is the companion: assignments are clamped when applied, but a window nobody snapped
keeps whatever absolute position its own mod stored, from whatever screen it was stored on. It clamps
every open `EnumDialogType.Dialog` composer back inside the screen (never past a zero origin, so a
window taller than the screen still has a reachable title bar), writes through `SetDialogPosition` and
flushes immediately. HUDs are skipped, as everywhere else.

## A colour is never shown as hex alone

Every surface that names a colour paints it too, and there are exactly three ways to do that
depending on what the surface is:

- **VTML** (`<pmswatch color="#rrggbb"/>`, `ColorSwatchComponent`) for anything that takes markup —
  dropdown labels (`ColorFilterLabels`), `AddRichtext` (the Pin sets list's `CriteriaSummaryVtml`,
  the recolour confirmation title). `AddStaticText` does **not** parse VTML, so switching a label to
  swatches means switching the element too. Player text mixed into VTML must go through
  `WpCommands.VtmlEscape` — a pin title with an angle bracket would otherwise eat the rest of the label.
- **`AddDynamicCustomDraw` + `PaintChip`/`PaintSwatch`** beside every hex text input. Map options set
  this pattern (trader, translocator, herty cup); the editor's two "...or hex" boxes follow it. A
  chip next to a hex box must show what the screen *would actually apply* — hex when complete, else
  the palette selection — because a chip that disagreed with the Save button is worse than none.
- **Cairo, painted directly** inside custom-drawn lists: the table's colour column, and
  `PendingBulk.LineSwatches` in the confirmation list. These are not text at all, so truncation has
  to account for the chip's width or long rows overflow by exactly that much.

`AddColorListPicker` and `AddIconListPicker` are vanilla's and already show the real thing — the
palette they take is `svc.PaletteColors()` (the *full* vanilla palette, deliberately: you pick a new
colour from everything available, which is a different question from filtering).

The colour **filter** is the opposite: it lists only colours some waypoint actually uses
(`RebuildColorFilterValues` over `allRows`, not the filtered rows), hue-sorted via `HueKey` so
near-identical colours sit together, with a live count that tracks the *other* filters. Rebuilt
whenever the waypoint list changes, dropping any filtered colour whose last pin has gone.

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
- **A grid of choices is a dropdown that has not been written yet.** The matrix screen's icon filter
  was a 24-per-row clickable strip of every icon in the game, costing two rows to answer a question
  three of its cells were ever asked; 1.6.0 replaced it with a multi-select dropdown carrying glyphs
  and live counts, beside the colour one it now matches. The same reasoning moved six rarely-pressed
  buttons behind **Tools...**. What must NOT move into a cabinet is a *signal* — the duplicate and
  same-spot counts stayed on the main screen as a status line, because a fact you cannot see is a
  fact you never act on.
- Keep custom-draw elements narrow enough not to overlap neighbouring interactive elements — the
  trader swatches are one strip per column, not one wide surface spanning both.

## The one lang file

`assets/pinmatrix/lang/en.json` holds a single key, and the reason is worth keeping: the world map
labels each tab in its left-hand strip with `Lang.Get("maplayer-" + MapLayer.LayerGroupCode)`, and
`Lang.Get` returns the key unchanged when it cannot resolve it. With no lang file, our
`TranslocatorPathLayer` tab rendered as the literal string `maplayer-pinmatrixtl`. **Any future map
layer needs its key added here**, or it will do the same.

This is the mod's only asset, and the cross-mod recipe warning above still stands for everything
else. `tools/compat-test.ps1` packs `PinMatrix/assets` if it exists — the whole folder, so a stray
file in it ships.

## Why the map's own tab strip cannot hold the pin sets

Investigated in Aug 2026 against decompiled 1.22.7 and rejected, so do not re-litigate it without
new measurements. `GuiElementVerticalTabs`:

- **No dynamic labels.** Every tab's text is baked into one `baseTexture` plus a hover texture per
  tab at `ComposeTextElements`. `GuiTab.Name` has no setter that redraws, and the element exposes no
  recompose entry point — vanilla's own path for a changed strip is `WorldMapManager.ToggleMap()`
  twice, i.e. close and reopen the map. So live counts are out.
- **One width for all tabs**, computed as the max text width over the whole array, so a count going
  9 to 10 would rewidth the entire strip.
- **No VTML** — labels go through `DrawTextLineAt`, plain Cairo text. No icons, no per-tab colour.
- **No paging, no wrapping, no scrollbar.** Layout is a single vertical run and the map fixes the
  strip's bounds at `ElementBounds.Fixed(-200, 45, 200, 545)`; the element composes onto a surface
  of exactly those bounds, so at a 30px pitch tab nineteen is silently clipped. Worse,
  `OnMouseDownOnElement` iterates every tab regardless, so a clipped tab is invisible but still
  clickable.
- **A tab is a `MapLayer.LayerGroupCode`, not a set.** `WorldMapManager.getTabsOrdered()` collects
  distinct group codes, so being a map layer buys exactly one tab however many sets exist.

What the strip *does* have, if it is ever useful: `AddVerticalToggleTabs` sets `ToggleTabs = true`,
so several tabs can be lit at once, and `GuiTab.PaddingTop` adds a gap above one.

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
upload is manual. **Run `.\tools\compat-test.ps1` before every push, and both it and
`.\tools\version-sweep.ps1` before every release.**
