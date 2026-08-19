# Changelog

All notable changes to Pin Matrix are documented here.

## [1.5.0] — 2026-08-19

### Added
- **Map windows layout — a snap grid for the game's dialogs.** Divide the screen into zones — any grid from 1 x 1 up to 20 x 20, split evenly by whatever numbers you pick — turn the overlay on with the new **Layout Zones (Z)** button on the map screen, drag any window by its title bar and drop it into a cell.
Positions are remembered and reapplied when that window opens again. Built for the map screen, where Pin Matrix, the world map, and other mods' panels compete for the same space.
  - **Opt-in: the button does not exist until you ask for it.** Tick **Snap map-screen windows to a grid** on the editor's **Map options** screen and the Layout Zones button appears next to the editor button; off (the default), the map screen shows exactly what it always did and `Z` does nothing. A button for a feature you never use is clutter on every map open.
  - **Cells occupied by a HUD are disabled** and drawn red, so a snapped window never lands on top of your location readout, Tallybook, hotbar or chat. **Do not cover HUDs** is a toggle — untick it and the same cells become available, with the grid geometry unchanged, so nothing already placed moves. A threshold (default 25% of a cell) decides how much of a cell a HUD has to cover before it counts; any-overlap would be far too aggressive, since the hotbar is wide but short and clips a whole row.
  - **Windows are moved, never resized** — Pin Matrix's own included. See the design note below for why the engine leaves no honest way to do otherwise.
  - **Reset layout** on the Map windows layout screen and `.pinmatrix resetlayout` in chat. The chat form exists deliberately: a button inside our own dialog is no use if our own dialog is what ended up somewhere unreachable.
  - `.pinmatrix dialogs` lists every open window with everything the grid decides on — name, type, alignment, rect, stored position. It is the support tool for "this mod's panel will not snap".
  - **A row can be nominated as a button row**, which turns it into a tab strip: everything dropped on that row is spread evenly across the full screen width instead of sitting in the one cell it landed on, and re-spaces itself as items join or leave. The column you drop on decides the order along the strip, not the position.
  - Also `.pinmatrix zones` (show/hide the overlay), `.pinmatrix rescanhuds` (re-measure the obstacles), `.pinmatrix whyblocked <col> <row>` (why one cell is refusing a drop) and `.pinmatrix tlpaths` (list translocator paths: each marker and where its label points).
- **No new keybinding.** The zone overlay is toggled by a **Layout Zones (Z)** button attached directly under **Pin Matrix Editor (P)** on the map screen, and `Z` — like `P` — is a map-screen-only shortcut rather than a registered hotkey. With a large mod list every global bind is a collision waiting to happen, so this mod claims none: both keys exist only while those buttons are on screen, only while the map itself has focus, and never while a text field is focused.

- **Fix same-spot pins** — a second, deliberately separate cleanup for pins that share a *place* and nothing else: the trader carrying a hand-placed pin, a previous auto-marker's leftover, and ours, all pointing at one cart under three different names. **Fix duplicates** keeps its strict meaning (identical in every column) because it is the safe one; this is the one that can delete pins that genuinely differ, so it earns its own button and a preview that names the survivor of every set, not just the condemned.
  - **Ours wins while trader auto-marking is on.** Pin Matrix's own trader marker is kept in preference to the others — it is the one that would simply be re-created on the next scan if it were binned, and its colour is the only one carrying the trade specialisation. With auto-marking off there is nothing to prefer, so the ordinary rule applies: a copy still on the map beats a hidden one, and the earliest beats the rest.
  - **A trader camp survives intact.** Traders really do stand together, but never two of the same kind — so two pins naming *different* specialisations are two different traders and are never collapsed into each other, however close they stand. Pins whose specialisation cannot be read stay eligible, which is exactly what lets the unnamed strays join the trader they point at.
  - **Grouping is not transitive**: every member is within tolerance of the pin that opened the set, never merely of its nearest neighbour, so a line of pins a few blocks apart cannot chain into one "spot" spanning half a village. Tolerance is `SameSpotRadius`, 3 blocks by default — deliberately far tighter than the trader dedupe radius, which only ever suppresses a new marker rather than deleting an existing pin.

