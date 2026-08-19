# Pin Matrix — build & test notes

Client-side bulk waypoint manager for Vintage Story 1.22.x (tested against 1.22.0 – 1.22.7).
Spec: [pin-matrix-mod-spec.md](pin-matrix-mod-spec.md).

## Installed & ready

`dist/pinmatrix_1.5.0.zip` is already copied to `%APPDATA%\VintagestoryData\Mods\`. Launch the game, load a world, open the map (**M**) and click the **Pin Matrix Editor** button (top right). Optionally bind a hotkey to "Pin Matrix (waypoint manager)" in Settings → Controls — it ships unbound.

## Building

Requires the .NET 10 SDK (the game's assemblies target net10.0; SDK 9 refuses the reference).
A local SDK was used at build time; with a system SDK 10 installed it is just:

```
cd PinMatrix
dotnet build -c Release
```

References resolve from `%APPDATA%\Vintagestory` (override with `-p:VintageStoryPath=...`).
Package = zip of `modinfo.json` + `bin/Release/PinMatrix.dll`.

## What to test first

1. **Read-only pass:** open dialog (P) — table lists your waypoints; sort by clicking headers; search/filters; pagination. Try the radius slider: sort by Dist, then drag the slider next to "Within … blocks" slowly right from "off" — the nearest pins should appear in growing rings (10, 25, 50, … 10000); mouse wheel over the slider ticks one notch. Typing an exact number in the box still works and snaps the slider handle to the nearest notch. **Next pin** starts from "off" showing only your closest pin and reveals one more distance shell per click (respecting the other filters); at the last pin it reports "No pins beyond the current radius."
2. **Colour filter:** open the colour dropdown — every entry should be a painted swatch + hex + count, listing only colours your waypoints actually use, sorted round the colour wheel with greys first, and nothing ticked on a fresh open. Type in the search box or click an icon in the strip and reopen the dropdown: the counts must have followed those filters. Ticking a colour must *not* change the other entries' counts.
3. **Duplicates:** make 3 identical pins (New pin, same name/icon/colour/position), then flip **Group duplicates** — they collapse to one row marked `x 3 copies`. Click the header to unfold, click its checkbox to select all three, click the header again to refold. Then **Fix duplicates (N)...** → the preview must list exactly 2 of them (the original is kept) → confirm → restore from the recycle bin afterwards.
4. **Hide/show (1.4.0):** click the eye in the **Vis** column of one row — the pin must vanish from the world map *and* the minimap (open the map to check) while the row stays in the table, dimmed, with a struck-through eye; the `N hidden` count appears by the pagination controls. Click it again to bring it back. Then the real workflow: filter to one icon → **Select all filtered** → **Hide** → the map loses that whole class of pin instantly (no chat spam, no confirm screen) → **Show** restores them. Cycle the **Show: all / visible / hidden** button and confirm the table follows it. With hidden pins present and the filter on **all**, **Next pin** and the radius slider must walk past them; switch to **Show: hidden** and they must work on the hidden ones instead. Finally: hide a few pins, leave the world, come back — they must still be hidden (state is per savegame); delete a hidden pin via the recycle bin round trip and its restored copy must come back visible.
5. **Selection:** click rows (toggles), shift-click (range), "Select all filtered".
6. **The §4 index-shift test:** create 5 pins (`New pin...`), select #1/#3/#5, Delete → confirm — #2/#4 must survive. Restore from bin afterwards.
7. **Bulk edit:** filter to an icon, select all filtered, Set color → preview shows before→after → confirm. Then "Undo last bulk".
8. **Row actions:** Edit (opens the vanilla waypoint dialog — it must appear *in front of* the Pin Matrix window, with typing landing in its title box immediately), Map (centers the world map), Move (re-creates at new coords), Share (chat/clipboard sharing), double-click row = show on map.
9. **Export/Import:** export all, then re-import the same file — everything should be skipped as duplicates.
10. **Share:** row Share button → "Send to chat" posts the share line and (because your own client also runs Pin Matrix) a clickable "[Pin Matrix] Click here to add..." line should follow it — clicking shows the vanilla confirm prompt and re-creates the pin (a duplicate, since you already own it — delete it after). "Copy command" → paste into Notepad/Discord, then paste into the chat box and send — same pin appears.

## Implementation notes / deviations from spec

- **Coordinates** display/edit/export as X/Z spawn-relative, Y absolute (matches the coordinate HUD).
- **Inline cell editing** is implemented as: row **Edit** button → vanilla edit dialog (name/icon/color/pinned), row **Move** button → coordinate editor. True per-cell inline editing was dropped — the VS GUI composer makes per-row text inputs at 50 rows/page impractical.
- **Coordinate changes are re-creates**: `/waypoint modify` has no position args (verified against 1.22.6), so Move = add new + remove old (new Guid, new index — invisible to the player).
- **Deletes via the vanilla edit dialog bypass the recycle bin** (that's vanilla's own delete button). Deletes made through Pin Matrix are always binned.
- **Server chat feedback**: every command echoes one server response line ("Ok, waypoint added"), so big bulk ops produce chat spam. Unavoidable for a client-side mod — the command channel is the only mutation path.
- **Filter dropdowns** re-apply their state after a recompose (sort click / refresh); the underlying filter state is authoritative.
- **Group-shared waypoints** (owned by other players) are intentionally hidden: `/waypoint modify|remove` indices count own waypoints only — managing the synced-but-not-owned entries would corrupt the index space (verified against 1.22.6 server code).
- **Sharing is plain text on the wire**: the server escapes `<`/`>` in player chat, so a client-side mod cannot send a clickable VTML link. The share line is `[Pin Matrix] Name (x, y, z) | icon #color [pinned]` — receiving clients that run Pin Matrix parse it (`ChatShareLinks`) and locally print a clickable `command://` link with vanilla's confirm prompt. The tail carries the pin's look because chat has no hidden data channel; the command itself is never put in chat (chat text can't be selected/copied — clipboard via the Share screen's "Copy command" instead). Titles are stripped of `<>"&|` on share so they survive the round trip. See the 1.1.1 design notes in [CHANGELOG.md](CHANGELOG.md) for the full rationale.
- **Hiding is a client-side render filter, not waypoint data.** Vanilla's `Waypoint` has no visibility field, so hidden pins are a Pin Matrix concept: the mod drops their map components from `WaypointMapLayer`'s private render list (rebuilt on map-open and each resync, so it re-applies from the existing watcher tick) and never touches the waypoints themselves. Consequences worth knowing: the state is per savegame and local only (`ModData/pinmatrix/hidden-<savegame>.json`), it is not exported or shared, hidden pins still exist server-side and still show in `/waypoint list`, another mod that draws its own markers from the waypoint list would still draw them, and uninstalling Pin Matrix brings every hidden pin straight back. If a future game version renames those private fields the feature disables itself with a logged warning and everything becomes visible again — see the 1.4.0 design notes in [CHANGELOG.md](CHANGELOG.md).
- **`ownWaypoints` is deliberately never trimmed** to hide pins, though it is public and it would be easier: `/waypoint modify|remove` indices are positions in that list, so removing entries would silently point every later edit — vanilla's own map editor included — at the wrong waypoint.
- **Redraw map** button exists behind `EnableMapRefresh` (default off) in `ModConfig/pinmatrix.json`; it invokes vanilla's client-side `.map redraw` command.
- The hotkey ships **unbound** (assign one under Settings → Controls if wanted); the map-screen button is the primary entry point. No hotkey entry in the mod config (vanilla controls are the single source of truth).

