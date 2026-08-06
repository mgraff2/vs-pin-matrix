# Changelog

All notable changes to Pin Matrix are documented here.

## [1.2.0] — unreleased

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

[1.1.4]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.4
[1.1.3]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.3
[1.1.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.1
[1.1.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.0
[1.0.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.1
[1.0.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.0
