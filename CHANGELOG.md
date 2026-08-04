# Changelog

All notable changes to Pin Matrix are documented here.

## [1.1.1] — 2026-08-04

### Changed
- The "Send to chat" share line no longer embeds the full `/waypoint addati` command — chat text cannot be selected/copied in Vintage Story, so it was unreadable noise. The line now ends with a compact `| icon #color [pinned]` tail; Pin Matrix clients rebuild the clickable add-link from it (lines from older versions still linkify), and "Copy command" on the Share screen remains the way to get the command for Discord.

### Fixed
- The "Pin Matrix Editor" map-screen button could bounce up and down every second when another HUD near the top-right corner periodically changed size (e.g. the vanilla coordinate box recomposing as the player moves). The auto-positioning re-picked the topmost free slot every tick; it now stays put while its current slot is clear and only moves when actually overlapped, so it cannot oscillate regardless of what other HUDs or mods do. The preferred top slot is re-tried each time the map is opened.

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

[1.1.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.1
[1.1.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.1.0
[1.0.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.1
[1.0.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.0
