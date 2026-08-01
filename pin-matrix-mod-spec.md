# Pin Matrix — Client-Side Waypoint Manager for Vintage Story

**Spec v1.1 — final for implementation**
*(v1.1: mutation channel corrected to chat commands; index-shift rule extended to all bulk mutations; coordinate & color conventions specified; map-refresh demoted to stretch goal; hotkey config de-duplicated; pinned-flag soft cap added.)*
Target: Vintage Story 1.22.x. **Client-side only** (`Side = EnumAppSide.Client`) — installable per-player, no server mod required, works on any server. Coexists with auto-waypoint mods (waypointer, Translocator Paths, etc.) since it manages the same underlying waypoint store.
Proposed modid: `pinmatrix` (rename freely)

---

## 1. Design intent

Vanilla waypoint management is one-pin-at-a-time through a small map dialog. Auto-waypoint mods multiply pins into the hundreds (every resin node, every trader), and there is no way to clean up, retheme, or reorganize at scale. Pin Matrix provides a spreadsheet-style table over the player's full waypoint list: sort, filter, multi-select, bulk edit, inline edit, create, and export/import.

"Pins" = **all** of the player's waypoints. The vanilla *pinned* flag (pin-to-screen-edge) is one editable column, not the scope of the tool.

## 2. UI

**Hotkey** (default `P`, rebindable in vanilla controls) toggles a large dialog. The hotkey is registered via `capi.Input.RegisterHotKey` with `P` as the default only — vanilla's control settings are the single source of truth for rebinds; the mod config has **no** hotkey entry. Verify `P` is unbound in 1.22 defaults at registration time and log (don't silently swallow) a collision.

### Table
One row per waypoint. Columns:

| Column | Sortable | Inline-editable |
|---|---|---|
| ☐ (selection checkbox) | — | — |
| Name | yes | yes (text) |
| Icon | yes (groups) | yes (dropdown of vanilla icon set) |
| Color | yes (groups) | yes (color swatch → palette picker) |
| X / Y / Z | yes | yes (numeric) |
| Distance from player | yes | computed, read-only |
| Pinned | yes | yes (toggle) |

Header click sorts; second click reverses. Distance recomputes on open and on demand (refresh button), not per-frame.

**Coordinate convention:** `Waypoint.Position` is stored in **absolute** world coordinates, but everything the player sees elsewhere (coordinate HUD, F3, coords pasted in chat) is **relative to world spawn**. The table displays and edits **spawn-relative** X/Z (and raw Y), converting to/from absolute internally via the world's spawn position. This applies everywhere coordinates surface: the X/Y/Z columns, inline editing, the new-pin form, and the confirmation pages.

**Icon dropdown:** enumerate the icon set at runtime from the client `WaypointMapLayer`'s icon dictionary (`WaypointIcons` or its 1.22 equivalent) rather than hardcoding, so icons registered by other mods appear and render correctly.

**Color values:** waypoint colors are stored as int ARGB but players interact with named colors / hex / the vanilla palette. The swatch picker offers the vanilla palette plus a hex field. The color *filter* groups by exact stored value, but renders each distinct value as its own swatch chip (auto-waypoint mods often use off-palette colors — these must be filterable, not lumped or lost).

### Filter bar
- Live text search on name (substring, case-insensitive)
- Icon filter (**multi-select** — any number of icons at once)
- Color filter (**multi-select**)
- Pinned-only toggle
- Distance radius ("within N blocks of me")

All filters combine (AND across filter types; OR within a multi-select — e.g., icon ∈ {resin, trader} AND color = red AND within 3000 blocks). **"Select all filtered"** button — this is the workhorse: filter to icon=resin, select all, bulk-act.

### Selection & bulk actions
Checkbox per row, shift-click range select, select-all-filtered. Action bar applies to selection:

- **Delete** (→ recycle bin, see §5)
- **Set color** / **Set icon**
- **Pin / Unpin**
- **Rename: find & replace** (plain text) and **add prefix/suffix**
- **Export selection** (non-destructive; no confirmation needed)

**Every destructive/mutating bulk action routes through a confirmation page** before executing: a preview table of the affected rows showing **before → after** values (for delete: the rows headed to the bin), with the exact count in the confirm button ("Recolor 47 pins" / "Delete 132 pins"). Cancel returns to the matrix with selection intact. No bulk mutation ever fires directly from the action bar.

**Pinned-flag soft cap:** vanilla renders every pinned waypoint at the screen edge, so a bulk "Pin 300 pins" is technically valid and visually catastrophic. If a bulk pin would leave more than **20** waypoints pinned (configurable, `pinnedWarnThreshold`), the confirmation page shows a warning line with the resulting total. It warns, it does not block.