- **New Map options screen** (**Map options...** on the manager), holding everything the mod can add to the map: trader markers, translocator paths, and the switch for the Layout Zones button. Three sections, one switch and its settings per row, separated by rules — every explanation is a hover tooltip rather than a paragraph beside the control, the same rule the Map windows layout screen already follows. Trader colours run in two columns instead of nine stacked rows, which alone gave back a third of the screen.

- **Trader auto-markers.** Optional, off by default: walk past a trader and Pin Matrix drops a waypoint on it, coloured by trade specialisation, using the vanilla `trader` icon. Nine specialisations, each colour editable on the **Map options** screen with a live swatch and a per-row reset.
  - What it produces is **ordinary waypoints**, which is the whole point: they sort, filter, recolour, hide, bulk-rename, land in the recycle bin and export like any hand-placed pin. No parallel marker system, and switching the feature off later leaves nothing the matrix cannot manage.
  - Dedupe is positional, not by entity id — ids do not survive a reload, and a trader wanders a few blocks around its cart, so "is there already a trader waypoint about here" is the question that actually matters. Radius is configurable (24 blocks by default).
  - Off by default on principle: writing waypoints onto someone's map uninvited is not a sane default.

- **Translocator paths, for translocators you have actually used.** Optional and off by default. Step through a translocator and both pads get a waypoint, each named after the coordinates of the *other* end, with a straight line drawn between them on the world map. Nothing is ever recorded for a pad you merely walked past.
  - **The waypoint title is the storage.** A hop is fully described by its two waypoints, so there is no save file, no sync folder and no import step — and because waypoints live server-side per player, paths recorded on one machine appear on every other machine you log in from. A player without the mod still sees a waypoint that says in plain text where the pad goes.
  - **A hop you just took is drawn in a high-contrast colour** for a configurable window (20 minutes by default) and then reverts. Recency is tracked in memory, not written into the waypoint — baking it in would mean a server round-trip to set the colour and another twenty minutes later to put it back, churning the player's waypoint list for something purely cosmetic.
  - Detection tests the **arrival** end for a translocator block, not the departure end: after a hop the origin chunk may already be unloaded client-side, so a departure-side test would fail exactly when the feature is meant to fire.
  - Coordinates in titles are spawn-relative X/Z with absolute Y — the same form the vanilla coordinate readout shows, so the title reads as the number already on screen.

### Design notes — why the layout system works the way it does
- **It moves windows, it does not resize them.** Vintage Story has no resizable-dialog concept: a composer's size falls out of its child element bounds at compose time, and there is no per-dialog scale. Stretching a foreign composer's outer bounds would leave its contents sitting in the corner of a bigger background. Every window is therefore anchored by its top-left corner to the zone it is dropped on and keeps its own size. Rebuilding our own table at a different row count to fill a zone was tried and dropped: it churned the window on every layout change and was more disruptive than the tidiness was worth.
- **It never moves a HUD, only measures them.** HUDs generally re-assert their own position from their own config every tick, so moving one is a fight that shows up as jitter. Blocking cells instead gets the same result — nothing covers your HUDs — with no conflict.
- **Blocked cells are computed sample-and-hold, not live.** Vanilla's location readout (`HudElementCoordinates`) rewrites its text every 250ms, so it *resizes as you walk* (digit counts roll over, "North" becomes "Northeast") and re-stacks its own Y below whatever else is `RightTop`-aligned. Recomputing off a live scan would flicker cells between available and blocked while the player merely walks — the same oscillation `PositionMapButton`'s hysteresis exists to avoid. Rects are sampled continuously but only committed when the overlay opens, on a resolution/scale change, or on an explicit rescan, and a rect only counts once it has held still for ~2s. That dwell requirement also disqualifies the transient HUDs by construction: the cursor-followers and the centre-screen toasts never settle.
- **Assignments are stored as cell indices, never coordinates.** Vanilla's `dialogPositions` is a flat name → pixel map with no resolution key, which is fine until you dock a laptop or change GUI scale. Storing cells means a geometry change is a re-derivation rather than a repair. GUI scale and font size are watched via `ISettings.AddWatcher` (font size matters: autosized dialogs are FitToChildren, so text width changes their actual size); resolution has no event in `IClientEventAPI` at all and is polled on the existing watcher tick.
- **Nothing here knows a mod name.** HUD-ness is `EnumDialogType.HUD`, a base-class property; the rect comes from the composer every mod already has; persistence is keyed by `DialogName`, the string that mod passed to `CreateCompo`. This keeps the standing rule that all cross-mod behaviour in this mod is dynamic and nameless. An earlier draft filtered HUDs on `Bounds.Alignment` being a corner constant, which was wrong for exactly this reason — a HUD may legitimately be absolutely positioned with `EnumDialogArea.None`, as our own map button is.
- **The overlay is a lattice, not 400 quads.** At 20 x 20 a fill plus an outline per cell would be 800 draw calls every frame. It draws grid lines instead — 42 — and fills only the cells that carry meaning: blocked ones, the button row, and the hovered target. A fine grid also reads better as a lattice than as a wall of boxes. Because one cell on a fine grid is far smaller than most windows, dragging additionally previews the rectangle the window will *actually* occupy, since highlighting the cell alone would say almost nothing about the result.
- **The overlay stands down when no dialog is open.** Zones exist to arrange windows, so with none open there is nothing to arrange — and that guard is what makes it impossible to end up staring at a grid over the world with no obvious way to dismiss it.
- **The overlay is not in the input path at all.** `ShouldReceiveMouseEvents()` returns false, so it is never offered a mouse event and cannot swallow one. Drags are detected by watching which composer moves away from its at-press baseline while the button is down. The alternative — sitting early in the dispatch chain declining every event — is one careless edit away from eating another mod's clicks, and this mod already carries a scar from that class of bug on the keyboard side (see `HudPinMatrixMapButton.TextInputHasFocus`).

