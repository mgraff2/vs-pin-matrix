using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// Draw a recently-used path as marching ants: alternating bands of
        /// <see cref="TranslocatorRecentColor"/> and <see cref="TranslocatorMarkerColor"/> that
        /// crawl from the hop's origin towards its destination.
        ///
        /// Two colours rather than colour-and-gap, because a gapped line disappears into busy
        /// terrain at 2.5px. The second colour is the ordinary path colour on purpose: the recent
        /// line then reads as one of the network rather than as an unrelated object. Set both
        /// colours the same and the bands become invisible — the animation still runs, so that is
        /// a way of switching the effect off by accident. The toggle is the way to mean it.
        ///
        /// Only recent paths are dashed. Old lines stay one quad each, so the cost of this scales
        /// with the hop you care about and not with how many translocators you have ever used.
        /// </summary>
        public bool TranslocatorRecentAnts { get; set; } = true;

        /// <summary>Width of one colour band, in screen pixels. A band pair is twice this.</summary>
        public double TranslocatorAntsDashPx { get; set; } = 9;

        /// <summary>
        /// How fast the bands crawl, in screen pixels per second. Zero leaves them as static
        /// stripes, which is the honest setting for anyone who finds movement on a map distracting
        /// but still wants the hop to stand out.
        /// </summary>
        public double TranslocatorAntsSpeed { get; set; } = 16;

        /// <summary>
        /// Re-derive the game's GUI scale from the screen size when the screen size changes.
        ///
        /// THE PROBLEM THIS EXISTS FOR: nothing in the engine resizes a dialog, so a 900px window is
        /// 900px whatever the screen is - a third of a 2560-wide screen and most of a 1280-wide one.
        /// The only lever is the global GUI scale, four clicks deep in Settings. Anyone who moves
        /// between a desk monitor and a remote session at a smaller resolution re-does that by hand
        /// every single time.
        ///
        /// Off by default because it writes a *game* setting, not one of ours, and a mod quietly
        /// changing the size of your entire interface had better be something you asked for.
        /// </summary>
        public bool LayoutAutoScale { get; set; } = false;

        /// <summary>
        /// The reference pairing auto-scale derives from: a GUI scale and the screen it was chosen on.
        ///
        /// NEVER derived from the *current* scale, always from this fixed pair. Scaling the current
        /// value by a ratio compounds - bounce between two machines a few times and the size drifts
        /// away from anything you chose. From a fixed base the answer is exact and round-trips
        /// perfectly: base 1.0 at 2560x1440 gives 0.5 at 1280x720 and 1.0 again on return.
        /// Re-captured whenever the player moves the scale slider themselves, because that is them
        /// saying "this is the size I want, at this screen size". Zero means "not captured yet".
        /// </summary>
        public double LayoutBaseScale { get; set; } = 0;
        public int LayoutBaseScreenW { get; set; } = 0;
        public int LayoutBaseScreenH { get; set; } = 0;

        // ------------------------------------------------------------------ herty cups

        /// <summary>
        /// Auto-mark a Herty cup when the player places one or collects from one.
        ///
        /// Off by default, like every other auto-marker here: waypoints appearing on someone's map
        /// unasked is not a default. See <see cref="HertyCupMarkers"/> for why both triggers are
        /// player interactions rather than a scan of nearby blocks — it is the only way a
        /// client-only mod can tell your cups from everyone else's on a server.
        /// </summary>
        public bool HertyCupMarkersEnabled { get; set; } = false;

        /// <summary>
        /// Waypoint icon for cup markers. "vessel" is vanilla's little cracked pot, which is as close
        /// as the game's own icon set gets to a resin pot on a tree. Pick another from the dropdown on
        /// the Map options screen - it lists every icon the game has, drawn rather than named.
        /// </summary>
        public string HertyCupMarkerIcon { get; set; } = "vessel";

        public bool HertyCupMarkerPinned { get; set; } = false;

        /// <summary>Resin amber, far enough from the trader and translocator defaults to read apart.</summary>
        public string HertyCupMarkerColor { get; set; } = "#C98B3A";

        /// <summary>
        /// Title prefix; the tapped tree's wood is appended when it can be read from the log next to
        /// the cup, giving "Herty cup: Pine". Changing it after the fact orphans the markers already
        /// placed, exactly as the trader prefix does.
        /// </summary>
        public string HertyCupMarkerTitlePrefix { get; set; } = "Herty cup: ";

        /// <summary>
        /// How close an existing cup marker has to be to suppress a new one, in blocks.
        ///
        /// Far tighter than the trader radius, and deliberately: a trader wanders around its cart,
        /// but a cup is a block that never moves, and two cups on neighbouring faces of the same
        /// trunk are two real cups one block apart that both deserve a pin.
        /// </summary>
        public double HertyCupDedupeRadius { get; set; } = 1.5;

        /// <summary>
        /// Whether the per-specialisation trader colours are unfolded on the Map options screen.
        ///
        /// Nine colours is the longest thing on that screen and the least often changed - most
        /// players set them once, if ever. Folded away by default, because a settings screen that
        /// autosizes to its children spends its height on whatever is left open.
        /// </summary>
        public bool TraderColorsExpanded { get; set; } = false;

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
        ///   "row"      - one window stretched across the whole row it is snapped to, buttons spread evenly
        ///   "stacked"  - one window, buttons in a column, wherever it was dragged
        ///   "parallel" - one window, buttons in a row, wherever it was dragged
        ///   "float"    - one window per button, each placed independently
        /// </summary>
        public string LayoutButtonMode { get; set; } = "stacked";

        /// <summary>
        /// Starting cell for "stacked" and "parallel" modes; the buttons run from its top-left corner.
        /// Defaults to the upper right of the default 20x10 grid, which is roughly where the
        /// floating stack used to sit — a fresh install should not find its buttons in the map's
        /// top-left corner. Clamped into range on smaller grids.
        /// </summary>


        /// <summary>
        /// Remembered zones, keyed by composer DialogName. Stored as cell indices, never as
        /// coordinates, so changing resolution or GUI scale re-derives the layout rather than
        /// stranding windows at coordinates that meant something on other hardware.
        /// </summary>
        public List<ZoneAssignment> LayoutAssignments { get; set; } = new List<ZoneAssignment>();

        /// <summary>
        /// Saved filters that can be switched on and off from the map screen. Global across worlds
        /// — a set is a question ("pins whose name has 'resin'"), and that question means the same
        /// thing everywhere; which pins are currently switched off is per savegame and lives in
        /// <see cref="WaypointVisibility"/>'s own file.
        ///
        /// They surface as the pin-set panel down the right of the world map — a column of on/off
        /// rows in the spirit of the map's own Terrain / Waypoints toggles — and on the editor's
        /// Pin sets screen. The panel appears only when at least one set exists, so a player who
        /// never makes one never sees it.
        /// </summary>
        public List<PinSet> PinSets { get; set; } = new List<PinSet>();

        /// <summary>
        /// Whether the map's pin-set panel is open, or shut down to its 16px pull.
        ///
        /// Shut by default, and that is the considered choice rather than caution. The world map is
        /// CenterMiddle at 1200x800, so the free space either side is only whatever the screen has
        /// left over — at a higher GUI scale or in a window there is very little, and a permanently
        /// open 150-320px column spends all of it on a list most players read twice a session. The
        /// pull is one click and the answer is remembered, so anyone who wants it open pays that
        /// click once per install rather than once per map open.
        /// </summary>
        public bool PinSetPanelExpanded { get; set; } = false;

        /// <summary>
        /// Ceiling on saved sets. Not a technical limit — the panel pages, so it would cope — but a
        /// list this long has stopped being a set of filters and become a second waypoint list.
        /// </summary>
        public const int MaxPinSets = 24;

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
            // A band narrower than the line is thicker than it is long and stops reading as a band;
            // wider than 40px and a typical on-screen hop holds fewer than two of them.
            TranslocatorAntsDashPx = Math.Min(40, Math.Max(3, TranslocatorAntsDashPx));
            TranslocatorAntsSpeed = Math.Min(200, Math.Max(0, TranslocatorAntsSpeed));
            HertyCupDedupeRadius = Math.Min(64, Math.Max(0, HertyCupDedupeRadius));
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
            if (LayoutAssignments == null) LayoutAssignments = new List<ZoneAssignment>();

            if (PinSets == null) PinSets = new List<PinSet>();
            // A set with no name has nothing to put on a button, and one with no id cannot own a
            // window or be edited; both mean a hand-edited or half-written config, so drop them
            // rather than carrying a set nothing can address.
            PinSets.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Name));
            var seenSetIds = new HashSet<string>();
            foreach (var set in PinSets)
            {
                if (set.Icons == null) set.Icons = new List<string>();
                if (set.Colors == null) set.Colors = new List<string>();
                if (set.Search == null) set.Search = "";
                set.Name = set.Name.Trim();
                if (set.Name.Length > 40) set.Name = set.Name.Substring(0, 40);
                // Duplicate or missing ids would give two sets the same button window and the same
                // zone assignment, so the later one is re-minted rather than dropped.
                if (string.IsNullOrEmpty(set.Id) || !seenSetIds.Add(set.Id))
                {
                    set.Id = PinSet.NewId(PinSets.Where(x => !string.IsNullOrEmpty(x.Id)));
                    seenSetIds.Add(set.Id);
                }
            }
            if (PinSets.Count > MaxPinSets) PinSets.RemoveRange(MaxPinSets, PinSets.Count - MaxPinSets);

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