### Row action: show on map
Each row has a **"show on map"** action (button and/or double-click): opens the vanilla world map if closed and centers the viewport on that waypoint's position. Optionally flash/highlight the pin briefly for findability among dense markers. Fully client-side — the map viewport is client view state (vanilla itself recenters on the player at open, so the plumbing exists; locate the center-to handle in the 1.22 map dialog source rather than assuming its signature).

### Stretch goal (v1.1, config-gated): force map refresh
**Demoted from core scope** — this is the one feature that pokes engine internals with no public API (`ChunkMapLayer`'s tile cache and generation queue), its failure mode is a worse-looking map, and it is conceptually unrelated to waypoint management. It ships only if the invalidation path verifies cleanly against the 1.22 source, lives behind its own config flag (`enableMapRefresh`, default off), and must never block or delay the core table.

The feature itself: a **"Refresh map"** button (in the matrix dialog, optionally also a standalone hotkey) that invalidates the client's cached chunk-map tiles for currently loaded chunks and re-queues their image generation — repairing the common vanilla failure where map tiles silently fail to render. **Diagnostic signature this targets:** tiles that appear after a disconnect/rejoin (or eventually on their own) — proof the chunk data is present client-side and only the generation event was dropped; the button is the map-repair effect of a rejoin without the rejoin. **Scope honesty:** this only repaints areas whose chunks are presently loaded; explored-but-unloaded regions render from the stored map cache and cannot be regenerated without revisiting. Note that third-party block mods crashing during `GenerateChunkImage` are one known cause of half-rendered maps — this button re-triggers generation but cannot fix a mod that crashes it again.

### Creation
"New pin" button → editable blank row. Coordinates default to **player's current position** (one-click "here" button); all fields editable before commit. Commit sends the standard waypoint-add.

## 3. Data flow & mutation channel

- **Read:** the client's `WaypointMapLayer` holds the synced list of the player's own waypoints (`ownWaypoints`). The table is a view over that list — no separate store, no divergence.
- **Write — chat commands are the only channel.** There is no client→server waypoint packet; the waypoint sync packet is server→client only. Vanilla's own edit dialog builds `/waypoint modify <index> ...` strings and sends them via `capi.SendChatMessage`; creation is `/waypoint addati ...`, deletion is `/waypoint remove <index>`. Pin Matrix does the same. **Verify exact command syntax against the 1.22 source at implementation time** — do not trust remembered signatures. Consequence: a bulk op is a burst of chat commands, which makes throttling (below) a primary correctness concern, not optional paranoia.
- **Resync — there is no public waypoint-list-changed event.** The client layer rebuilds `ownWaypoints` when the server pushes data but raises nothing a third-party mod can subscribe to. **Plan of record:** re-read the layer after a short post-mutation delay, plus a slow poll (every few seconds while the dialog is open) to catch external changes. **Optional polish, not a dependency:** a Harmony patch on the layer's `OnDataFromServer` to get a real event — but the mod must work fully without Harmony.
- **Bulk throttling:** batch mutations with a small per-command delay to stay under server chat-command rate limits. Config knob `bulkOpDelayMs`, default **30** (not 0 — bursts of hundreds of commands with no gap will trip rate limiting on public servers).

## 4. CRITICAL: index-shift on ALL bulk mutations

Vanilla waypoint removal is **index-based**, and every deletion shifts subsequent indices. Naive iteration deletes the wrong pins and destroys the player's map. **`/waypoint modify` is also index-based**, so the hazard applies to bulk edits (recolor, re-icon, rename, pin) too, not just deletes: if the server resyncs the list mid-batch (e.g. another mod auto-adds a pin), captured indices go stale and edits land on the wrong waypoints.

**Requirements:**
- Bulk **deletes** MUST resolve selections to indices at execution time and delete in **strictly descending index order**.
- **Every** mutating bulk op (delete or modify) resolves all indices once, up front, then runs the whole batch while **ignoring/suppressing incoming resyncs** — no re-read of the layer until the batch completes, followed by exactly one full re-read. If a batch must be interrupted, the remainder is re-resolved from scratch against the fresh list, never resumed on stale indices.

This is a named, tested requirement — write test cases: (a) create 5, delete #1/#3/#5 as one bulk op, verify #2/#4 survive; (b) bulk-recolor a selection while a new waypoint is inserted mid-batch, verify the right pins changed color.

## 5. Safety: recycle bin + bulk-op undo

**Recycle bin (deletes).** Bulk- and single-deleted waypoints are never destroyed outright — they move to a client-side bin (JSON store under `ModData/pinmatrix/recyclebin.json`, persists across sessions). Bin view accessible from the main dialog:

- Lists binned pins with all attributes + deletion timestamp + which operation binned them
- **Restore** (selection or all) — re-creates pins through the normal mutation channel; restored pins are new waypoints (new indices), which is invisible to the player
- **Empty bin** (with its own confirmation — this one is truly permanent)
- Config cap on bin size; oldest entries pruned past it

The bin is local data: the server never knows about it, which is exactly right for a client-side mod. If the player deletes pins on one PC, the bin lives on that PC.

**Bulk-op undo (mutations).** Rename/recolor/re-icon/pin bulk actions snapshot the prior state of affected rows before executing; a **"Undo last bulk operation"** button reverts the most recent one (single-level undo). Covers the "find-replace matched more than I thought" case without needing the bin.

**Identity keying.** Check whether `Waypoint.Guid` exists in 1.22 (added to recent VS versions). If it does, key bin entries, undo snapshots, and table selections on the Guid. If not, identity = (position, name, icon) tuple. Either way, never key persistent safety data on list index.

**Auto-backup** (from earlier drafts) is retained as a belt-and-suspenders config option, default **off** now that the bin exists.

## 6. Export / import (JSON)

- **Export:** full list or current selection → JSON file. Default folder: `%Vintagestory data%/ModData/pinmatrix/` with timestamped filenames; user-visible path shown in UI.
- **Format:** array of `{name, icon, color, x, y, z, pinned}`. Human-readable, hand-editable, shareable with a friend on the same server/world.
  - **Coordinates are spawn-relative** (matching the table display and what players paste in chat), converted from/to absolute on export/import. This is what makes a file shared between two players on the same world "just work" — both convert against the same spawn. A top-level `"coords": "spawn-relative"` field is written so the convention is self-describing and future-proof.
  - **Colors are hex strings** (`"#RRGGBB"`, or `"#AARRGGBB"` when alpha ≠ FF) — readable and hand-editable, converted from/to int ARGB internally.
- **Import:** file picker (or paste-path field if the GUI API makes file dialogs painful). Additive — never wipes existing pins. **Dedupe rule:** skip any incoming waypoint whose position AND name exactly match an existing one; report "imported N, skipped M duplicates."
- Import executes through the same mutation channel with the same throttling.

## 7. Configuration (`ModConfig/pinmatrix.json`)

```jsonc
{
  // NOTE: no hotkey entry — vanilla controls are the single source of truth for the keybind
  "recycleBinMaxEntries": 500,      // oldest pruned beyond this
  "autoBackupBeforeBulkOps": false, // redundant with bin; available for the paranoid
  "backupRetentionCount": 20,
  "bulkOpDelayMs": 30,              // per-command throttle; 0 only for local/singleplayer
  "pinnedWarnThreshold": 20,        // warn on confirm page if bulk pin exceeds this total
  "enableMapRefresh": false,        // stretch-goal map refresh button (see §2)
  "rowsPerPage": 50                 // paginate above this; 0 = no paging
}
```

## 8. Implementation architecture

- C# client-only `ModSystem`; registers hotkey + a `GuiDialog` subclass.
- Table built on the vanilla GUI composer system (scrollable container of row composers). The VS GUI API is not table-native — budget implementation time for the grid, and paginate (config `rowsPerPage`) rather than fighting virtualization for 1000-row lists.
- Distance column: computed from `capi.World.Player.Entity.Pos` at refresh time.
- No server component, no network channel registration of its own, no world data. Uninstalling leaves zero residue beyond its config/backup files.
- **API caveat (same as always):** waypoint layer class names, packet shapes, and dialog plumbing must be verified against the official 1.22 modding docs / game source at implementation time. Build the read-only table first (list + sort + filter over live waypoints) as the API-validation prototype; add mutations second; bulk ops third; import/export last.

## 9. Edge cases

- Waypoint list changes underneath an open dialog (another mod auto-adds a pin) → table refreshes via the poll/delayed-re-read path (§3 — there is no change event without Harmony); selections persist by waypoint identity (§5 — Guid if available, else position+name+icon), else clear with a notice.
- Editing coordinates to another dimension/invalid Y → clamp to world bounds, no validation beyond numeric.
- Empty selection + bulk action → no-op with a hint, never "applies to all."
- Import file malformed → per-entry validation; import valid rows, report failures with line numbers; never partial-crash.

## 10. Out of scope for v1

- Waypoint sharing over the network / party sync (export-import a file instead)
- Grouping/foldering of pins (filters cover the need; revisit if usage demands)
- Map-click integration (creation is table-side or at-player-position only)