### Fixed
- **Pin Matrix's own buttons grew a "Move" title bar that did nothing.** Reported in Floating mode, where two of the three buttons could be dragged while **Pin Matrix Editor (P)** could not. A title bar is only draggable when the game has a stored position under that dialog name — `GuiElementDialogTitleBar` reads `GetDialogPosition` once, on compose, and stays *Fixed* if it finds nothing — and the pass that stores positions for other mods' windows skips ours twice over, since they are HUDs *and* they are our own. So a button window was draggable only if something had happened to leave a position behind for it, and one that had ever been set to *Fixed* could never be dragged again, because dragging was the only thing that would have restored the handle. Layout mode now seeds each of its own windows the position it already occupies, which moves nothing and makes the handle work, and takes it back when the grid is hidden.
  - Placement rules now ask whether the *player* placed a window rather than whether a position is merely stored — otherwise the seed would read as a placement, freezing the buttons wherever they sat the first time the grid was shown and stopping the configured **Cell** taking effect.
  - Clearing a seed is flushed to `clientsettings.json` rather than left in memory, because the layout manager writes that file whole whenever the zones are shown: a seed can reach disk, and a seed read back next session would be indistinguishable from a position you chose.
- **Leaving the Map windows layout screen dropped you in the waypoint manager.** That screen is only ever opened by the **Layout Options** button on the map, so both its Back button and the title-bar close sent you to a window you had not come from — and buried the map you were arranging behind it. Both now return to the map, with the zones still showing, and the button says **Back to map** so it reads as what it does. Every other screen is reached from the manager and still returns there.
- **Fix duplicates missed pins that read as identical in the table.** It compared positions to two decimal places while the table displays whole blocks, so two rows agreeing in every visible column — same name, same icon, same colour, same coordinates on screen — still would not group if their true positions differed by a fraction of a block. Positions are now compared at exactly the precision shown, which is what the rule always claimed: pins indistinguishable in the table are duplicates. Reported from a trader carrying three markers that the cleanup would not touch.

### Testing
- `tallybook` added to the compat matrix (8 combos, all passing) — another HUD-corner occupant, and a named target of the blocked-cell logic.
- Same-spot grouping was checked against transcribed cases before shipping, since it is the one cleanup that can delete pins which genuinely differ: one trader with three differently-named markers (all group, ours kept), a three-trader camp (nothing grouped), a camp with a stray unnamed pin (only the stray joins), a 0/3/6-block chain (no transitive merging), and the auto-marking-off fallback.

## [1.4.0] — 2026-08-16

