# Changelog

All notable changes to Pin Matrix are documented here.

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

[1.0.1]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.1
[1.0.0]: https://github.com/mgraff2/vs-pin-matrix/releases/tag/v1.0.0