## Compat regression testing

`.\tools\compat-test.ps1` builds the zip and boots a headless dedicated server for every
mod combination — solo, +Waypointer, +Translocator Paths, +ProspectTogether, +Boat
Autopilot, +Status HUD Continued, +Tallybook, and all together — failing on any `[Error]`/`[Warning]`
in the server log, a wrong mod count or load order, or a violated marker. Because Pin
Matrix is client-side only, the pinned markers are: the server must still *load* the
assembly and instantiate its mod systems (proves the DLL works against the game version),
and the mod must stay completely silent otherwise — exactly one `pinmatrix` mention in
`server-main.log` (its load-order entry) in every combo. Companion zips are cached in
`tools/compat-cache/` (gitignored), pulled from the live Mods folder or the mod DB on
first use. `-SkipBuild` reuses the packaged zip; to check another game version, pass
`-ServerExe <dir>\VintagestoryServer.exe` pointing at an extracted per-version dedicated
server package (`https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_<ver>.zip`).
Run it after any code change and before every release.

`.\tools\version-sweep.ps1` — run at the end of every version, before the release commit.
Builds the zip **once**, then runs that same artifact through the full compat matrix against
real dedicated servers for **1.22.0 through 1.22.7**, downloaded from the official CDN and
cached in `tools/server-cache/` (gitignored). One artifact, N servers — that is what backs the
`"game": "1.22.0"` dependency declaration, since the DLL is compiled against one game
version's references and claimed to work on all of them. `-Versions 1.22.0,1.22.7` checks just
the endpoints while iterating; `-KeepGoing` reports every version rather than stopping at the
first failure. When a new patch ships, append it to the `-Versions` default — the CDN 404s on
versions that don't exist, which is how you find the current latest.

