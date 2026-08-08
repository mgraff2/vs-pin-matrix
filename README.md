# Pin Matrix — build & test notes

Client-side bulk waypoint manager for Vintage Story 1.22.x. Spec: [pin-matrix-mod-spec.md](pin-matrix-mod-spec.md).

## Installed & ready

`dist/pinmatrix_1.3.1.zip` is already copied to `%APPDATA%\VintagestoryData\Mods\`. Launch the game, load a world, open the map (**M**) and click the **Pin Matrix Editor** button (top right). Optionally bind a hotkey to "Pin Matrix (waypoint manager)" in Settings → Controls — it ships unbound.

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
3. **Duplicates:** make 3 identical pins (New pin, same name/icon/colour/position), then flip **Group dupes** — they collapse to one row marked `x 3 copies`. Click the header to unfold, click its checkbox to select all three, click the header again to refold. Then **Fix duplicates (N)...** → the preview must list exactly 2 of them (the original is kept) → confirm → restore from the recycle bin afterwards.
4. **Selection:** click rows (toggles), shift-click (range), "Select all filtered".
5. **The §4 index-shift test:** create 5 pins (`New pin...`), select #1/#3/#5, Delete → confirm — #2/#4 must survive. Restore from bin afterwards.
6. **Bulk edit:** filter to an icon, select all filtered, Set color → preview shows before→after → confirm. Then "Undo last bulk".
7. **Row actions:** Edit (opens the vanilla waypoint dialog — it must appear *in front of* the Pin Matrix window, with typing landing in its title box immediately), Map (centers the world map), Move (re-creates at new coords), Share (chat/clipboard sharing), double-click row = show on map.
8. **Export/Import:** export all, then re-import the same file — everything should be skipped as duplicates.
9. **Share:** row Share button → "Send to chat" posts the share line and (because your own client also runs Pin Matrix) a clickable "[Pin Matrix] Click here to add..." line should follow it — clicking shows the vanilla confirm prompt and re-creates the pin (a duplicate, since you already own it — delete it after). "Copy command" → paste into Notepad/Discord, then paste into the chat box and send — same pin appears.

## Implementation notes / deviations from spec

- **Coordinates** display/edit/export as X/Z spawn-relative, Y absolute (matches the coordinate HUD).
- **Inline cell editing** is implemented as: row **Edit** button → vanilla edit dialog (name/icon/color/pinned), row **Move** button → coordinate editor. True per-cell inline editing was dropped — the VS GUI composer makes per-row text inputs at 50 rows/page impractical.
- **Coordinate changes are re-creates**: `/waypoint modify` has no position args (verified against 1.22.6), so Move = add new + remove old (new Guid, new index — invisible to the player).
- **Deletes via the vanilla edit dialog bypass the recycle bin** (that's vanilla's own delete button). Deletes made through Pin Matrix are always binned.
- **Server chat feedback**: every command echoes one server response line ("Ok, waypoint added"), so big bulk ops produce chat spam. Unavoidable for a client-side mod — the command channel is the only mutation path.
- **Filter dropdowns** re-apply their state after a recompose (sort click / refresh); the underlying filter state is authoritative.
- **Group-shared waypoints** (owned by other players) are intentionally hidden: `/waypoint modify|remove` indices count own waypoints only — managing the synced-but-not-owned entries would corrupt the index space (verified against 1.22.6 server code).
- **Sharing is plain text on the wire**: the server escapes `<`/`>` in player chat, so a client-side mod cannot send a clickable VTML link. The share line is `[Pin Matrix] Name (x, y, z) | icon #color [pinned]` — receiving clients that run Pin Matrix parse it (`ChatShareLinks`) and locally print a clickable `command://` link with vanilla's confirm prompt. The tail carries the pin's look because chat has no hidden data channel; the command itself is never put in chat (chat text can't be selected/copied — clipboard via the Share screen's "Copy command" instead). Titles are stripped of `<>"&|` on share so they survive the round trip. See the 1.1.1 design notes in [CHANGELOG.md](CHANGELOG.md) for the full rationale.
- **Map refresh** button exists behind `EnableMapRefresh` (default off) in `ModConfig/pinmatrix.json`; it invokes vanilla's client-side `.map redraw` command.
- The hotkey ships **unbound** (assign one under Settings → Controls if wanted); the map-screen button is the primary entry point. No hotkey entry in the mod config (vanilla controls are the single source of truth).

## Compat regression testing

`.\tools\compat-test.ps1` builds the zip and boots a headless dedicated server for every
mod combination — solo, +Waypointer, +Translocator Paths, +ProspectTogether, +Boat
Autopilot, +Status HUD Continued, and all together — failing on any `[Error]`/`[Warning]`
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

## Config (`VintagestoryData/ModConfig/pinmatrix.json`)

Created on first run: `RecycleBinMaxEntries` (500), `AutoBackupBeforeBulkOps` (false), `BackupRetentionCount` (20),
`BulkOpDelayMs` (30 — per-command throttle for bulk ops), `PinnedWarnThreshold` (20), `EnableMapRefresh` (false),
`RowsPerPage` (14, clamped 5–18), `MapButtonRightMargin` / `MapButtonYOffset` (-1 = automatic placement; set both
to unscaled px from the right/top edges to pin the map-screen button and disable overlap avoidance — use this if
another map-HUD mod shares the corner and the automatic placement picks a spot you dislike).

Recycle bin + exports live in `VintagestoryData/ModData/pinmatrix/`.
