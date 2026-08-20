# Pin Matrix — build & test notes

Client-side bulk waypoint manager for Vintage Story 1.22.x (tested against 1.22.0 – 1.22.7).
Spec: [pin-matrix-mod-spec.md](pin-matrix-mod-spec.md).

## Installed & ready

`dist/pinmatrix_1.7.0.zip` is already copied to `%APPDATA%\VintagestoryData\Mods\`. Launch the game, load a world, open the map (**M**) and click the **Pin Matrix Editor** button (top right). Optionally bind a hotkey to "Pin Matrix (waypoint manager)" in Settings → Controls — it ships unbound.

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
2. **Colour filter:** open the colour dropdown — every entry should be a painted swatch + hex + count, listing only colours your waypoints actually use, sorted round the colour wheel with greys first, and nothing ticked on a fresh open. Type in the search box or tick an icon in the icon dropdown and reopen the colour one: the counts must have followed those filters. Ticking a colour must *not* change the other entries' counts.
3. **Duplicates:** make 3 identical pins (New pin, same name/icon/colour/position), then flip **Group duplicates** — they collapse to one row marked `x 3 copies`. Click the header to unfold, click its checkbox to select all three, click the header again to refold. Then **Fix duplicates (N)...** → the preview must list exactly 2 of them (the original is kept) → confirm → restore from the recycle bin afterwards.
4. **Hide/show (1.4.0):** click the eye in the **Vis** column of one row — the pin must vanish from the world map *and* the minimap (open the map to check) while the row stays in the table, dimmed, with a struck-through eye; the `N hidden` count appears by the pagination controls. Click it again to bring it back. Then the real workflow: filter to one icon → **Select all filtered** → **Hide** → the map loses that whole class of pin instantly (no chat spam, no confirm screen) → **Show** restores them. Cycle the **Show: all / visible / hidden** button and confirm the table follows it. With hidden pins present and the filter on **all**, **Next pin** and the radius slider must walk past them; switch to **Show: hidden** and they must work on the hidden ones instead. Finally: hide a few pins, leave the world, come back — they must still be hidden (state is per savegame); delete a hidden pin via the recycle bin round trip and its restored copy must come back visible.
5. **Pin sets (1.6.0):** filter to something (say one icon plus a search word), press **Save as set...**, name it, pick an icon, Save. Open the world map: the **pin-set panel** must appear down the right-hand side with that set as a row — icon in colour, name, count. Click the row: every matching pin leaves the map at once and the icon greys out; click again to bring them back. Add a *new* pin matching the set's criteria and confirm the count grows without touching the set (the filter is re-evaluated, not a saved list). Save a set with **no** icon and confirm its row shows a plain colour chip instead. Then save enough sets to overflow the panel and confirm it pages rather than running off the screen, and that the pager remembers its page while you toggle rows. Finally untick **Show in the map panel** on one set — its row must go while the set stays on the **Pin sets** screen, where **Hide** / **Show** still work.
6. **Selection:** click rows (toggles), shift-click (range), "Select all filtered".
7. **The §4 index-shift test:** create 5 pins (`New pin...`), select #1/#3/#5, Delete → confirm — #2/#4 must survive. Restore from bin afterwards.
8. **Bulk edit:** filter to an icon, select all filtered, Set color → preview shows before→after → confirm. Then "Undo last bulk".
9. **Row actions:** Edit (opens the vanilla waypoint dialog — it must appear *in front of* the Pin Matrix window, with typing landing in its title box immediately), Map (centers the world map), Move (re-creates at new coords), Share (chat/clipboard sharing), double-click row = show on map.
10. **Export/Import:** export all, then re-import the same file — everything should be skipped as duplicates.
11. **Share:** row Share button → "Send to chat" posts the share line and (because your own client also runs Pin Matrix) a clickable "[Pin Matrix] Click here to add..." line should follow it — clicking shows the vanilla confirm prompt and re-creates the pin (a duplicate, since you already own it — delete it after). "Copy command" → paste into Notepad/Discord, then paste into the chat box and send — same pin appears.

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
   - **Tab row:** switch **Buttons** to Tab row and drag the Pin Matrix bar onto a row — it must
     stretch right across that row, aligned to the grid, with its buttons spread evenly, and follow
     you to whichever row you drop it on next. Windows dropped on that same row must snap to the
     cell they were dropped on like anywhere else.
   - **Pin-set panel:** it must open as a thin pull against the map's right edge, not a full
     column. Click the pull: the list slides open; click again: it shuts. Reopen the map and
     confirm it came back the way you left it. Toggle a set off and confirm the pull re-colours
     while shut. No drag handle when the zones are shown, and no snapping. Change GUI scale with
     the map open and confirm the whole assembly moves with the map's edge.
   - **Map layer tab:** open the map and confirm the left-hand tab strip reads **Translocator
     paths**, not `maplayer-pinmatrixtl`.
   - Change GUI scale, then font size, then resolution (or dock/undock). Snapped windows must
     re-derive into the same cells rather than drift or land off-screen.
   - **The tab row refuses other windows.** In tab-row mode, drag another mod's window over the
     tinted row: the cell must go red and the drop must be refused with a chat line saying why —
     the bar is stretched across that row and sits above the map dialog, so anything landing there
     would be underneath it and unclickable. Then drop the *bar* on a row that already holds a
     snapped window: that window must be released back to its own mod's position, with a chat line
     naming it.
   - **Recovery:** snap the Pin Matrix window somewhere awkward, then run `.pinmatrix resetlayout`
     from chat — everything returns and `dialogPositions` in `clientsettings.json` empties out.
     `.pinmatrix unsnap` with no name lists the snapped windows; with one (`.pinmatrix unsnap
     prospect`, matched case-insensitively on any part of the name) it releases just that one.
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
10. **Marching ants on recent paths** (the headless test cannot see any of this). Take a
    translocator hop with translocator paths on, then open the map: the hop you just took must be
    banded in the Recent and Path colours, crawling *towards the pad you arrived at*. Then check
    the two things the clip exists for — **zoom right in** until both pads are off opposite edges
    of the map frame (the line must stay banded all the way across the view, not stop dead
    mid-screen, and the frame rate must not move), and **pan the map** (the bands must stay put on
    the line and travel with it, not slide along it). Wait out `TranslocatorRecentMinutes` and
    confirm the line reverts to a plain single-colour one. Set speed to 0 for static stripes, and
    turn the switch off for the old flat line.

11. **Herty cup markers** (needs the Herty Cups mod; the headless test cannot see any of it). Turn
    **Mark Herty cups I place or collect from** on, then: place a cup and confirm one waypoint
    appears on it titled after the wood ("Herty cup: Pine"); right-click that same cup to collect
    and confirm a *second* waypoint is **not** added; break it, place another a few blocks away and
    confirm that one is marked separately. Right-click a cup someone else placed — it must be
    marked too, because you have now collected from it. Then confirm the negatives: walking past a
    cup marks nothing, right-clicking a plain log or a chest marks nothing, and with the switch off
    nothing is marked at all. On a server, confirm cups placed by other players out of your reach
    never appear.

12. **Interface scale follows the screen** (nothing here is visible to the headless test). Turn on
    **Fit interface scale to the screen** on Map options, note the reference it shows, then resize
    the game window (or reconnect at a lower resolution): the GUI scale must change, a chat line must
    say what it did, and the windows must end up taking roughly the same share of the screen as
    before. Go back to the original size and confirm the scale returns to **exactly** its old value —
    that round trip is the whole point of measuring from a fixed reference, and a drifting value
    means the base is being re-captured when it should not be. Then move the scale slider on the
    layout screen and confirm the reference updates to what you chose. Drag the slider fast: the
    readout must follow the handle, but the interface must only rescale once you stop.
    Then the one that matters most: change the GUI scale in **Vintage Story's own** Settings >
    Interface, resize the window, and resize back — the scale you set by hand must be what you end up
    with, not the one it replaced. Do that twice in a row and confirm it does not creep: each answer
    comes from the reference, never from the previous answer.
13. **The floating layout tools.** Show the zones: a small window with **Layout Options**, **Rescan
    HUDs**, **Reset layout** and **Rescue off-screen** must appear, and the Pin Matrix button bar
    must **not** change size when it does (in tab-row mode, it must not re-stretch either). Drag the
    tools window over the grid and confirm it does **not** snap to a cell — it stays exactly where
    you drop it, on or off a cell boundary — and that no assignment for it appears in
    `.pinmatrix unsnap`. Hide the zones: the tools window must disappear. Show them again: it must
    come back where you left it, and still be there after a restart. Cycle all four button modes with
    the zones showing and confirm the tools window is identical in each.
14. **Rescue off-screen windows.** Drag another mod's window mostly off the right edge, then shrink
    the game window until it is gone entirely. **Rescue off-screen** (or `.pinmatrix rescue`) must
    bring it back with its title bar reachable, and report how many it moved. Run it again: it must
    report zero and move nothing. Confirm it leaves HUDs alone.
15. **Colours are shown as colours.** Open the editor: the colour filter dropdown must show a
    swatch, the hex and a count per entry, and list *only* colours your pins use — delete the last
    pin of some colour and confirm that colour leaves the list. On **Pin sets**, a set filtering on
    colour must show swatches in its criteria line, not "colour #8a6fe8". Select some pins →
    **Set colour**: typing a complete hex must light the chip beside the box, clicking a palette
    colour must move it, and the preview screen must show the target beside its title and each
    pin's current colour beside its row. Same chip check on **New pin**.

## Config (`VintagestoryData/ModConfig/pinmatrix.json`)

Created on first run: `RecycleBinMaxEntries` (500), `AutoBackupBeforeBulkOps` (false), `BackupRetentionCount` (20),
`BulkOpDelayMs` (30 — per-command throttle for bulk ops), `PinnedWarnThreshold` (20), `EnableMapRefresh` (false),
`RowsPerPage` (14, clamped 5–18), `SameSpotRadius` (3 blocks — the tolerance for **Fix same-spot pins**, which groups
by location alone; kept far tighter than `TraderMarkerDedupeRadius` because that one only suppresses a new marker
while this one deletes existing pins), `MapButtonRightMargin` / `MapButtonYOffset` (-1 = automatic placement; set both
to unscaled px from the right/top edges to pin the map-screen button and disable overlap avoidance — use this if
another map-HUD mod shares the corner and the automatic placement picks a spot you dislike),
Interface scale: `LayoutAutoScale` (false — re-derive the game's GUI scale when the screen changes size),
`LayoutBaseScale` / `LayoutBaseScreenW` / `LayoutBaseScreenH` (the reference pairing every fit is measured
from; 0 = not captured yet, set by moving the scale slider or running `.pinmatrix guiscale`).

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
`TranslocatorRecentMinutes` (20, 0 = never highlight), `TranslocatorLineThickness` (2.5 px),
`TranslocatorRecentAnts` (true — band a recent path in the Recent and Path colours and crawl it towards the pad
you arrived at), `TranslocatorAntsDashPx` (9, clamped 3-40 — one band's width) and `TranslocatorAntsSpeed`
(16 px/s, clamped 0-200 — 0 leaves static stripes).

Herty cups (requires the Herty Cups mod): `HertyCupMarkersEnabled` (false), `HertyCupMarkerIcon` (`vessel`),
`HertyCupMarkerPinned` (false), `HertyCupMarkerColor` (`#C98B3A`), `HertyCupMarkerTitlePrefix` (`"Herty cup: "` —
changing it after the fact orphans the markers already placed, as the trader prefix does) and
`HertyCupDedupeRadius` (1.5 blocks — far tighter than the trader radius because a cup never moves, and two cups
on neighbouring faces of one trunk are two real cups a block apart).