### Added
- **Hide/show waypoints without deleting them** (requested by a player: *"I don't need to know where every copper node or resin tree is all the time, only when I need copper or resin"*). A hidden pin stops being drawn on the world map and the minimap — marker, hover text, pinned screen-edge arrow and middle-click editing all go with it — while staying exactly where it is on the server and staying listed, searchable and editable in the matrix.
  - **Vis column** with a clickable eye per row: open eye = on the map, struck-through eye = hidden. Hidden rows draw dimmed. It sorts like any other column, and clicking the eye on a folded duplicate group switches every copy at once.
  - **Hide / Show buttons** (utilities row) act on the selection, so the whole point of the feature is one pass: filter to the copper icon → **Select all filtered** → **Hide**. Instant — nothing is sent to the server, so there is no confirmation screen, no command throttle and no chat spam, in either direction.
  - **Show: all / visible / hidden** filter button at the end of the icon strip, composing with search, icons, colour, pinned-only and radius like every other filter. Defaults to **all**, so hidden pins never quietly vanish from the table too.
  - A standing **`N hidden`** count sits next to the pagination controls whenever anything is hidden.
  - **The distance tools skip hidden pins**: "hidden" means "not now", so the radius filter and **Next pin** walk past them rather than repeatedly landing on pins that aren't on the map. Set the filter to **Show: hidden** and they work on the hidden set instead.
  - Hidden state is stored per savegame in `ModData/pinmatrix/hidden-<savegame>.json` and is purely local: it is not shared, not exported, and pins restored from the recycle bin or re-created by **Move** come back visible (they are new waypoints with new Guids).

### Design notes — why hiding is a render filter and not a delete
- Vanilla's `Waypoint` has **no visibility field of any kind** (`Position, Title, Text, Color, Icon, ShowInWorld, Pinned, OwningPlayerUid, OwningPlayerGroupId, Temporary, Guid` — decompiled 1.22.6 VSEssentials), so "hidden" can only ever be this mod's own concept. That leaves two implementations: delete the waypoint and re-add it from a local copy, or leave it alone and skip drawing it.
- **Delete-and-restore was rejected.** It is destructive — losing the mod or its JSON would lose the waypoints for good — it costs one chat command per pin in *both* directions (300 resin trees, twice a week), and every restore mints a fresh Guid, breaking identity for the recycle bin, sharing and any other mod. A feature whose whole purpose is toggling large batches back and forth has to be instant, free and incapable of losing data.
- **What is actually done:** `WaypointMapLayer` draws markers by iterating a private `List<MapComponent> wayPointComponents`, rebuilt from `ownWaypoints` in `RebuildMapComponents()`. The full map and the minimap render through that one list, so dropping a component removes the pin everywhere at once with the waypoint itself untouched; `MapComponent.Dispose` is a no-op, so dropped components leak nothing. The list is rebuilt on map-open and on every server resync, so the filter is re-applied from the map-button watcher tick that already runs — and costs nothing at all until something is hidden.
- **Deliberately *not* touching `ownWaypoints`,** even though it is public and trimming it would need no reflection: `/waypoint modify|remove` indices are positions in that list, so removing entries would silently redirect every subsequent edit — vanilla's own map editor included — at the wrong waypoint. Components are the only safe cut point.
- **The reflection is the risk and it fails soft.** Both private field names are identical in 1.22.0 through 1.22.6 (checked against every server package in `tools/server-cache`). If a future version renames them, the feature disables itself with one logged warning and everything else carries on — pins already hidden simply come back into view, which is the right way to fail: visible and recoverable, never lost.
- Hiding is a **client-side render filter, not a permission**: hidden pins still exist server-side, still appear in `/waypoint list`, and a mod that reads the waypoint list and draws its own markers would still draw them (none of the five compat companions do).

### Changed
- The icon filter strip wraps at 24 icons per row instead of 27 — the tail of that row is where the visibility filter button now lives, and the vanilla icon set occupies the same two rows either way.
- The Name column gave up 36px to the new Vis column; long titles truncate a little sooner. The row is packed to the pixel and the Actions column had no slack.

## [1.3.2] — 2026-08-16

### Fixed
- **Typing into another mod's map-screen panel could open Pin Matrix** (reported against Boat Autopilot's route planner: naming a route "Port Nowhere" opened the editor on the `p`). The map button's plain-`P` shortcut now stands down whenever any text field anywhere in the GUI has keyboard focus. Root cause (decompiled 1.22.6 VintagestoryLib): mods attach their map panels to the vanilla world map dialog as *extra composers* — Boat Autopilot adds `worldmap-layer-boatroutes` with its route-name and filter inputs — and vanilla's protection for those inputs is `GuiElementEditableTextBase.OnKeyDown` marking every key handled while focused. That protection never reached us: `GuiManager` dispatches key-downs down `OpenedGuis` in *descending draw order*, and the Pin Matrix map button sits at 0.2 against the map dialog's 0.11, so it saw each keystroke first. Checking focus directly is the only guard that doesn't depend on that ordering. The rebindable **Settings > Controls** hotkey was never affected — vanilla runs hotkeys only after every dialog has declined the key.