Headless boots validate zip packaging, modinfo/dependency declarations, assembly loading,
and the client-only gate — **not** client visuals or in-world interaction. Manual
pre-release checklist for what the server can't see:

1. Open the world map with ProspectTogether and/or Boat Autopilot active — the Pin Matrix
   Editor button must land clear of their panels/readouts and stay frozen (no dancing).
2. With Status HUD Continued active, confirm its HUD elements and the map button coexist.
3. With Waypointer / Translocator Paths active, open the editor and bulk-edit a few pins —
   the table must track waypoints those mods add, and nothing may corrupt indices (§4 test
   in "What to test first").
4. Share round trip: "Send to chat" → clickable add-link appears and works.
5. **Hidden pins survive the map's own rebuilds** (1.4.0 — the headless test cannot see any of
   this): hide a pin, then force vanilla to rebuild its marker list several ways — close and
   reopen the map, pan far enough to trigger a resync, press **Refresh** in the matrix, add a
   new waypoint — the pin must stay off the map each time, and the minimap too. Confirm a
   hidden *pinned* waypoint also loses its screen-edge arrow. With Waypointer / Translocator
   Paths active, check whether their own markers still appear for a hidden pin (they read the
   waypoint list themselves; that is expected, not a bug — worth knowing about).
6. **Keyboard hand-off** (regression, 1.3.2): with Boat Autopilot active, click into its
   route-name or filter box on the map screen and type a name containing `p` — the letter
   must land in the box and Pin Matrix must not open. Then click the map itself and press
   `P` — the editor must open. Repeat for any other mod that puts a text field on the map.