Pin sets (1.6.0): `PinSets` (empty) — saved filters, each with `Name`, `Search`, `Icons`, `Colors`, `PinnedOnly`,
`ShowButton` (true — whether it gets a row in the map's pin-set panel) and `ButtonIcon` (empty = a plain colour
chip; a waypoint icon code = that icon, drawn in colour while any of the set's pins are visible and greyed once
they are all hidden). Edited from the editor's **Pin sets** screen rather than by hand. The panel appears down the
right of the world map as soon as one set exists, pages when there are more than fit, and can be dragged and
snapped like any other window while **Layout Zones** is on. Capped at 24 (`MaxPinSets`).

Window layout (1.5.0): `LayoutEnabled` (false — the feature and its map-screen **Layout Zones** button are
opt-in; tick **Snap map-screen windows to a grid** on the editor's **Map options** screen), `LayoutCols` / `LayoutRows` (20 x 10, anything from 1 to 50 each
way — the screen is split evenly by whatever you pick), (in tab-row mode the strip is the row your Pin Matrix
button bar is snapped to — drag the bar onto a row and everything else dropped there spreads evenly along it, the
column you drop on deciding only the order; there is no row number to set),
`LayoutZonePadding` (6 unscaled px of gap around each zone), `LayoutAvoidHuds` (true — disables grid cells a
HUD is sitting in so snapped windows never cover one; untick to use the whole screen),
`LayoutHudCoverageThreshold` (25 — percent of a cell a HUD must cover before that cell is disabled; any-overlap
would be far too aggressive, since the hotbar is wide but short and clips a whole row of cells),
`LayoutButtonMode` (how the map-screen buttons are packaged — "stacked", "parallel", "row" or "float"; there is no
configured cell any more, since you drag them where you want them), and `LayoutAssignments` (the remembered zones,
stored as **cell indices plus the grid they were dropped on** rather than coordinates, so a resolution, GUI-scale or
grid-size change re-derives the layout — a window four fifths of the way across a 20-wide grid stays four fifths of
the way across a 40-wide one — instead of stranding windows at coordinates that meant something on other hardware).

Window positions themselves are written to vanilla's own `clientsettings.json` (`dialogPositions`), the same
store its movable-dialog title bars use. Both files are per machine and nothing syncs, so a laptop and a
desktop keep independent layouts.

Recycle bin, exports and the per-savegame hidden-pin list live in `VintagestoryData/ModData/pinmatrix/`.