### Added
- `MapButtonShortcutKey` config option (default `true`) to switch the map button's `P` shortcut off entirely. Not needed for the conflict above, which is fixed on its own; it is there for anyone who wants the key free regardless. With it off the button's label drops the `(P)` and the mod takes no part in keyboard dispatch at all.

### Testing
- **Game-version sweep** (`tools/version-sweep.ps1`, new here): the release zip is built once and that same artifact is run through the whole compat matrix — solo plus each companion mod plus all together — against real dedicated servers for every patch from **1.22.0 to 1.22.7**. The mod declares `"game": "1.22.0"` while being compiled against a single game version's references, so "one artifact, N servers" is the claim that needed a test rather than an assumption. 1.22.7 is the current latest and is now covered.

## [1.3.1] — 2026-08-08

### Fixed
- The **Group duplicates** label wrapped to a second line and overran the table header. A `AddStaticText` narrower than its string does not ellipsize — it wraps — and the abbreviated "Group dupes" was still short at typical GUI scales. The label is spelled out in full with a width that fits it, and **Refresh** moved down to the utilities row (next to Recycle bin) to free the space, rather than shaving pixels off neighbours again. Rule of thumb now recorded at the call site: ~9.5 unscaled px per character, plus padding, rounded up.

## [1.3.0] — 2026-08-08

### Added
- **Duplicate grouping and one-click cleanup.** A **Group dupes** switch folds every set of pins that are identical in *all* columns — title, icon, colour, pinned state and position — into a single row marked `x N copies`. Click a header to unfold it, click its checkbox to select the whole set. Unique pins keep drawing as ordinary rows, so the table only changes where there is something to see. Distance is not part of the comparison (it is derived from the position) and neither is the Actions column.
- **Fix duplicates** button, labelled with the live count (`Fix duplicates (37)...`), so duplication is visible without turning grouping on. It keeps the earliest copy of each set — the original, in the layer's own index order — and sends the rest to the recycle bin through the usual confirm-preview path. It deliberately scans the **whole** waypoint list rather than the filtered view: a filter hiding half a duplicate set would otherwise leave copies behind while reporting the job done.