7. **Window layout / snap zones** (1.5.0 — entirely invisible to the headless test). Opt-in:
   tick **Snap map-screen windows to a grid** on the editor's **Map options** screen first. No keybinding
   needed: open the map and use **Layout Zones (Z)**, the button directly under
   **Pin Matrix Editor (P)**.
   - With that switch off (the default), confirm the Layout Zones button is absent from
     the map screen and `Z` does nothing there; switch it on and the button must appear on the
     next map open without a restart.
   - Click it (and press `Z`) with the world map and a couple of mod panels open: the grid must
     draw over everything, the button must re-label to **Hide Zones (Z)**, and clicking again
     must remove the grid completely.
   - `Z` must be inert everywhere except the map screen, and must not fire while any text field
     has focus — the same guard as `P` (see item 6).
   - **Layout Options must return where it came from:** open it from the map, then leave by
     **Back to map** *and* by the title-bar close — both must put you back on the map with the
     zones still showing, never in the waypoint manager. Every other screen still returns to the
     manager, so check one of those (e.g. Map options) has not been changed by the same edit.
   - Drag a window by its title bar onto a cell and release — its top-left corner snaps to the
     zone and its size is left alone (nothing is ever resized, ours included). Close
     and reopen that window: it must come back in the same place (that path is vanilla's own
     `dialogPositions` store doing the work, not us).
   - **Cells a HUD sits in must be drawn red and refuse a drop.** Test against Tallybook and the
     vanilla location readout specifically — the readout resizes itself every 250ms as you walk,
     so walk around with the overlay held and confirm the blocked set does *not* flicker.
   - Untick **Do not cover HUDs**: the same cells must become available without the grid moving.
   - **Fine grids:** set 20 x 20 and confirm the overlay still renders as a clean lattice at full
     frame rate (it draws grid *lines* plus fills only for blocked/hovered cells — 42 draw calls,
     not 800). While dragging, the magenta outline must preview where the window will actually
     land, not just the cell under the cursor.
   - **Tab row:** set **Tab row** to a row index, drop two or three small windows on it, and
     confirm they spread evenly across the full width like tabs and re-space when one is added or
     removed. Dropping on a different column must change their order, not their position.
   - Change GUI scale, then font size, then resolution (or dock/undock). Snapped windows must
     re-derive into the same cells rather than drift or land off-screen.
   - **Recovery:** snap the Pin Matrix window somewhere awkward, then run `.pinmatrix resetlayout`
     from chat — everything returns and `dialogPositions` in `clientsettings.json` empties out.
   - Confirm nothing is stolen: with the overlay held, clicking and typing in other mods' panels
     must behave exactly as it does with the overlay hidden (the overlay declines mouse events
     outright, so this is a check that it still does).
   - **Cycle all four button modes and toggle zones on/off in each** (Stacked, Parallel, Tab row,
     Floating). Two client-only crashes lived here and neither was visible to the headless test:
     a window composed with zero visible buttons throws from `GuiElementDialogBackground`
     (`FitToChildren` with no children), and clearing one with `SingleComposer = null` throws from
     the `DlgComposers` indexer — use `ClearComposers()`. Floating mode with the zones hidden is
     the case that reaches both, since its Options window empties.
   - Confirm no button window can sit off-screen in any mode, at any GUI scale — a window you
     cannot see is indistinguishable from a mod that failed to load.
   - **Every one of our own buttons must be draggable the moment the grid is up** (1.5.0 fix), in
     all four modes. This broke before because a "Move" title bar is inert unless the game has a
     stored position for that dialog name, and one button that had ever been set to *Fixed* could
     never be dragged again. Check Floating specifically: all three windows, not two. Then hide the
     zones and confirm the Cell setting still moves the stacked window — i.e. the seeded position
     was given back rather than mistaken for a placement.
8. **Fix same-spot pins** (1.5.0 — destructive, and the headless test cannot see it). With trader
   auto-marking **on**, stand at a trader carrying more than one marker: the preview must list a
   `KEEP` line per set and name Pin Matrix's own trader marker as the survivor. Then check the case
   that must *not* fire: a trader camp of two or three different specialisations standing together
   must produce **no** sets at all. Switch auto-marking off and confirm the survivor becomes the
   earliest pin instead. Always restore from the recycle bin afterwards.
9. **Back out of every screen** (1.5.0 fix): **Map windows layout** must return to the map — by its
   **Back to map** button *and* by the title-bar close — never to the waypoint manager. Every other
   screen (Map options, Recycle bin, Export/Import, Share) must still return to the manager.

## Config (`VintagestoryData/ModConfig/pinmatrix.json`)

