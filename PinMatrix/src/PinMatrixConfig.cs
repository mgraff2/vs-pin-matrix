using System;
using System.Collections.Generic;

namespace PinMatrix
{
    public class PinMatrixConfig
    {
        /// <summary>Max entries kept in the recycle bin; oldest pruned beyond this.</summary>
        public int RecycleBinMaxEntries { get; set; } = 500;

        /// <summary>Redundant with the recycle bin; exports a full backup before every mutating bulk op.</summary>
        public bool AutoBackupBeforeBulkOps { get; set; } = false;

        /// <summary>How many auto-backup files to keep.</summary>
        public int BackupRetentionCount { get; set; } = 20;

        /// <summary>Per-command delay in ms during bulk operations. 0 = burst (fine for singleplayer/local).</summary>
        public int BulkOpDelayMs { get; set; } = 30;

        /// <summary>Warn on the confirmation page if a bulk pin would leave more than this many waypoints pinned.</summary>
        public int PinnedWarnThreshold { get; set; } = 20;

        /// <summary>
        /// Blocks of slack for "Fix same-spot pins", which groups pins by location alone.
        ///
        /// Exact equality would miss the sets this exists for: a trader wanders a few blocks around
        /// its cart, and a hand-placed pin is rarely on the same block as an auto-placed one.
        /// Deliberately far tighter than <see cref="TraderMarkerDedupeRadius"/> — that one only ever
        /// suppresses a new marker, while this one deletes pins that may be genuinely different, so
        /// it must not cast a wide net.
        /// </summary>
        public double SameSpotRadius { get; set; } = 3;

        /// <summary>Show the "Redraw map" utility button (invokes the vanilla client-side ".map redraw" command).</summary>
        public bool EnableMapRefresh { get; set; } = false;

        /// <summary>Table rows per page (5-18; the table area is sized to fit).</summary>
        public int RowsPerPage { get; set; } = 14;

        /// <summary>
        /// Fixed placement for the map-screen button, in unscaled pixels from the right/top edge.
        /// -1 (default) = automatic placement. Set both to pin the button and disable the
        /// automatic overlap-avoidance entirely — the escape hatch when another mod's map HUD
        /// occupies the same corner.
        /// </summary>
        public int MapButtonRightMargin { get; set; } = -1;
        public int MapButtonYOffset { get; set; } = -1;

        /// <summary>
        /// Whether plain "P" on the map screen opens the editor (the button's own shortcut; the
        /// rebindable Settings &gt; Controls hotkey is unaffected). It is already suppressed while
        /// any text field has focus, so it does not eat typing — set this to false only to free
        /// the key up completely.
        /// </summary>
        public bool MapButtonShortcutKey { get; set; } = true;

        /// <summary>
        /// Whether plain "Z" on the map screen toggles the layout zone overlay — the companion to
        /// MapButtonShortcutKey, with exactly the same guards. Like P it is not a registered
        /// hotkey: it exists only while the map-screen buttons are showing and the map has focus,
        /// so it cannot collide with any other mod's binding.
        /// </summary>
        public bool MapLayoutShortcutKey { get; set; } = true;

        // ------------------------------------------------------------------ map features

        /// <summary>
        /// Auto-drop a waypoint on every trader the player walks past. Off by default: writing
        /// waypoints onto someone's map uninvited is not a sane default.
        /// </summary>
        public bool TraderMarkersEnabled { get; set; } = false;

        /// <summary>Waypoint icon for trader markers. "trader" is a vanilla icon, so no assets are needed.</summary>
        public string TraderMarkerIcon { get; set; } = "trader";

        /// <summary>Whether trader markers get the screen-edge pinned arrow.</summary>
        public bool TraderMarkerPinned { get; set; } = false;

        /// <summary>Prefixed to the trade specialisation, e.g. "Trader: Luxuries".</summary>
        public string TraderMarkerTitlePrefix { get; set; } = "Trader: ";

        /// <summary>
        /// Blocks within which an existing trader-icon waypoint counts as "this one is already
        /// marked". Traders wander a few blocks around their cart, so this is deliberately loose.
        /// NOT a detection range — see <see cref="TraderMarkerMaxDistance"/>.
        /// </summary>
        public double TraderMarkerDedupeRadius { get; set; } = 24;

        /// <summary>
        /// How close the player must get before a trader is marked, in blocks. 0 = mark any trader
        /// the client has loaded, which is governed by the server's entity-tracking range and so
        /// usually fires before the trader is even visible. Set this to mark only traders you
        /// actually walk up to.
        /// </summary>
        public double TraderMarkerMaxDistance { get; set; } = 0;

