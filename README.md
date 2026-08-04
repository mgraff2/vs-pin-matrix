# Pin Matrix — build & test notes

Client-side bulk waypoint manager for Vintage Story 1.22.x. Spec: [pin-matrix-mod-spec.md](pin-matrix-mod-spec.md).

## Installed & ready

`dist/pinmatrix_1.1.3.zip` is already copied to `%APPDATA%\VintagestoryData\Mods\`. Launch the game, load a world, open the map (**M**) and click the **Pin Matrix Editor** button (top right). Optionally bind a hotkey to "Pin Matrix (waypoint manager)" in Settings → Controls — it ships unbound.

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

1. **Read-only pass:** open dialog (P) — table lists your waypoints; sort by clicking headers; search/filters; pagination.
2. **Selection:** click rows (toggles), shift-click (range), "Select all filtered".
3. **The §4 index-shift test:** create 5 pins (`New pin...`), select #1/#3/#5, Delete → confirm — #2/#4 must survive. Restore from bin afterwards.
4. **Bulk edit:** filter to an icon, select all filtered, Set color → preview shows before→after → confirm. Then "Undo last bulk".
5. **Row actions:** Edit (opens the vanilla waypoint dialog), Map (centers the world map), Move (re-creates at new coords), Share (chat/clipboard sharing), double-click row = show on map.
6. **Export/Import:** export all, then re-import the same file — everything should be skipped as duplicates.
7. **Share:** row Share button → "Send to chat" posts the share line and (because your own client also runs Pin Matrix) a clickable "[Pin Matrix] Click here to add..." line should follow it — clicking shows the vanilla confirm prompt and re-creates the pin (a duplicate, since you already own it — delete it after). "Copy command" → paste into Notepad/Discord, then paste into the chat box and send — same pin appears.

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

## Config (`VintagestoryData/ModConfig/pinmatrix.json`)

Created on first run: `RecycleBinMaxEntries` (500), `AutoBackupBeforeBulkOps` (false), `BackupRetentionCount` (20),
`BulkOpDelayMs` (30 — per-command throttle for bulk ops), `PinnedWarnThreshold` (20), `EnableMapRefresh` (false),
`RowsPerPage` (14, clamped 5–18), `MapButtonRightMargin` / `MapButtonYOffset` (-1 = automatic placement; set both
to unscaled px from the right/top edges to pin the map-screen button and disable overlap avoidance — use this if
another map-HUD mod shares the corner and the automatic placement picks a spot you dislike).

Recycle bin + exports live in `VintagestoryData/ModData/pinmatrix/`.