Created on first run: `RecycleBinMaxEntries` (500), `AutoBackupBeforeBulkOps` (false), `BackupRetentionCount` (20),
`BulkOpDelayMs` (30 — per-command throttle for bulk ops), `PinnedWarnThreshold` (20), `EnableMapRefresh` (false),
`RowsPerPage` (14, clamped 5–18), `SameSpotRadius` (3 blocks — the tolerance for **Fix same-spot pins**, which groups
by location alone; kept far tighter than `TraderMarkerDedupeRadius` because that one only suppresses a new marker
while this one deletes existing pins), `MapButtonRightMargin` / `MapButtonYOffset` (-1 = automatic placement; set both
to unscaled px from the right/top edges to pin the map-screen button and disable overlap avoidance — use this if
another map-HUD mod shares the corner and the automatic placement picks a spot you dislike),
`MapButtonShortcutKey` (true — the map button's plain `P` shortcut; it already stands down while any text
field has focus, so set it false only to free the key up entirely), `MapLayoutShortcutKey` (true — the same
thing for the `Z` zone-overlay shortcut).

Neither `P` nor `Z` is a registered hotkey: both live only on the map screen while the Pin Matrix buttons are
showing, so they cannot collide with any other mod's binding and never appear in Settings > Controls.

Map markers (1.5.0) — all of these are on the **Map options** screen, so the file is only for the ones with no
control of their own. Traders: `TraderMarkersEnabled` (false), `TraderMarkerIcon` (`trader`), `TraderMarkerPinned`
(false), `TraderMarkerTitlePrefix` (`"Trader: "` — also how same-spot cleanup recognises our own markers, so
changing it after the fact orphans the ones already placed), `TraderMarkerDedupeRadius` (24 blocks — the
already-marked test, deliberately loose because traders wander around their cart), `TraderMarkerMaxDistance`
(0 = mark as soon as the client loads them) and `TraderMarkerColors` (empty = the Waypointer palette).
Translocator paths: `TranslocatorPathsEnabled` (false), `TranslocatorMinJump` (40 blocks in one tick before a
move counts as a possible hop), `TranslocatorDedupeRadius` (6), `TranslocatorMarkerIcon` (`spiral`),
`TranslocatorMarkerPinned` (false), `TranslocatorMarkerColor` (`#8A6FE8`), `TranslocatorRecentColor` (`#00E5FF`),
`TranslocatorRecentMinutes` (20, 0 = never highlight) and `TranslocatorLineThickness` (2.5 px).

Window layout (1.5.0): `LayoutEnabled` (false — the feature and its map-screen **Layout Zones** button are
opt-in; tick **Snap map-screen windows to a grid** on the editor's **Map options** screen), `LayoutCols` / `LayoutRows` (20 x 10, anything from 1 to 20 each
way — the screen is split evenly by whatever you pick), `LayoutButtonRow` (-1 = none; set it to a row index to
make that row a tab strip, where everything dropped on it is spread evenly across the full width and the column
you drop on only decides the order),
`LayoutZonePadding` (6 unscaled px of gap around each zone), `LayoutAvoidHuds` (true — disables grid cells a
HUD is sitting in so snapped windows never cover one; untick to use the whole screen),
`LayoutHudCoverageThreshold` (25 — percent of a cell a HUD must cover before that cell is disabled; any-overlap
would be far too aggressive, since the hotbar is wide but short and clips a whole row of cells),
`LayoutButtonMode` / `LayoutButtonCol` / `LayoutButtonCellRow` (how the map-screen buttons are packaged, and the
cell they anchor to — "stacked", "parallel", "row" or "float"), and `LayoutAssignments` (the remembered zones,
stored as **cell indices** rather than coordinates so a resolution or GUI-scale change re-derives the layout
instead of stranding windows at coordinates that meant something on other hardware).

Window positions themselves are written to vanilla's own `clientsettings.json` (`dialogPositions`), the same
store its movable-dialog title bars use. Both files are per machine and nothing syncs, so a laptop and a
desktop keep independent layouts.

Recycle bin, exports and the per-savegame hidden-pin list live in `VintagestoryData/ModData/pinmatrix/`.