        /// <summary>
        /// Colour per trade specialisation. Defaults are Laimfo's Waypointer palette, so the same
        /// kind of trader reads as the same colour whichever mod put the marker there. Empty =
        /// use the defaults.
        /// </summary>
        public Dictionary<string, string> TraderMarkerColors { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Record and draw translocator hops the player has actually travelled. Off by default.
        /// Nothing is ever drawn for a pad you merely walked past — that is the point of it.
        /// </summary>
        public bool TranslocatorPathsEnabled { get; set; } = false;

        /// <summary>Blocks the player must move in one tick before it is treated as a possible hop.</summary>
        public double TranslocatorMinJump { get; set; } = 40;

        /// <summary>Blocks within which an existing marker means this pad end is already recorded.</summary>
        public double TranslocatorDedupeRadius { get; set; } = 6;

        /// <summary>Waypoint icon for pad markers. "spiral" is a vanilla icon, so no assets are needed.</summary>
        public string TranslocatorMarkerIcon { get; set; } = "spiral";

        public bool TranslocatorMarkerPinned { get; set; } = false;

        /// <summary>Colour of the pad markers and of the line drawn between them.</summary>
        public string TranslocatorMarkerColor { get; set; } = "#8A6FE8";

        /// <summary>
        /// Colour a path is drawn in just after it has been used, so the hop you just took stands
        /// out from the rest of the network. Deliberately far from the default in hue and
        /// brightness — a "recent" highlight that needs squinting at is not a highlight.
        /// </summary>
        public string TranslocatorRecentColor { get; set; } = "#00E5FF";

        /// <summary>Minutes a path stays highlighted after use before reverting to the default colour.</summary>
        public double TranslocatorRecentMinutes { get; set; } = 20;

        /// <summary>Line thickness on the map, in pixels.</summary>
        public double TranslocatorLineThickness { get; set; } = 2.5;

        // ------------------------------------------------------------------ window layout (zones)

        /// <summary>
        /// Master switch for the snap-grid, and for its map-screen entry point: while this is off
        /// (the default) the "Layout Zones" button does not exist at all, Z does nothing, remembered
        /// zones are ignored and nothing is moved. Switched on from the editor's Map options screen
        /// — opt-in, so players who never rearrange windows never see the extra button.
        /// </summary>
        public bool LayoutEnabled { get; set; } = false;

        /// <summary>Grid size, capped at ZoneGrid.MaxCols/MaxRows.</summary>
        public int LayoutCols { get; set; } = 20;
        public int LayoutRows { get; set; } = 10;

        /// <summary>Gap inset around each zone, in unscaled units.</summary>
        public double LayoutZonePadding { get; set; } = 6;

        /// <summary>
        /// Disable grid cells that a HUD is sitting in, so snapped windows never cover one. Off
        /// lets you use the whole screen and overlap them deliberately.
        /// </summary>
        public bool LayoutAvoidHuds { get; set; } = true;

        /// <summary>
        /// Percent of a cell a HUD must cover before that cell is disabled. Any-overlap would be
        /// far too aggressive: the hotbar is wide but short, so it clips the bottom edge of a whole
        /// row of cells while occupying a fraction of their height.
        /// </summary>
        public int LayoutHudCoverageThreshold { get; set; } = 25;

        /// <summary>
        /// How the Pin Matrix map-screen buttons are packaged while layout management is on:
        ///   "row"      - one window stretched across LayoutButtonRow, buttons spread evenly
        ///   "stacked"  - one window, buttons in a column, snapped to LayoutButtonCol/CellRow
        ///   "parallel" - one window, buttons in a row, snapped to LayoutButtonCol/CellRow
        ///   "float"    - one window per button, each placed independently
        /// </summary>
        public string LayoutButtonMode { get; set; } = "stacked";

        /// <summary>
        /// The row used as a tab strip in "row" mode. Anything snapped onto it is spread evenly
        /// across the full width instead of sitting in the single cell it was dropped on — the
        /// column it was dropped on only decides its order along the strip. -1 = none.
        /// </summary>
        public int LayoutButtonRow { get; set; } = -1;

        /// <summary>
        /// Starting cell for "stacked" and "parallel" modes; the buttons run from its top-left corner.
        /// Defaults to the upper right of the default 20x10 grid, which is roughly where the
        /// floating stack used to sit — a fresh install should not find its buttons in the map's
        /// top-left corner. Clamped into range on smaller grids.
        /// </summary>
        public int LayoutButtonCol { get; set; } = 16;
        public int LayoutButtonCellRow { get; set; } = 1;

        /// <summary>
        /// Remembered zones, keyed by composer DialogName. Stored as cell indices, never as
        /// coordinates, so changing resolution or GUI scale re-derives the layout rather than
        /// stranding windows at coordinates that meant something on other hardware.
        /// </summary>
        public List<ZoneAssignment> LayoutAssignments { get; set; } = new List<ZoneAssignment>();

        public void Clamp()
        {
            RecycleBinMaxEntries = Math.Max(10, RecycleBinMaxEntries);
            BackupRetentionCount = Math.Max(1, BackupRetentionCount);
            BulkOpDelayMs = Math.Min(2000, Math.Max(0, BulkOpDelayMs));
            PinnedWarnThreshold = Math.Max(1, PinnedWarnThreshold);
            SameSpotRadius = Math.Min(64, Math.Max(0, SameSpotRadius));
            if (RowsPerPage <= 0) RowsPerPage = 18;
            RowsPerPage = Math.Min(18, Math.Max(5, RowsPerPage));
            if (MapButtonRightMargin >= 0) MapButtonRightMargin = Math.Min(4000, MapButtonRightMargin);
            if (MapButtonYOffset >= 0) MapButtonYOffset = Math.Min(4000, MapButtonYOffset);

            if (TraderMarkerColors == null) TraderMarkerColors = new Dictionary<string, string>();
            TraderMarkerDedupeRadius = Math.Min(256, Math.Max(1, TraderMarkerDedupeRadius));
            TraderMarkerMaxDistance = Math.Min(4096, Math.Max(0, TraderMarkerMaxDistance));

            TranslocatorMinJump = Math.Min(4096, Math.Max(8, TranslocatorMinJump));
            TranslocatorDedupeRadius = Math.Min(64, Math.Max(1, TranslocatorDedupeRadius));
            TranslocatorRecentMinutes = Math.Min(1440, Math.Max(0, TranslocatorRecentMinutes));
            TranslocatorLineThickness = Math.Min(12, Math.Max(0.5, TranslocatorLineThickness));
            if (string.IsNullOrWhiteSpace(TranslocatorMarkerIcon)) TranslocatorMarkerIcon = "spiral";
            if (string.IsNullOrWhiteSpace(TraderMarkerIcon)) TraderMarkerIcon = "trader";
            if (TraderMarkerTitlePrefix == null) TraderMarkerTitlePrefix = "";

            LayoutCols = Math.Min(ZoneGrid.MaxCols, Math.Max(1, LayoutCols));
            LayoutRows = Math.Min(ZoneGrid.MaxRows, Math.Max(1, LayoutRows));
            LayoutZonePadding = Math.Min(40, Math.Max(0, LayoutZonePadding));
            LayoutHudCoverageThreshold = Math.Min(100, Math.Max(1, LayoutHudCoverageThreshold));
            // "cell" was the old name for the stacked single window.
            if (LayoutButtonMode == "cell") LayoutButtonMode = "stacked";
            if (LayoutButtonMode != "row" && LayoutButtonMode != "stacked"
                && LayoutButtonMode != "parallel" && LayoutButtonMode != "float")
            {
                LayoutButtonMode = "stacked";
            }
            if (LayoutButtonRow >= LayoutRows || LayoutButtonRow < 0) LayoutButtonRow = -1;
            if (LayoutButtonMode == "row" && LayoutButtonRow < 0) LayoutButtonRow = Math.Max(0, LayoutRows - 1);
            LayoutButtonCol = Math.Min(LayoutCols - 1, Math.Max(0, LayoutButtonCol));
            LayoutButtonCellRow = Math.Min(LayoutRows - 1, Math.Max(0, LayoutButtonCellRow));
            if (LayoutAssignments == null) LayoutAssignments = new List<ZoneAssignment>();

            LayoutAssignments.RemoveAll(a => a == null || string.IsNullOrEmpty(a.Dialog));

            // Shrinking the grid pulls out-of-range assignments back inside it rather than dropping
            // them. Dropping would strand the window: it would stay where we put it, with no record
            // left for "reset layout" to undo and no entry left to clear out of vanilla's store.
            foreach (var a in LayoutAssignments)
            {
                a.Col = Math.Min(LayoutCols - 1, Math.Max(0, a.Col));
                a.Row = Math.Min(LayoutRows - 1, Math.Max(0, a.Row));
                a.ColSpan = Math.Min(LayoutCols - a.Col, Math.Max(1, a.ColSpan));
                a.RowSpan = Math.Min(LayoutRows - a.Row, Math.Max(1, a.RowSpan));
            }
        }
    }
}