### Fixed
- **Empty table on a server the map hadn't been opened on.** The client is only ever sent waypoints in reply to a map view-change packet (`WorldMapManager.OnViewChangedServer` → `WaypointMapLayer.ResendWaypoints`), and vanilla sends that only from the world map dialog — so a session that opened Pin Matrix by hotkey without ever opening the map had a genuinely empty `ownWaypoints` and the matrix reported "No waypoints yet" while the map itself was full of them. Opening the dialog with nothing loaded now sends that same packet itself (view rect trimmed to the player's own chunk, which the waypoint layer ignores anyway), and the existing 1-second poll picks up the reply. **Refresh** also re-requests. The empty-table message now names the actual cause — nothing synced yet, layer not ready, or synced-but-none-owned-by-you (group-shared pins can't be managed) — instead of claiming there are no waypoints.
- **Crash on opening the dialog** (`ArgumentNullException: Asset Data is null (Parameter 'svgAsset')`) whenever a waypoint icon's SVG data had not been loaded. Vanilla's `WaypointMapLayer` indexes `textures/icons/worldmap/` with `loadAsset: false` and registers one custom GUI icon per asset whose renderer draws the `IAsset` *object*, so painting an icon whose data is still null throws — and the icon filter strip paints every icon in the set at compose time, taking the whole dialog down with it. Vanilla only loads them lazily, when the world map first builds its waypoint icon textures, so this hit any session that reached Pin Matrix (via the hotkey) without opening the map. The dialog now loads the whole icon set itself on open: `IAssetManager.GetMany` hands back the *cached* asset instances and fills them in place, which is precisely what those renderers closed over.
- Belt and braces around the same fault, for an SVG that is genuinely unusable rather than merely unloaded: icons are probed once onto a scratch surface, every real draw is guarded, and a failure degrades to the icon's code as text while staying filterable. Verdicts are re-established on each dialog open so a transient failure can't blacklist half the set for the session. Vanilla's own `AddIconListPicker` has the identical landmine and can't be guarded from outside, so the Set icon / New pin pickers are built from the drawable subset only — the same list is used to resolve the picked index back to a code.

### Changed
- **The colour filter dropdown now shows the colours.** Each entry is a painted swatch followed by its hex and a live count — `▮ #3399ff (52)` — instead of a bare hex code. Entries are sorted round the colour wheel (greys first) rather than lexicographically by hex, so near-identical colours sit together.
- The dropdown lists **only colours that some waypoint actually uses**, not the whole palette, and drops a colour as soon as the last waypoint using it goes away (a filter on a vanished colour is cleared with it).
- **Counts respond to the other filters.** They report how many pins each colour would show under the current search / icon / pinned / radius filters, so they stay a useful targeting aid rather than a fixed census. Counts deliberately ignore the colour filter itself — otherwise picking one colour would zero out every other entry. Labels are baked into a texture at compose time, so they are rebuilt on each filter change; a change made while the menu is expanded is deferred until it closes (recomposing an open menu would reset its scroll position under the cursor).
- Opening the dialog no longer shows the first colour's checkbox ticked while filtering on nothing (the dropdown was constructed with a preselected index 0).

### Design notes
- Dropdown entry labels are run through VTML before being drawn, so the swatch is a custom `<pmswatch color="#rrggbb"/>` tag registered in `VtmlUtil.TagConverters` (namespaced — that table is process-wide and shared with every other mod) that paints a Cairo rectangle. The built-in tags can only tint *text*, which would have meant betting on the player's font having a block glyph.
- **The dropdown's width is load-bearing.** `GuiElementListMenu` sizes its expanded list to the widest entry but omits the multi-select checkbox column it then shifts every entry by, so the tail of each label — the count — clips unless the element's own width already covers swatch + hex + count + that offset. The field is 200px (was 145) and the rest of the filter bar is squeezed to fit it.
- **`VtmlUtil.TagConverters` does not survive a world.** `ClientMain.Dispose()` clears it on leaving one, while static state in the mod assembly lives for the whole process — so guarding registration with a `static bool registered` made the swatches work on the first world joined and silently vanish on every one after it. Registration is now keyed off the table's own contents and re-asserted when the dialog opens. Worth remembering for anything else registered into a process-wide game table.

## [1.2.1] — 2026-08-06

### Fixed
- The vanilla waypoint editor (row **Edit** button) opened *behind* the Pin Matrix window, where it was invisible and unreachable. Root cause (decompiled 1.22.6 VintagestoryLib): both dialogs draw at order 0.2, and the matrix table handles its row buttons on mouse-down — so after the click that spawns the editor, the game's GuiManager re-focuses the click-handling dialog, and `RequestFocus` re-raises it to the front of its draw-order group, burying the freshly opened editor. A same-order editor can never win that race. The editor Pin Matrix opens is now a trivial subclass drawing at 0.25: its own group, always above the matrix, immune to the re-raise, and it also receives mouse clicks first where the windows overlap (`RegisterDialog` orders same-input-order dialogs by descending draw order). The matrix also steals dialog *focus* at the end of the spawning click, so Pin Matrix re-focuses the editor one tick later — typing lands in the editor immediately.

## [1.2.0] — 2026-08-06

### Added
- **Distance slider** next to the "Within … blocks" radius filter (requested by a tester): drag through notches on a 1–2.5–5 ladder — off, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 — and since the filter re-applies live on every change, dragging right from "off" reveals your nearest pins in growing rings (sort by Dist for the full effect). Equal-width notches give the slider a log scale: fine control close-in, no runaway huge numbers at the top (an earlier unreleased +/− button design kept climbing past 10k — the slider's ladder is capped instead). Mouse wheel or arrow keys on the slider tick one notch at a time. The number box remains for exact values or radii beyond 10000; typing moves the slider to the nearest notch (display only — the typed value is what filters). The box's native tiny arrows and mouse wheel now step by 50 blocks (previously a useless 1); negative radii can no longer be entered.
- **"Next pin" button** (requested by the same tester): widens the radius just enough to admit the next-nearest pin that passes the other filters. From "off", the first click shows only your closest pin; every further click reveals the next one out — a one-button "expanding search". When nothing lies beyond the current radius, a notice says so and the radius stays put.

## [1.1.4] — 2026-08-05

### Changed
- `modinfo.json` now declares a real minimum game version — `"dependencies": { "game": "1.22.0" }` — instead of the empty string that had shipped since 1.0.0. Matches the Herty Cups convention. Consequence: the loader (SemVer `installed >= floor`) hard-disables the mod on 1.21.x and older clients; all 1.22.x clients load it fine.

### Design notes — what the dependency version actually does (decompiled 1.22.0 and 1.22.6, VintagestoryLib/VintagestoryAPI, Aug 2026)
- **The empty string was never the source of any in-game warning.** Both the loader (`SatisfiesVersion`: empty → always satisfied) and the mod-manager warning badge (`GuiElementModCell`: skips `""` and `"*"` outright) ignore it, identically in 1.22.0 and 1.22.6. The "requires ⟨version⟩" screen that prompted this change was traced in the client log to a different mod entirely: Clothing Rarity 1.1.2 declares `"game": "1.22.2"`, which on a 1.22.0 install fails the loader's SemVer check and aborts world join with the *nameless* message "A mod requires v1.22.2 of the game" (`disconnect-modrequiresnewerclient`) — the game never says which mod, inviting misattribution. Pin Matrix was innocent; the fix for that screen is updating the game (or disabling Clothing Rarity).
- **The mod-manager badge is not a SemVer floor check.** `GameVersion.IsCompatibleApiVersion` requires the declared floor's major.minor to equal the client's `APIVersion` constant — and 1.22.0, 1.22.1, and the first 1.22.2 build shipped with `APIVersion` stuck at `"1.21.0"` (fixed 2026-05-30, `anegostudios/vsapi` commit `caebe5cd`, by deriving it from `OverallMajorMinor`). On those clients ANY mod declaring a 1.22 floor — including this one from 1.1.4 on, and Herty Cups — shows the nonsense badge "This mod requests game version 1.22.0, but you are on 1.22.0/1.22.1. It might not load properly." The mod loads and runs fine regardless; the badge is cosmetic and disappears on late-1.22.2 through 1.22.6 clients.
- **No non-empty floor is badge-free on all six 1.22.x releases** (stale-`APIVersion` clients accept only 1.21 floors; fixed clients accept only 1.22 floors). `"1.22.0"` is chosen because it is honest, matches Herty Cups, and only mis-badges on the April/May-2026-era clients that the auto-updater is steadily draining.
- Trivia: the 1.22.0 release also shipped `VintagestoryAPI.dll` with a stale 1.21.0 file-version resource (every other binary says 1.22.0) — don't trust `VersionInfo.ProductVersion` on that DLL; the client log's "Game Version:" line is authoritative.

## [1.1.3] — 2026-08-04

(1.1.2 was never released; interim test builds are folded into this entry.)

### Fixed
- Root cause of the bouncing map-screen button found (thanks to careful bisection by a tester: vanilla only, coordinate overlay on/off): **vanilla's coordinate overlay (Ctrl+V) re-stacks itself below the first other RightTop-aligned dialog every 250ms** (`HudElementCoordinates.Every250ms` → `GetDialogBoundsInArea(RightTop)`, which matches purely on bounds alignment — GUI scale is irrelevant). The Pin Matrix button was a RightTop-aligned dialog, so the overlay parked itself under the button, the button's own overlap-avoidance reacted, and the two dialogs chased each other forever — 1.1.1's one-sided hysteresis couldn't stop vanilla's side of the dance. The button is now positioned with absolute coordinates (alignment `None`), making it invisible to vanilla's stacking system; it re-anchors itself on window resize / GUI-scale changes.
- Defense in depth kept from the investigation: the button auto-places only during a ~1 second settle window after the map opens and is then frozen for that map session, so no other periodically-moving HUD (e.g. mod panels like Boat Autopilot's) can drag it into a dance either. Worst case is a static overlap, which is cosmetic.
- New config escape hatch: set `MapButtonRightMargin` / `MapButtonYOffset` (unscaled px from the right/top edges) in `ModConfig/pinmatrix.json` to pin the button to a fixed spot and disable automatic placement entirely.

## [1.1.1] — 2026-08-04

### Changed
- The "Send to chat" share line no longer embeds the full `/waypoint addati` command — chat text cannot be selected/copied in Vintage Story, so it was unreadable noise. The line now ends with a compact `| icon #color [pinned]` tail; Pin Matrix clients rebuild the clickable add-link from it (lines from older versions still linkify), and "Copy command" on the Share screen remains the way to get the command for Discord.

### Fixed
- The "Pin Matrix Editor" map-screen button could bounce up and down every second when another HUD near the top-right corner periodically changed size (e.g. the vanilla coordinate box recomposing as the player moves). The auto-positioning re-picked the topmost free slot every tick; it now stays put while its current slot is clear and only moves when actually overlapped, so it cannot oscillate regardless of what other HUDs or mods do. The preferred top slot is re-tried each time the map is opened.

### Design notes — why the share message looks the way it does
Lessons learned while iterating on sharing in 1.1.0/1.1.1, recorded so the format isn't "simplified" back into a known dead end:

- **A client-side mod cannot send a clickable link.** The server escapes `<`/`>` in player chat, so VTML sent by a player arrives as literal text. The clickable add-link is therefore created by the *receiving* client: Pin Matrix on the receiver's side recognizes the share line and prints an extra local line with a `command://` link (vanilla confirm prompt before it runs). Players without the mod see only the plain share line.
- **Never put a command in chat for humans.** Vintage Story chat text cannot be selected or copied, so an inline `/waypoint addati ...` helps nobody — it's noise for vanilla players and redundant for modded ones. The command lives only behind the Share screen's "Copy command" button (for Discord and the like).
- **The `| icon #color [pinned]` tail is load-bearing, not clutter.** Everything the clickable link must reproduce has to travel inside the visible chat text: player chat has no hidden data channel (VTML is escaped, the packet's data field is server-controlled). Dropping the tail was tried and rejected — it forces the link to use a default icon/color and loses the pinned state. The tail is the minimum text that keeps the link full-fidelity.
- **Coordinates are shared in HUD convention.** Plain (unprefixed) numbers in waypoint commands resolve as X/Z spawn-relative, Y absolute — identical for every player on the server (vsapi `PopFlexiblePos` resolves them against `(spawnX, 0, spawnZ)`). The `=`-absolute form vanilla's dialog uses internally also works but confuses readers because it doesn't match the coordinates players see on their HUD and share by hand.

## [1.1.0] — 2026-08-04

### Added
- **Share** row action: a fourth mini-button in the Actions column opens a Share screen with two options.
  - **Send to chat**: posts one line with the pin's name, spawn-relative coordinates, and the `/waypoint addati` command so anyone can copy it. The command uses plain coordinates (X/Z spawn-relative, Y absolute — same as the coordinate HUD), matching how players share waypoints by hand.
  - **Copy command**: puts the `/waypoint addati` command on the clipboard for pasting into Discord etc.; recipients paste it into their chat box to add the pin.
- Players who also run Pin Matrix additionally get a clickable "[Pin Matrix] Click here to add this waypoint to your map" link when a share line arrives in chat (vanilla confirm prompt before the command runs). Vanilla clients just see the plain text — the server escapes VTML in player chat, so a clickable link cannot be sent directly.

## [1.0.1] — 2026-08-03

### Fixed
- "Pinned only" filter label was given too narrow a text box, causing it to wrap onto a second line and overlap the icon filter strip below. Widened the label and re-spaced the "Within … blocks" radius controls to match.

## [1.0.0] — 2026-08-01

Initial release for Vintage Story 1.22.x.

### Added
- Spreadsheet-style waypoint matrix: sortable columns (name, icon, color, X/Y/Z, distance, pinned) with pagination.
- Filtering: text search, color multi-select, icon strip multi-select, pinned-only toggle, and within-radius filter.
- Multi-select with bulk operations: delete, set color, set icon, pin/unpin, and rename — each with a confirmation screen.
- Recycle bin for deleted waypoints with restore support.
- Undo for the last bulk operation.
- New pin / move pin editor with absolute world coordinates.
- Export / import of waypoints.
- Map screen button ("Pin Matrix Editor") and bindable hotkey.
- Fully client-side — no server-side install required.

[1.5.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.5.0
[1.4.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.4.0
[1.3.2]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.3.2
[1.3.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.3.1
[1.3.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.3.0
[1.2.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.2.1
[1.2.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.2.0
[1.1.4]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.4
[1.1.3]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.3
[1.1.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.1
[1.1.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.0
[1.0.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.1
[1.0.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.0
