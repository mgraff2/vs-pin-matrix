# Changelog

All notable changes to Pin Matrix are documented here.

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
