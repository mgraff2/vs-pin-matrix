using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace PinMatrix
{
    public enum PmScreen
    {
        Matrix, Confirm, SetColor, SetIcon, Rename, NewPin, Bin, ImportExport, Share, Layout, MapOptions,
        PinSets, EditSet, Tools
    }

    public class PendingBulk
    {
        public string Title;

        /// <summary>
        /// Colour to paint as a swatch beside the title, or null. A confirmation that names a colour
        /// only in hex asks the player to decode it before agreeing to it, which is the one moment
        /// they most need to see what they are about to get.
        /// </summary>
        public int? TitleSwatch;

        public string Warning;
        public string[] Lines;

        /// <summary>
        /// One chip per line, painted before its text, or null for a list that needs none. Used by
        /// the recolour preview to show each pin's *current* colour: the target is the same on every
        /// row and is already up in the title, so the per-row fact worth showing is what is being
        /// replaced. Shorter than Lines is fine — a row past the end simply gets no chip.
        /// </summary>
        public int[] LineSwatches;
        public string ConfirmText;
        public Action Execute;
        public PmScreen ReturnScreen = PmScreen.Matrix;
    }

    /// <summary>Which side of the hidden/visible split the table shows. Cycled by one button.</summary>
    public enum VisFilter { All = 0, VisibleOnly = 1, HiddenOnly = 2 }

    /// <summary>A set of pins identical in every displayed column — i.e. duplicates of each other.</summary>
    public class DupGroup
    {
        public string Sig;
        public readonly List<PinRow> Rows = new List<PinRow>();
    }

    /// <summary>
    /// One drawn line of the table: either a duplicate-group header (<see cref="Group"/> set) or a
    /// single pin. Paging, hit-testing and drawing all run over these, so a collapsed group costs
    /// exactly one line however many copies it holds.
    /// </summary>
    public struct GridLine
    {
        public DupGroup Group;
        public PinRow Row;
    }

    public partial class GuiDialogPinMatrix : GuiDialog
    {
        // ---- layout constants (unscaled) ----
        const double DW = 896;          // content width
        const double RowH = 25;
        const double ConfRowH = 22;

        // table columns: x, width. The Vis column's 36px came out of the Name column — the row is
        // packed to the pixel and the Actions column has no slack left (4 mini buttons at 46).
        const double ColSelX = 4, ColSelW = 26;
        const double ColNameX = 34, ColNameW = 204;
        const double ColIconX = 242, ColIconW = 78;
        const double ColColorX = 324, ColColorW = 60;
        const double ColXX = 388, ColXW = 60;
        const double ColYX = 452, ColYW = 46;
        const double ColZX = 502, ColZW = 60;
        const double ColDistX = 566, ColDistW = 62;
        const double ColVisX = 632, ColVisW = 34;
        const double ColPinX = 670, ColPinW = 34;
        const double ColActX = 708, ColActW = 186;

        readonly PinMatrixConfig config;
        readonly WaypointService svc;
        readonly BatchEngine batch;
        readonly RecycleBin bin;
        readonly WaypointVisibility visibility;

        /// <summary>Window-layout system; set by the mod system after construction.</summary>
        public LayoutManager Layout;
        /// <summary>Trader auto-marker service; set by the mod system after construction.</summary>
        public TraderMarkers Traders;
        public HertyCupMarkers HertyCups;
        /// <summary>Translocator path service; set by the mod system after construction.</summary>
        public TranslocatorPaths TlPaths;
        public PinMatrixModSystem ModSystem;

        PmScreen screen = PmScreen.Matrix;
        string notice = "";
        long tickListenerId;
        string lastSignature = "";

        // data
        List<PinRow> allRows = new List<PinRow>();
        List<PinRow> viewRows = new List<PinRow>();
        List<GridLine> gridLines = new List<GridLine>();
        Vec3d playerPos = new Vec3d();

        // duplicate grouping
        bool groupDuplicates;
        readonly HashSet<string> expandedGroups = new HashSet<string>();
        int shownDuplicates;    // extra copies inside the current view, i.e. what grouping folds away

        // filters
        string searchText = "";
        readonly HashSet<string> iconFilter = new HashSet<string>();
        readonly HashSet<string> colorFilter = new HashSet<string>();   // "#rrggbb" values
        bool pinnedOnly;
        double radius;                                                   // <= 0 means off
        VisFilter visFilter = VisFilter.All;                             // hidden pins: show all / only visible / only hidden

        // sort: 0 name, 1 icon, 2 color, 3 x, 4 y, 5 z, 6 dist, 7 pinned, 8 visible
        int sortCol = 0;
        bool sortAsc = true;

        // selection & paging
        readonly HashSet<string> selectedKeys = new HashSet<string>();
        int anchorRow = -1;
        int page;
        long lastClickMs;
        int lastClickRow = -1;

        // bulk op state
        PendingBulk pending;
        int confPage;
        UndoState undo;

        ElementBounds tableBounds;

        static readonly double[] IconWhite = { 1, 1, 1, 1 };
        static readonly double[] IconHiddenRow = { 1, 1, 1, 0.7 };   // matches the dimmed text of a hidden row
        readonly HashSet<string> probedIcons = new HashSet<string>();
        readonly HashSet<string> brokenIcons = new HashSet<string>();
        bool iconAssetsLoaded;

        // icon filter dropdown - values are the icons actually in use, labels carry live counts.
        // It replaced a 24-per-row clickable grid of every icon the game has, which cost two rows
        // of the screen to answer a question three of its cells were ever asked.
        string[] filterIconCodes = { "circle" };
        bool iconLabelsStale;

        // colour filter dropdown — values are the colours actually in use, labels carry live counts
        string[] filterColorHexes = { "#ffffff" };
        bool colorLabelsStale;

        int PageSize => config.RowsPerPage;
        int MaxPage => Math.Max(0, (gridLines.Count - 1) / PageSize);

        public GuiDialogPinMatrix(ICoreClientAPI capi, PinMatrixConfig config, WaypointService svc, BatchEngine batch, RecycleBin bin, WaypointVisibility visibility)
            : base(capi)
        {
            this.config = config;
            this.svc = svc;
            this.batch = batch;
            this.bin = bin;
            this.visibility = visibility;
            tickListenerId = capi.World.RegisterGameTickListener(OnPollTick, 1000);
        }

        public override string ToggleKeyCombinationCode => "pinmatrix";
        public override bool PrefersUngrabbedMouse => true;

        // Above the full world map (0.11) so the matrix is never hidden behind it
        public override double DrawOrder => 0.2;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // The hotkey's own character event arrives right after opening; without this it lands in the search box
            ignoreNextKeyPress = true;
            notice = "";
            screen = PmScreen.Matrix;
            pending = null;

            // Both tag tables are wiped on leaving a world, so both are re-asserted per open
            ColorSwatchComponent.EnsureTagRegistered();
            IconGlyphComponent.EnsureTagRegistered();
            EnsureIconAssetsLoaded();
            // re-decide per open: the assets an earlier open found missing may have loaded since
            probedIcons.Clear();
            brokenIcons.Clear();

            RefreshData();
            // Nothing synced: ask for it and let the poll tick pick the reply up a moment later
            if (allRows.Count == 0) svc.RequestResync();
            Recompose();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (tickListenerId != 0)
            {
                capi.World.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }

        // ------------------------------------------------------------------ data

        void RefreshData()
        {
            lastSignature = svc.Signature();
            playerPos = capi.World.Player.Entity.Pos.XYZ;

            var own = svc.Own;
            allRows = new List<PinRow>(own.Count);
            for (int i = 0; i < own.Count; i++)
            {
                var wp = own[i];
                allRows.Add(new PinRow { Wp = wp, Index = i, Key = PinKey.KeyOf(wp), Dist = wp.Position.DistanceTo(playerPos) });
            }

            selectedKeys.IntersectWith(allRows.Select(r => r.Key));
            // Waypoints arrive as one complete list, so anything hidden that isn't in it is gone for
            // good and its key can be dropped (guarded inside against pruning off an empty sync).
            visibility.PruneTo(new HashSet<string>(allRows.Select(r => r.Key)));
            anchorRow = -1;
            RebuildColorFilterValues();
            RebuildIconFilterValues();
            ApplyView();
        }

        // ------------------------------------------------------------------ colour filter dropdown

        /// <summary>
        /// The dropdown lists only colours some waypoint actually uses — the full vanilla palette
        /// plus every custom colour is an unreadable wall of hex. Recomputed whenever the waypoint
        /// list changes, dropping any filtered colour that no longer exists.
        ///
        /// ORDERED BY HOW MANY PINS USE IT, most first, because the colour you want is nearly always
        /// one of the handful you use constantly. Counted over <em>every</em> pin rather than over
        /// the current filter: the label counts move with the other filters, and if the order moved
        /// with them too, entries would swap places under the cursor every time you typed in the
        /// search box. Ties fall back to the hue walk, which keeps near-identical colours adjacent
        /// within a tie group — lexicographic hex order scatters them, which is what makes a long
        /// list hard to scan.
        /// </summary>
        void RebuildColorFilterValues()
        {
            var totals = new Dictionary<string, int>();
            foreach (var r in allRows)
            {
                string h = WpCommands.ColorHex(r.Wp.Color);
                totals.TryGetValue(h, out int n);
                totals[h] = n + 1;
            }

            var hexes = totals.Keys
                .OrderByDescending(h => totals[h])
                .ThenBy(HueKey)
                .ThenBy(h => h, StringComparer.Ordinal)
                .ToArray();

            // A list menu with no entries composes a zero-sized surface, so keep one placeholder
            filterColorHexes = hexes.Length > 0 ? hexes : new[] { "#ffffff" };
            colorFilter.IntersectWith(filterColorHexes);
        }

        /// <summary>
        /// Sort key that walks the colour wheel, greys first — lexicographic hex order scatters
        /// near-identical colours all over the list, which is exactly what makes it hard to scan.
        /// </summary>
        static double HueKey(string hex)
        {
            if (!int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return -2;
            double r = ((v >> 16) & 0xFF) / 255.0, g = ((v >> 8) & 0xFF) / 255.0, b = (v & 0xFF) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double chroma = max - min;
            if (chroma < 0.02) return -1 + max;                 // greys, dark to light, ahead of every hue
            if (max == r) return ((g - b) / chroma + 6) % 6;    // 0..6 round the wheel
            if (max == g) return (b - r) / chroma + 2;
            return (r - g) / chroma + 4;
        }

        /// <summary>
        /// One dropdown label per colour: a painted swatch, the hex, and how many pins that colour
        /// would show under the *other* filters.
        /// </summary>
        string[] ColorFilterLabels()
        {
            var counts = new Dictionary<string, int>();
            foreach (var r in FilteredExceptColor())
            {
                string h = WpCommands.ColorHex(r.Wp.Color);
                counts.TryGetValue(h, out int n);
                counts[h] = n + 1;
            }

            return filterColorHexes
                .Select(h => $"<{ColorSwatchComponent.TagName} color=\"{h}\"/> {h} ({(counts.TryGetValue(h, out int n) ? n : 0)})")
                .ToArray();
        }

        /// <summary>
        /// Repaints the dropdown labels so the counts track the other filters. Labels are baked
        /// into a texture at compose time, so they need an explicit rebuild; only the label text
        /// changes (the value list is fixed to the colours in use), so the selection survives.
        /// </summary>
        void RefreshColorFilterLabels()
        {
            var dd = SingleComposer?.GetDropDown("colorfilter");
            if (dd == null) return;

            // Recomposing an expanded menu resets its scroll position out from under the cursor
            if (dd.listMenu.IsOpened) { colorLabelsStale = true; return; }

            colorLabelsStale = false;
            dd.SetList(filterColorHexes, ColorFilterLabels());
            dd.SetSelectedValue(colorFilter.ToArray());   // re-ticks the switches and repaints the collapsed label
        }

        // ------------------------------------------------------------------ icon filter dropdown

        /// <summary>
        /// The dropdown lists only icons some waypoint actually uses, for the same reason the colour
        /// one does: the full set is 36 entries, and a player's pins use a handful. Recomputed
        /// whenever the waypoint list changes, dropping any filtered icon that no longer exists.
        ///
        /// Undrawable icons stay in the list. Their entry loses its glyph and keeps its code, which
        /// is strictly better than dropping the entry: the pins using that icon still exist and
        /// still need a way to be selected.
        /// </summary>
        /// <summary>
        /// Same ordering rule as the colours: most-used first, counted over every pin so the list
        /// does not reshuffle as the other filters change. Alphabetical within a tie.
        /// </summary>
        void RebuildIconFilterValues()
        {
            var totals = new Dictionary<string, int>();
            foreach (var r in allRows)
            {
                string c = WpCommands.SafeIcon(r.Wp.Icon);
                totals.TryGetValue(c, out int n);
                totals[c] = n + 1;
            }

            var codes = totals.Keys
                .OrderByDescending(c => totals[c])
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // A list menu with no entries composes a zero-sized surface, so keep one placeholder
            filterIconCodes = codes.Length > 0 ? codes : new[] { "circle" };
            iconFilter.IntersectWith(filterIconCodes);
        }

        /// <summary>
        /// One label per icon: the glyph, its code, and how many pins it would show under the
        /// *other* filters - the same "how many more would this add" count the colour dropdown uses.
        /// </summary>
        string[] IconFilterLabels()
        {
            var counts = new Dictionary<string, int>();
            foreach (var r in FilteredExceptIcon())
            {
                string code = WpCommands.SafeIcon(r.Wp.Icon);
                counts.TryGetValue(code, out int n);
                counts[code] = n + 1;
            }

            return filterIconCodes
                .Select(code =>
                {
                    string glyph = IconDrawable(code) ? $"<{IconGlyphComponent.TagName} code=\"{code}\"/> " : "";
                    return $"{glyph}{code} ({(counts.TryGetValue(code, out int n) ? n : 0)})";
                })
                .ToArray();
        }

        /// <summary>Repaints the icon labels so their counts track the other filters. See <see cref="RefreshColorFilterLabels"/>.</summary>
        void RefreshIconFilterLabels()
        {
            var dd = SingleComposer?.GetDropDown("iconfilter");
            if (dd == null) return;

            if (dd.listMenu.IsOpened) { iconLabelsStale = true; return; }

            iconLabelsStale = false;
            dd.SetList(filterIconCodes, IconFilterLabels());
            dd.SetSelectedValue(iconFilter.ToArray());
        }

        void OnIconFilterChanged(string code, bool selected)
        {
            if (selected) iconFilter.Add(code); else iconFilter.Remove(code);
            ApplyView();
            UpdateMatrixDynamic();
        }

        bool IsHidden(PinRow r) => visibility.IsHidden(r.Key);

        /// <summary>
        /// Whether the distance tools should look past hidden pins. "Hidden" means "not now", so
        /// walking the nearest pins or drawing a radius ring around the player skips them — unless
        /// the table is deliberately showing the hidden ones, where skipping them would leave the
        /// distance tools with nothing to work on.
        /// </summary>
        bool DistanceToolsSkipHidden => visFilter != VisFilter.HiddenOnly;

        IEnumerable<PinRow> Filtered(bool useColor, bool useRadius, bool useIcon = true)
        {
            IEnumerable<PinRow> q = allRows;
            if (searchText.Length > 0) q = q.Where(r => (r.Wp.Title ?? "").IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (useIcon && iconFilter.Count > 0) q = q.Where(r => iconFilter.Contains(WpCommands.SafeIcon(r.Wp.Icon)));
            if (useColor && colorFilter.Count > 0) q = q.Where(r => colorFilter.Contains(WpCommands.ColorHex(r.Wp.Color)));
            if (pinnedOnly) q = q.Where(r => r.Wp.Pinned);
            if (visFilter == VisFilter.VisibleOnly) q = q.Where(r => !IsHidden(r));
            else if (visFilter == VisFilter.HiddenOnly) q = q.Where(IsHidden);
            if (useRadius && radius > 0)
            {
                q = DistanceToolsSkipHidden
                    ? q.Where(r => r.Dist <= radius && !IsHidden(r))
                    : q.Where(r => r.Dist <= radius);
            }
            return q;
        }

        /// <summary>All filters except the radius — also the candidate set for "Next pin".</summary>
        IEnumerable<PinRow> FilteredExceptRadius()
        {
            var q = Filtered(useColor: true, useRadius: false);
            return DistanceToolsSkipHidden ? q.Where(r => !IsHidden(r)) : q;
        }

        /// <summary>
        /// All filters except the colour filter — the base set the colour dropdown counts against.
        /// Excluding the colour filter itself is what makes the counts useful: they answer "how many
        /// more pins would this colour add", so picking one colour doesn't zero out all the others.
        /// </summary>
        IEnumerable<PinRow> FilteredExceptColor() => Filtered(useColor: false, useRadius: true);

        /// <summary>The base set the icon dropdown counts against - see <see cref="FilteredExceptColor"/>.</summary>
        IEnumerable<PinRow> FilteredExceptIcon() => Filtered(useColor: true, useRadius: true, useIcon: false);

        void ApplyView()
        {
            IEnumerable<PinRow> q = Filtered(useColor: true, useRadius: true);

            switch (sortCol)
            {
                case 0: q = Sorted(q, r => r.Wp.Title ?? "", StringComparer.OrdinalIgnoreCase); break;
                case 1: q = Sorted(q, r => WpCommands.SafeIcon(r.Wp.Icon), StringComparer.OrdinalIgnoreCase); break;
                case 2: q = Sorted(q, r => r.Wp.Color & 0xFFFFFF, Comparer<int>.Default); break;
                case 3: q = Sorted(q, r => r.Wp.Position.X, Comparer<double>.Default); break;
                case 4: q = Sorted(q, r => r.Wp.Position.Y, Comparer<double>.Default); break;
                case 5: q = Sorted(q, r => r.Wp.Position.Z, Comparer<double>.Default); break;
                case 6: q = Sorted(q, r => r.Dist, Comparer<double>.Default); break;
                case 7: q = Sorted(q, r => r.Wp.Pinned ? 1 : 0, Comparer<int>.Default); break;
                case 8: q = Sorted(q, r => IsHidden(r) ? 1 : 0, Comparer<int>.Default); break;
            }

            viewRows = q.ToList();
            BuildGridLines();
            page = Math.Min(page, MaxPage);
        }

        // ------------------------------------------------------------------ duplicate grouping

        /// <summary>
        /// Identity across every displayed column — title, icon, colour, pinned and position. Two
        /// pins sharing a signature are indistinguishable in the table, which is exactly what makes
        /// them duplicates. Distance is excluded because it is derived from the position, and the
        /// Actions column holds no data.
        /// </summary>
        /// <remarks>
        /// Position is compared at exactly the precision the table <em>shows</em> — whole blocks,
        /// spawn-relative X/Z, straight through <see cref="FmtCoord"/>. It used to compare to two
        /// decimals, which quietly broke the promise in the summary above: two rows reading
        /// identically in every visible column still would not group, because their positions
        /// differed by a fraction of a block no player could see. Comparing more finely than the
        /// player can look is how "these are obviously the same pin" became "no duplicates found".
        /// </remarks>
        string DupSignature(Waypoint wp)
            => string.Join("|",
                WpCommands.SafeTitle(wp.Title),
                WpCommands.SafeIcon(wp.Icon),
                WpCommands.ColorHex(wp.Color),
                wp.Pinned ? "1" : "0",
                FmtCoord(svc.RelX(wp.Position.X)),
                FmtCoord(wp.Position.Y),
                FmtCoord(svc.RelZ(wp.Position.Z)));

        /// <summary>Groups <paramref name="rows"/> by duplicate signature, keeping first-seen order.</summary>
        List<DupGroup> GroupByDuplicate(IEnumerable<PinRow> rows)
        {
            var bySig = new Dictionary<string, DupGroup>();
            var ordered = new List<DupGroup>();
            foreach (var r in rows)
            {
                string sig = DupSignature(r.Wp);
                if (!bySig.TryGetValue(sig, out var g))
                {
                    bySig[sig] = g = new DupGroup { Sig = sig };
                    ordered.Add(g);
                }
                g.Rows.Add(r);
            }
            return ordered;
        }

        /// <summary>
        /// Flattens the sorted view into drawable lines. With grouping off that is one line per pin;
        /// with it on, each duplicate set becomes a single header line (expandable to its copies)
        /// while unique pins still draw as ordinary rows — otherwise every row would grow a header.
        /// </summary>
        void BuildGridLines()
        {
            gridLines = new List<GridLine>(viewRows.Count);
            shownDuplicates = 0;

            if (!groupDuplicates)
            {
                foreach (var r in viewRows) gridLines.Add(new GridLine { Row = r });
                return;
            }

            foreach (var g in GroupByDuplicate(viewRows))
            {
                if (g.Rows.Count == 1)
                {
                    gridLines.Add(new GridLine { Row = g.Rows[0] });
                    continue;
                }

                shownDuplicates += g.Rows.Count - 1;
                gridLines.Add(new GridLine { Group = g });
                if (expandedGroups.Contains(g.Sig))
                {
                    foreach (var r in g.Rows) gridLines.Add(new GridLine { Row = r });
                }
            }
        }

        /// <summary>Extra copies across the whole waypoint list — what "Fix duplicates" would remove.</summary>
        int DuplicateCount() => GroupByDuplicate(allRows).Sum(g => g.Rows.Count - 1);

        /// <summary>
        /// Groups pins that sit in the same place, whatever they are called — the case the strict
        /// rule above cannot serve, where one trader carries a hand-placed pin, another tool's
        /// marker and ours, all pointing at the same cart under three different names.
        ///
        /// Two deliberate restraints, because unlike duplicate cleanup this can delete pins that
        /// genuinely differ:
        ///
        /// - **Not transitive.** Every member is within tolerance of the row that opened the
        ///   cluster, never merely of its nearest neighbour. Chaining would let a line of pins a few
        ///   blocks apart collapse into one "spot" spanning half a village.
        /// - **Never two named specialisations.** Traders really do stand together in camps, but
        ///   never two of the same kind — so a pin naming a different specialisation from the seed
        ///   is a different trader and starts its own cluster however close it is. Pins whose
        ///   specialisation cannot be read stay eligible, which is what lets the unnamed strays
        ///   join the trader they point at.
        /// </summary>
        List<DupGroup> GroupBySpot(IEnumerable<PinRow> rows)
        {
            double r = Math.Max(0, config.SameSpotRadius);
            var list = rows as List<PinRow> ?? rows.ToList();
            var taken = new bool[list.Count];
            var groups = new List<DupGroup>();

            for (int i = 0; i < list.Count; i++)
            {
                if (taken[i]) continue;
                taken[i] = true;

                var g = new DupGroup { Sig = "spot:" + list[i].Key };
                g.Rows.Add(list[i]);

                var seed = list[i].Wp.Position;
                string seedRole = SpotRole(list[i]);

                for (int j = i + 1; j < list.Count; j++)
                {
                    if (taken[j]) continue;

                    string role = SpotRole(list[j]);
                    if (seedRole != null && role != null && seedRole != role) continue;

                    var p = list[j].Wp.Position;
                    if (Math.Abs(p.X - seed.X) > r) continue;
                    if (Math.Abs(p.Y - seed.Y) > r) continue;
                    if (Math.Abs(p.Z - seed.Z) > r) continue;

                    taken[j] = true;
                    g.Rows.Add(list[j]);
                }
                groups.Add(g);
            }
            return groups;
        }

        /// <summary>The trade specialisation this pin's title names, or null if it names none.</summary>
        string SpotRole(PinRow r) =>
            TraderMarkers.RoleFromTitle(r.Wp.Title, config.TraderMarkerTitlePrefix);

        int SameSpotCount() => GroupBySpot(allRows).Sum(g => g.Rows.Count - 1);

        IEnumerable<PinRow> Sorted<TKey>(IEnumerable<PinRow> q, System.Func<PinRow, TKey> key, IComparer<TKey> cmp)
            => sortAsc ? q.OrderBy(key, cmp) : q.OrderByDescending(key, cmp);

        List<PinRow> SelectedAllRows() => allRows.Where(r => selectedKeys.Contains(r.Key)).ToList();

        void OnPollTick(float dt)
        {
            if (!IsOpened() || batch.Busy) return;

            // deferred from a filter change made while the dropdown was expanded
            if (screen == PmScreen.Matrix && colorLabelsStale) RefreshColorFilterLabels();
            if (screen == PmScreen.Matrix && iconLabelsStale) RefreshIconFilterLabels();

            string sig;
            try { sig = svc.Signature(); }
            catch (Exception) { return; }

            if (sig == lastSignature) return;

            if (screen == PmScreen.Confirm)
            {
                pending = null;
                screen = PmScreen.Matrix;
                notice = "Waypoint list changed — pending operation was cancelled.";
            }
            RefreshData();
            Recompose();
        }

        // ------------------------------------------------------------------ composing

        void Recompose()
        {
            if (screen == PmScreen.Confirm && pending == null) screen = PmScreen.Matrix;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("pinmatrix-" + screen, dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(TitleFor(), OnTitleBarClose)
                .BeginChildElements(bgBounds);

            switch (screen)
            {
                case PmScreen.Matrix: ComposeMatrix(composer); break;
                case PmScreen.Confirm: ComposeConfirm(composer); break;
                case PmScreen.SetColor: ComposeSetColor(composer); break;
                case PmScreen.SetIcon: ComposeSetIcon(composer); break;
                case PmScreen.Rename: ComposeRename(composer); break;
                case PmScreen.NewPin: ComposeNewPin(composer); break;
                case PmScreen.Bin: ComposeBin(composer); break;
                case PmScreen.ImportExport: ComposeImportExport(composer); break;
                case PmScreen.Share: ComposeShare(composer); break;
                case PmScreen.Layout: ComposeLayout(composer); break;
                case PmScreen.MapOptions: ComposeMapOptions(composer); break;
                case PmScreen.PinSets: ComposePinSets(composer); break;
                case PmScreen.EditSet: ComposeEditSet(composer); break;
                case PmScreen.Tools: ComposeTools(composer); break;
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null)
            {
                // Deferred: the old composer may still be mid-iteration in the event loop that triggered this recompose
                capi.World.RegisterCallback(_ => replaced.Dispose(), 250);
            }

            if (screen == PmScreen.Matrix) RestoreMatrixState();
            if (screen == PmScreen.NewPin) RestoreNewPinState();
            if (screen == PmScreen.Rename) RestoreRenameState();
            if (screen == PmScreen.ImportExport) RestoreImportExportState();
            if (screen == PmScreen.Layout) RestoreLayoutState();
            if (screen == PmScreen.MapOptions) RestoreMapOptionsState();
            if (screen == PmScreen.EditSet) RestoreEditSetState();
        }

        string TitleFor()
        {
            switch (screen)
            {
                case PmScreen.Confirm: return "Pin Matrix — Confirm";
                case PmScreen.SetColor: return "Pin Matrix — Set color";
                case PmScreen.SetIcon: return "Pin Matrix — Set icon";
                case PmScreen.Rename: return "Pin Matrix — Rename";
                case PmScreen.NewPin: return newPinIsMove ? "Pin Matrix — Move pin" : "Pin Matrix — New pin";
                case PmScreen.Bin: return "Pin Matrix — Recycle bin";
                case PmScreen.ImportExport: return "Pin Matrix — Export / Import";
                case PmScreen.Share: return "Pin Matrix — Share pin";
                case PmScreen.Layout: return "Pin Matrix — Map windows layout";
                case PmScreen.MapOptions: return "Pin Matrix — Map options";
                case PmScreen.PinSets: return "Pin Matrix — Pin sets";
                case PmScreen.EditSet: return editingIsNew ? "Pin Matrix — New pin set" : "Pin Matrix — Edit pin set";
                case PmScreen.Tools: return "Pin Matrix — Tools";
                default: return "Pin Matrix — Waypoint manager";
            }
        }

        void OnTitleBarClose()
        {
            if (screen == PmScreen.Matrix) TryClose();
            else GoBack();
        }

        /// <summary>
        /// Where "back" leads from the screen we are on.
        ///
        /// Every screen is opened from the matrix and returns to it — except Map windows layout,
        /// whose only entry point is the "Layout Options" button on the map screen. Returning that
        /// one to the matrix drops the player into a window they never came from and buries the map
        /// they were arranging behind it.
        /// </summary>
        void GoBack()
        {
            if (screen == PmScreen.Layout) OnBackToMap();
            // The editor was opened from the sets list, so closing it lands back there rather than
            // skipping a level - the same rule the Layout screen follows for its own entry point.
            else if (screen == PmScreen.EditSet) { editingSet = null; OpenPinSets(); }
            else BackToMatrix();
        }

        void BackToMatrix()
        {
            pending = null;
            screen = PmScreen.Matrix;
            Recompose();
        }

        static ElementBounds EB(double x, double y, double w, double h) => ElementBounds.Fixed(x, y, w, h);

        // ------------------------------------------------------------------ matrix screen

        void ComposeMatrix(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            double y = 38;

            // Filter row one - the four questions worth asking about a pin: what is it called, what
            // colour is it, what icon is it, is it pinned. The colour and icon dropdowns both list
            // only values some pin actually uses and carry live counts, so the two of them together
            // are also the answer to "what have I got".
            //
            // Widths are load-bearing. GuiElementListMenu sizes the expanded list to the widest
            // entry but forgets to add the multi-select checkbox column it then shifts every entry
            // by, so the tail of each label - the count - is clipped unless the element's own width
            // already covers glyph + text + count + that offset. -1 preselects nothing: index 0
            // would tick the first entry's checkbox without actually filtering on it.
            //
            // Both dropdowns are LABELLED, and that is not decoration: a multi-select dropdown with
            // nothing selected renders completely blank (GuiElementDropDown builds its closed text
            // from the selected indices and there is no placeholder to set), so unlabelled they read
            // as two empty boxes that do not say what they would filter. The words match the Pin sets
            // screen exactly, because it is the same control on the same values.
            c.AddStaticText("Search", font, EB(4, y + 4, 52, 25));
            c.AddTextInput(EB(58, y, 124, 28), OnSearchChanged, font, "search");
            c.AddStaticText("Colours", font, EB(190, y + 4, 68, 25));
            c.AddMultiSelectDropDown(filterColorHexes, ColorFilterLabels(), -1, OnColorFilterChanged, EB(262, y, 200, 28), "colorfilter");
            c.AddStaticText("Icons", font, EB(470, y + 4, 50, 25));
            c.AddMultiSelectDropDown(filterIconCodes, IconFilterLabels(), -1, OnIconFilterChanged, EB(524, y, 170, 28), "iconfilter");
            c.AddSwitch(OnPinnedOnlyToggled, EB(702, y, 28, 28), "pinnedonly", 25, 3);
            c.AddStaticText("Pinned only", font, EB(734, y + 4, 110, 25));

            // Filter row two - distance, then what to do with a filter once you have one.
            y += 34;
            c.AddStaticText("Within", font, EB(4, y + 4, 52, 25));
            c.AddNumberInput(EB(58, y, 60, 28), OnRadiusChanged, font, "radius");
            c.AddSlider(OnRadiusSlider, EB(124, y + 4, 138, 20), "radiusslider");
            c.AddSmallButton("Next pin", OnRadiusNextPin, EB(268, y, 81, 28), EnumButtonStyle.Small);
            c.AddSmallButton("Save as set...", OnSaveFilterAsSet, EB(358, y, 130, 28), EnumButtonStyle.Small);
            c.AddSmallButton("Clear filters", OnClearFilters, EB(494, y, 116, 28), EnumButtonStyle.Small);
            // Moved down from row one to make room for the two dropdown labels. It belongs here
            // anyway: this row is the filter's own controls, and hidden-vs-visible is one of them.
            c.AddSmallButton(VisFilterLabel(), OnVisFilterClicked, EB(618, y, 148, 28), EnumButtonStyle.Small);

            // selection row
            y += 34;
            c.AddSmallButton("Select all filtered", OnSelectAllFiltered, EB(4, y, 148, 26));
            c.AddSmallButton("Clear selection", OnClearSelection, EB(158, y, 132, 26));
            // Rule of thumb for every label on this dialog: a static text needs ~9.5 unscaled px per
            // character plus padding, and one that comes up short does not ellipsize - it wraps to a
            // second line and overruns whatever is drawn below it. Round up.
            c.AddSwitch(OnGroupDuplicatesToggled, EB(300, y - 1, 26, 26), "groupdupes", 23, 3);
            c.AddStaticText("Group duplicates", font, EB(332, y + 4, 160, 24));
            c.AddDynamicText(StatusText(), font.Clone().WithOrientation(EnumTextOrientation.Right), EB(500, y + 4, DW - 500, 24), "status");

            // header row (sort buttons)
            y += 34;
            AddHeaderButton(c, "Name", 0, ColNameX, ColNameW, y);
            AddHeaderButton(c, "Icon", 1, ColIconX, ColIconW, y);
            AddHeaderButton(c, "Color", 2, ColColorX, ColColorW, y);
            AddHeaderButton(c, "X", 3, ColXX, ColXW, y);
            AddHeaderButton(c, "Y", 4, ColYX, ColYW, y);
            AddHeaderButton(c, "Z", 5, ColZX, ColZW, y);
            AddHeaderButton(c, "Dist", 6, ColDistX, ColDistW, y);
            AddHeaderButton(c, "Vis", 8, ColVisX, ColVisW, y);
            AddHeaderButton(c, "Pin", 7, ColPinX, ColPinW, y);
            c.AddStaticText("Actions", font, EB(ColActX + 4, y + 3, ColActW - 8, 24));

            // table
            y += 30;
            double tableH = PageSize * RowH;
            c.AddInset(EB(2, y - 2, DW + 4, tableH + 4), 3);
            tableBounds = EB(4, y, DW, tableH);
            c.AddDynamicCustomDraw(tableBounds, DrawTable, "table");

            // pagination + notice
            y += tableH + 8;
            // Ends outermost, then the ten-page jumps, then single steps: the further from the
            // middle a button is, the further it moves you.
            c.AddSmallButton("|<", OnFirstPage, EB(4, y, 40, 26));
            c.AddSmallButton("<<", OnFirstJump, EB(48, y, 40, 26));
            c.AddSmallButton("< Prev", OnPrevPage, EB(92, y, 78, 26));
            c.AddDynamicText(PageText(), font.Clone().WithOrientation(EnumTextOrientation.Center), EB(174, y + 4, 110, 24), "pageinfo");
            c.AddSmallButton("Next >", OnNextPage, EB(288, y, 78, 26));
            c.AddSmallButton(">>", OnLastJump, EB(370, y, 40, 26));
            c.AddSmallButton(">|", OnLastPage, EB(414, y, 40, 26));

            var tipFont = CairoFont.WhiteDetailText();
            c.AddHoverText("Back to the first page.", tipFont, 200, EB(4, y, 40, 26).FlatCopy(), "tipfirst");
            c.AddHoverText($"Jump {PageJump} pages. Stops at the first page rather than running past it.",
                tipFont, 260, EB(48, y, 40, 26).FlatCopy(), "tipjumpback");
            c.AddHoverText($"Jump {PageJump} pages. Stops at the last page rather than running past it.",
                tipFont, 260, EB(370, y, 40, 26).FlatCopy(), "tipjumpfwd");
            c.AddHoverText("On to the last page.", tipFont, 200, EB(414, y, 40, 26).FlatCopy(), "tiplast");

            // Standing count of switched-off pins, kept out of the status line (which has no room)
            // and off the transient notice: hidden pins draw nowhere on the map, so the one thing
            // that must never happen is a player forgetting they have any.
            c.AddDynamicText(HiddenText(), font, EB(462, y + 4, 130, 24), "hiddeninfo");
            c.AddDynamicText(notice, font.Clone().WithOrientation(EnumTextOrientation.Right), EB(600, y + 4, DW - 600, 24), "notice");

            // Action row A - the mutations, which all go through the confirmation screen.
            y += 34;
            c.AddSmallButton("Delete", () => { BuildDelete(); return true; }, EB(4, y, 84, 28));
            c.AddSmallButton("Set color...", () => { OpenSetColor(); return true; }, EB(94, y, 104, 28));
            c.AddSmallButton("Set icon...", () => { OpenSetIcon(); return true; }, EB(204, y, 100, 28));
            c.AddSmallButton("Rename...", () => { OpenRename(); return true; }, EB(310, y, 100, 28));
            c.AddSmallButton("Pin", () => { BuildPin(true); return true; }, EB(416, y, 58, 28));
            c.AddSmallButton("Unpin", () => { BuildPin(false); return true; }, EB(480, y, 72, 28));
            c.AddSmallButton("Undo last bulk", () => { BuildUndo(); return true; }, EB(558, y, 134, 28));

            // Action row B - things that change nothing on the server. Hide/Show take effect
            // instantly and are undone by clicking the other one, so they are deliberately kept
            // away from the row above.
            y += 34;
            c.AddSmallButton("Hide", () => { ApplyVisibility(true); return true; }, EB(4, y, 56, 28));
            c.AddSmallButton("Show", () => { ApplyVisibility(false); return true; }, EB(66, y, 58, 28));
            c.AddSmallButton("Pin sets...", () => { OpenPinSets(); return true; }, EB(130, y, 120, 28));
            c.AddSmallButton("New pin...", () => { OpenNewPin(); return true; }, EB(256, y, 100, 28));

            // Action row C - the way out, and the two screens that hold everything else.
            //
            // Tools and Map options are both "cabinets": the buttons behind them are ones you press
            // rarely and deliberately, and having them all on this screen at once was the clutter
            // that made the common ones hard to find. What is NOT allowed to move into a cabinet is
            // a signal - so the counts that used to live on the "Fix duplicates" and "Fix same-spot"
            // buttons are still on this screen, as a line of text beside the button they are behind.
            y += 34;
            c.AddSmallButton("Tools...", () => { screen = PmScreen.Tools; Recompose(); return true; }, EB(4, y, 100, 28));
            c.AddSmallButton("Map options...", () => { screen = PmScreen.MapOptions; Recompose(); return true; }, EB(110, y, 160, 28));
            c.AddDynamicText(ToolsHintText(), font, EB(280, y + 5, 380, 24), "toolshint");
            c.AddSmallButton("Back to map", () => { OnBackToMap(); return true; }, EB(DW - 146, y, 142, 28));
        }

        /// <summary>
        /// What the Tools screen would tell you if you opened it. Duplicates and same-spot pins are
        /// the only things in there that are a *fact about your data* rather than an action, and a
        /// fact you cannot see is a fact you never act on - so it stays on the main screen even
        /// though its buttons did not.
        /// </summary>
        string ToolsHintText()
        {
            var parts = new List<string>();
            int dupes = DuplicateCount();
            int spots = SameSpotCount();
            if (dupes > 0) parts.Add($"{dupes} duplicate{(dupes == 1 ? "" : "s")}");
            if (spots > 0) parts.Add($"{spots} same-spot");
            if (bin.Entries.Count > 0) parts.Add($"{bin.Entries.Count} in bin");
            return parts.Count == 0 ? "" : "Tools: " + string.Join(" · ", parts);
        }

        void OnBackToMap()
        {
            TryClose();
            var mm = svc.MapManager;
            var mapDlg = mm?.worldMapDlg;
            if (mm != null && (mapDlg == null || !mapDlg.IsOpened() || mapDlg.DialogType != EnumDialogType.Dialog))
            {
                mm.ToggleMap(EnumDialogType.Dialog);
            }
        }

        void AddHeaderButton(GuiComposer c, string label, int col, double x, double w, double y)
        {
            string text = label + (sortCol == col ? (sortAsc ? " ^" : " v") : "");
            c.AddSmallButton(text, () => { OnSortClicked(col); return true; }, EB(x, y, w, 26), EnumButtonStyle.Small);
        }

        void RestoreMatrixState()
        {
            var c = SingleComposer;
            c.GetTextInput("search").SetValue(searchText);
            c.GetSwitch("pinnedonly").SetValue(pinnedOnly);
            var radiusInput = c.GetNumberInput("radius");
            radiusInput.Interval = 50f;    // native tiny arrows / mouse wheel step usefully at map scale
            radiusInput.OnTryTextChangeText = lines => lines.Count == 0 || !lines[0].TrimStart().StartsWith("-");
            var slider = c.GetSlider("radiusslider");
            slider.OnSliderTooltip = RungLabel;
            slider.OnSliderRestingText = RungLabel;
            slider.ShowTextWhenResting = true;
            slider.SetValues(RungIndexFor(radius), 0, RadiusRungs.Length - 1, 1);
            if (radius > 0) radiusInput.SetValue(radius.ToString("0.#", CultureInfo.InvariantCulture));
            if (colorFilter.Count > 0) c.GetDropDown("colorfilter").SetSelectedValue(colorFilter.ToArray());
            if (iconFilter.Count > 0) c.GetDropDown("iconfilter").SetSelectedValue(iconFilter.ToArray());
            c.GetSwitch("groupdupes").SetValue(groupDuplicates);
        }

        // The folded-copy count lives in the toggle's notice and the "x N copies" headers rather than
        // here — the status line has no room for it once the grouping switch takes its left edge.
        string StatusText() => $"{allRows.Count} pins · {viewRows.Count} shown · {selectedKeys.Count} selected";
        string PageText() => $"Page {page + 1}/{MaxPage + 1}";

        string HiddenText() => visibility.HiddenCount > 0 ? $"{visibility.HiddenCount} hidden" : "";

        string VisFilterLabel()
        {
            switch (visFilter)
            {
                case VisFilter.VisibleOnly: return "Show: visible";
                case VisFilter.HiddenOnly: return "Show: hidden";
                default: return "Show: all";
            }
        }

        void UpdateMatrixDynamic()
        {
            if (screen != PmScreen.Matrix || SingleComposer == null) return;
            (SingleComposer.GetElement("table") as GuiElementCustomDraw)?.Redraw();
            SingleComposer.GetDynamicText("status")?.SetNewText(StatusText(), false, true, false);
            SingleComposer.GetDynamicText("pageinfo")?.SetNewText(PageText(), false, true, false);
            SingleComposer.GetDynamicText("hiddeninfo")?.SetNewText(HiddenText(), false, true, false);
            SingleComposer.GetDynamicText("notice")?.SetNewText(notice, false, true, false);
            SingleComposer.GetDynamicText("toolshint")?.SetNewText(ToolsHintText(), false, true, false);
            RefreshColorFilterLabels();
            RefreshIconFilterLabels();
        }

        // ------------------------------------------------------------------ filter/sort/paging handlers

        void OnSearchChanged(string text)
        {
            if (text == searchText) return;
            searchText = text ?? "";
            ApplyView();
            UpdateMatrixDynamic();
        }

        /// <summary>Draws a waypoint icon the same way vanilla's icon picker does; falls back to the code text.</summary>
        void DrawIconGlyph(Context ctx, string code, double xPx, double yPx, double sizePx, double[] rgba)
        {
            if (IconDrawable(code))
            {
                ctx.Save();
                try
                {
                    capi.Gui.Icons.DrawIcon(ctx, "wp" + code.UcFirst(), xPx, yPx, sizePx, sizePx, rgba);
                    return;
                }
                catch (Exception e)
                {
                    // an icon that probed fine can still break later if its asset data is released
                    MarkIconBroken(code, e);
                }
                finally { ctx.Restore(); }
            }

            var font = CairoFont.WhiteSmallText();
            font.SetupContext(ctx);
            capi.Gui.Text.DrawTextLine(ctx, font, code.Length > 3 ? code.Substring(0, 3) : code, xPx, yPx, false);
        }

        /// <summary>Loads the worldmap icon SVGs; see <see cref="WaypointIconAssets"/> for why that is needed.</summary>
        void EnsureIconAssetsLoaded()
        {
            if (iconAssetsLoaded) return;
            iconAssetsLoaded = true;
            WaypointIconAssets.EnsureLoaded(capi);
        }

        /// <summary>
        /// Whether a waypoint icon can actually be painted — established by painting it once onto a
        /// scratch surface, because asking is not possible and getting it wrong takes down the client.
        /// This is the backstop for a genuinely unusable SVG once
        /// <see cref="EnsureIconAssetsLoaded"/> has done what it can; vanilla's own
        /// <c>AddIconListPicker</c> has the same landmine, hence <see cref="DrawableIconCodes"/>.
        /// The verdicts are re-established on every dialog open so a transient failure cannot
        /// blacklist half the icon set for the rest of the session.
        /// </summary>
        bool IconDrawable(string code)
        {
            string name = "wp" + code.UcFirst();
            // not cached: the map layer may still be registering icons when the dialog first opens
            if (!capi.Gui.Icons.CustomIcons.ContainsKey(name)) return false;

            if (brokenIcons.Contains(code)) return false;
            if (!probedIcons.Add(code)) return true;

            try
            {
                using (var surface = new ImageSurface(Format.Argb32, 24, 24))
                using (var probeCtx = new Context(surface))
                {
                    capi.Gui.Icons.DrawIcon(probeCtx, name, 0, 0, 20, 20, IconWhite);
                }
                return true;
            }
            catch (Exception e)
            {
                MarkIconBroken(code, e);
                return false;
            }
        }

        void MarkIconBroken(string code, Exception e)
        {
            if (!brokenIcons.Add(code)) return;
            capi.Logger.Warning("[pinmatrix] Waypoint icon '{0}' cannot be drawn ({1}) — showing its code instead, and hiding it from the icon pickers.", code, e.Message);
        }

        /// <summary>
        /// Icon codes safe to hand to vanilla's <c>AddIconListPicker</c>, which paints every entry at
        /// compose time and cannot survive a broken one. Callers must use this same list to resolve
        /// the picked index — the filtering shifts indices.
        /// </summary>
        string[] DrawableIconCodes() => svc.IconCodes().Where(IconDrawable).ToArray();

        void OnColorFilterChanged(string code, bool selected)
        {
            if (selected) colorFilter.Add(code); else colorFilter.Remove(code);
            ApplyView();
            UpdateMatrixDynamic();
        }

        void OnGroupDuplicatesToggled(bool on)
        {
            groupDuplicates = on;
            expandedGroups.Clear();   // a fresh grouping starts folded, which is the point of it
            page = 0;
            anchorRow = -1;
            ApplyView();
            notice = !on ? ""
                : shownDuplicates > 0 ? $"{shownDuplicates} duplicate copies folded — click a header to unfold it."
                : "No duplicates among the pins currently shown.";
            UpdateMatrixDynamic();
        }

        bool OnVisFilterClicked()
        {
            visFilter = (VisFilter)(((int)visFilter + 1) % 3);
            page = 0;
            anchorRow = -1;
            ApplyView();
            Recompose();    // the button's own label is what says which state it is in
            return true;
        }

        /// <summary>
        /// Switches the selected pins off the map (or back on). Nothing is sent to the server and
        /// nothing is deleted — see <see cref="WaypointVisibility"/> — so this needs no confirmation
        /// screen, no batching and no undo entry: clicking the other button puts it back.
        /// </summary>
        void ApplyVisibility(bool hide)
        {
            if (!GuardSelection(out var rows)) return;

            if (!visibility.Available)
            {
                notice = "Hiding pins is not available on this game version — see the client log.";
                UpdateMatrixDynamic();
                return;
            }

            int changed = visibility.Set(rows.Select(r => r.Key), hide);
            if (changed == 0)
            {
                notice = hide ? "All selected pins are already hidden." : "None of the selected pins are hidden.";
                UpdateMatrixDynamic();
                return;
            }

            notice = hide
                ? $"Hid {changed} pins — still here, just not drawn on the map."
                : $"Restored {changed} pins to the map.";
            ApplyView();        // they may drop out of the current view
            UpdateMatrixDynamic();
        }

        void OnPinnedOnlyToggled(bool on)
        {
            pinnedOnly = on;
            ApplyView();
            UpdateMatrixDynamic();
        }

        void OnRadiusChanged(string text)
        {
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
            radius = v;
            // keep the slider handle in sync (nearest notch, display only — SetValues never
            // fires the slider's own event, so this cannot loop)
            SingleComposer?.GetSlider("radiusslider")?.SetValues(RungIndexFor(radius), 0, RadiusRungs.Length - 1, 1);
            ApplyView();
            UpdateMatrixDynamic();
        }

        // Slider notches for the radius filter. The slider's integer value is an INDEX into
        // this ladder; equal-width notches over 1–2.5–5 rungs give the travel a log scale —
        // fine control close-in, no huge-number tail. Dragging from the left reveals the
        // nearest pins in growing rings (the filter re-applies live). Index 0 = filter off.
        // The number box remains for exact or larger-than-10000 values.
        static readonly int[] RadiusRungs = { 0, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };

        static string RungLabel(int idx) => idx <= 0 ? "off" : RadiusRungs[idx] + " blocks";

        static int RungIndexFor(double v)
        {
            if (v <= 0) return 0;
            int best = 1;
            for (int i = 1; i < RadiusRungs.Length; i++)
            {
                if (Math.Abs(RadiusRungs[i] - v) < Math.Abs(RadiusRungs[best] - v)) best = i;
            }
            return best;
        }

        bool OnRadiusSlider(int idx)
        {
            int v = RadiusRungs[GameMath.Clamp(idx, 0, RadiusRungs.Length - 1)];
            // drive the number box; its changed-handler re-filters and redraws the table
            SingleComposer?.GetNumberInput("radius")?.SetValue(v == 0 ? "" : v.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// Widens the radius just enough to take in the next-nearest pin beyond it (among pins
        /// passing the other filters). From "off" the first click shows only the nearest pin —
        /// each further click admits the next distance shell.
        /// </summary>
        bool OnRadiusNextPin()
        {
            double cur = Math.Max(0, radius);
            double next = double.MaxValue;
            foreach (var r in FilteredExceptRadius())
            {
                if (r.Dist > cur && r.Dist < next) next = r.Dist;
            }
            if (next == double.MaxValue)
            {
                notice = radius > 0 ? "No pins beyond the current radius." : "No pins match the other filters.";
                UpdateMatrixDynamic();
                return true;
            }
            notice = "";
            // ceil keeps the box tidy and (Dist <= radius) inclusive of the found pin
            SingleComposer?.GetNumberInput("radius")?.SetValue(Math.Ceiling(next).ToString("0.#", CultureInfo.InvariantCulture));
            return true;
        }

        void OnSortClicked(int col)
        {
            if (sortCol == col) sortAsc = !sortAsc;
            else { sortCol = col; sortAsc = true; }
            ApplyView();
            Recompose();    // header arrow labels change
        }

        bool OnSelectAllFiltered()
        {
            foreach (var r in viewRows) selectedKeys.Add(r.Key);
            UpdateMatrixDynamic();
            return true;
        }

        bool OnClearSelection()
        {
            selectedKeys.Clear();
            anchorRow = -1;
            UpdateMatrixDynamic();
            return true;
        }

        bool OnClearFilters()
        {
            searchText = "";
            iconFilter.Clear();
            colorFilter.Clear();
            pinnedOnly = false;
            radius = 0;
            visFilter = VisFilter.All;
            ApplyView();
            Recompose();    // reset filter widgets visually
            return true;
        }

        bool OnRefreshClicked()
        {
            svc.RequestResync();
            RefreshData();
            Recompose();
            return true;
        }

        /// <summary>
        /// Why the table is empty — the three causes need different actions from the player, and
        /// "No waypoints yet" was misreporting a sync that simply hadn't happened.
        /// </summary>
        string EmptyTableText()
        {
            if (allRows.Count > 0) return "No pins match the current filters.";
            if (svc.Layer == null) return "Waypoint map layer not ready yet.";

            int synced = svc.SyncedCount;
            if (synced > 0) return $"{synced} waypoints are synced, but none are yours — group-shared pins can't be managed here.";

            return "No waypoints received from the server yet — a resync was requested; 'Refresh' asks again.";
        }

        /// <summary>How far << and >> move. Ten is a screenful of screenfuls, which is the point.</summary>
        public const int PageJump = 10;

        bool OnPrevPage()
        {
            if (page > 0) { page--; UpdateMatrixDynamic(); }
            return true;
        }

        bool OnNextPage()
        {
            if (page < MaxPage) { page++; UpdateMatrixDynamic(); }
            return true;
        }

        // Clamped, never wrapped: a jump that ran off the end and reappeared at the other one would
        // be indistinguishable from the list having changed under you.

        bool OnFirstPage()
        {
            if (page != 0) { page = 0; UpdateMatrixDynamic(); }
            return true;
        }

        bool OnLastPage()
        {
            if (page != MaxPage) { page = MaxPage; UpdateMatrixDynamic(); }
            return true;
        }

        bool OnFirstJump()
        {
            int target = Math.Max(0, page - PageJump);
            if (target != page) { page = target; UpdateMatrixDynamic(); }
            return true;
        }

        bool OnLastJump()
        {
            int target = Math.Min(MaxPage, page + PageJump);
            if (target != page) { page = target; UpdateMatrixDynamic(); }
            return true;
        }

        // ------------------------------------------------------------------ table drawing

        void DrawTable(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            var font = CairoFont.WhiteSmallText();
            // Hidden pins draw the same row, dimmed — they are still yours, just not on the map.
            // Dim enough to read at a glance as "switched off", never so dim it stops being legible:
            // the table is where you go to find a hidden pin again.
            var dimFont = font.Clone().WithColor(new double[] { 1, 1, 1, 0.7 });
            double rh = GuiElement.scaled(RowH);
            double innerW = GuiElement.scaled(DW);

            int start = page * PageSize;
            int count = Math.Min(PageSize, Math.Max(0, gridLines.Count - start));

            if (count == 0)
            {
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, EmptyTableText(), GuiElement.scaled(10), GuiElement.scaled(8), false);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var line = gridLines[start + i];
                var group = line.Group;
                // A group's members are identical in every column, so its header simply draws the
                // shared row once — the columns still line up, with "x N" where the actions would be.
                var row = group != null ? group.Rows[0] : line.Row;
                var wp = row.Wp;
                double ry = i * rh;
                // A group draws as one row, so it counts as hidden only when every copy in it is
                bool hiddenRow = group != null ? group.Rows.All(IsHidden) : IsHidden(row);
                var rowFont = hiddenRow ? dimFont : font;
                bool isSel = group != null
                    ? group.Rows.All(r => selectedKeys.Contains(r.Key))
                    : selectedKeys.Contains(row.Key);
                bool isMember = group == null && groupDuplicates && expandedGroups.Contains(DupSignature(wp));

                if (isSel)
                {
                    ctx.SetSourceRGBA(0.45, 0.62, 0.3, 0.32);
                    ctx.Rectangle(0, ry, innerW, rh);
                    ctx.Fill();
                }
                else if (group != null)
                {
                    ctx.SetSourceRGBA(0.85, 0.72, 0.4, 0.16);
                    ctx.Rectangle(0, ry, innerW, rh);
                    ctx.Fill();
                }
                else if ((start + i) % 2 == 0)
                {
                    ctx.SetSourceRGBA(1, 1, 1, 0.045);
                    ctx.Rectangle(0, ry, innerW, rh);
                    ctx.Fill();
                }

                // checkbox
                double cb = GuiElement.scaled(15);
                double cbx = GuiElement.scaled(ColSelX + 5);
                double cby = ry + (rh - cb) / 2;
                ctx.SetSourceRGBA(0.85, 0.85, 0.85, 0.85);
                ctx.LineWidth = 1.5;
                ctx.Rectangle(cbx, cby, cb, cb);
                ctx.Stroke();
                if (isSel)
                {
                    ctx.SetSourceRGBA(0.62, 0.85, 0.4, 1);
                    ctx.Rectangle(cbx + 3, cby + 3, cb - 6, cb - 6);
                    ctx.Fill();
                }

                // fold arrow on headers; members sit indented under their header
                double nameIndent = 0;
                if (group != null)
                {
                    DrawFoldArrow(ctx, GuiElement.scaled(ColNameX + 2), ry, rh, expandedGroups.Contains(group.Sig));
                    nameIndent = 13;
                }
                else if (isMember) nameIndent = 13;

                DrawCell(ctx, rowFont, wp.Title ?? "", ColNameX + nameIndent, ColNameW - nameIndent, ry, rh);
                DrawIconGlyph(ctx, WpCommands.SafeIcon(wp.Icon), GuiElement.scaled(ColIconX + 24), ry + GuiElement.scaled(2.5), GuiElement.scaled(20), hiddenRow ? IconHiddenRow : IconWhite);

                // color swatch
                int col = wp.Color;
                ctx.SetSourceRGBA(((col >> 16) & 0xff) / 255.0, ((col >> 8) & 0xff) / 255.0, (col & 0xff) / 255.0, hiddenRow ? 0.65 : 1);
                ctx.Rectangle(GuiElement.scaled(ColColorX + 8), ry + GuiElement.scaled(5), GuiElement.scaled(38), rh - GuiElement.scaled(10));
                ctx.Fill();
                ctx.SetSourceRGBA(0, 0, 0, 0.5);
                ctx.LineWidth = 1;
                ctx.Rectangle(GuiElement.scaled(ColColorX + 8), ry + GuiElement.scaled(5), GuiElement.scaled(38), rh - GuiElement.scaled(10));
                ctx.Stroke();

                DrawCell(ctx, rowFont, FmtCoord(svc.RelX(wp.Position.X)), ColXX, ColXW, ry, rh);
                DrawCell(ctx, rowFont, FmtCoord(wp.Position.Y), ColYX, ColYW, ry, rh);
                DrawCell(ctx, rowFont, FmtCoord(svc.RelZ(wp.Position.Z)), ColZX, ColZW, ry, rh);
                DrawCell(ctx, rowFont, FmtDist(row.Dist), ColDistX, ColDistW, ry, rh);
                DrawEye(ctx, ColVisX, ColVisW, ry, rh, !hiddenRow);
                DrawCell(ctx, rowFont, wp.Pinned ? "Y" : "", ColPinX, ColPinW, ry, rh);

                if (group != null)
                {
                    // no per-row actions on a header: they would be ambiguous across N identical pins
                    DrawCell(ctx, rowFont, $"x {group.Rows.Count} copies", ColActX, ColActW, ry, rh);
                    continue;
                }

                DrawMiniButton(ctx, font, "Edit", ColActX, ry, rh);
                DrawMiniButton(ctx, font, "Map", ColActX + 46, ry, rh);
                DrawMiniButton(ctx, font, "Move", ColActX + 92, ry, rh);
                DrawMiniButton(ctx, font, "Share", ColActX + 138, ry, rh);
            }
        }

        void DrawFoldArrow(Context ctx, double x, double ry, double rh, bool expanded)
        {
            double s = GuiElement.scaled(4.5);
            double cy = ry + rh / 2;
            ctx.NewPath();
            if (expanded)
            {
                ctx.MoveTo(x - s, cy - s * 0.7);
                ctx.LineTo(x + s, cy - s * 0.7);
                ctx.LineTo(x, cy + s * 0.8);
            }
            else
            {
                ctx.MoveTo(x - s * 0.7, cy - s);
                ctx.LineTo(x + s * 0.8, cy);
                ctx.LineTo(x - s * 0.7, cy + s);
            }
            ctx.ClosePath();
            ctx.SetSourceRGBA(0.95, 0.88, 0.7, 0.95);
            ctx.Fill();
        }

        /// <summary>
        /// The Vis cell: an open eye when the pin is drawn on the map, a dim struck-through one when
        /// it isn't. Clicking the cell toggles it — a Y/blank like the Pin column would have read as
        /// data rather than as a switch, and this column is the switch.
        /// </summary>
        void DrawEye(Context ctx, double colX, double colW, double ry, double rh, bool visible)
        {
            double cx = GuiElement.scaled(colX + colW / 2);
            double cy = ry + rh / 2;
            double w = GuiElement.scaled(8);      // half-width of the lens
            double h = GuiElement.scaled(4.6);    // half-height

            if (visible) ctx.SetSourceRGBA(0.95, 0.92, 0.82, 0.95);
            else ctx.SetSourceRGBA(0.85, 0.85, 0.85, 0.62);
            ctx.LineWidth = GuiElement.scaled(1.3);

            // lens: two arcs meeting at the corners, drawn as quadratic-ish curves
            ctx.NewPath();
            ctx.MoveTo(cx - w, cy);
            ctx.CurveTo(cx - w * 0.5, cy - h, cx + w * 0.5, cy - h, cx + w, cy);
            ctx.CurveTo(cx + w * 0.5, cy + h, cx - w * 0.5, cy + h, cx - w, cy);
            ctx.ClosePath();
            ctx.Stroke();

            if (visible)
            {
                ctx.NewPath();
                ctx.Arc(cx, cy, GuiElement.scaled(2.2), 0, Math.PI * 2);
                ctx.Fill();
            }
            else
            {
                ctx.NewPath();
                ctx.MoveTo(cx - w * 0.95, cy + h * 1.25);
                ctx.LineTo(cx + w * 0.95, cy - h * 1.25);
                ctx.Stroke();
            }
        }

        void DrawCell(Context ctx, CairoFont font, string text, double colX, double colW, double ry, double rh)
        {
            if (string.IsNullOrEmpty(text)) return;
            string t = Trunc(font, text, GuiElement.scaled(colW - 8));
            font.SetupContext(ctx);
            capi.Gui.Text.DrawTextLine(ctx, font, t, GuiElement.scaled(colX + 4), ry + GuiElement.scaled(3), false);
        }

        void DrawMiniButton(Context ctx, CairoFont font, string label, double xUnscaled, double ry, double rh)
        {
            double bx = GuiElement.scaled(xUnscaled);
            double bw = GuiElement.scaled(42);
            double by = ry + GuiElement.scaled(2.5);
            double bh = rh - GuiElement.scaled(5);

            ctx.SetSourceRGBA(1, 1, 1, 0.1);
            ctx.Rectangle(bx, by, bw, bh);
            ctx.Fill();
            ctx.SetSourceRGBA(0.85, 0.8, 0.65, 0.45);
            ctx.LineWidth = 1;
            ctx.Rectangle(bx, by, bw, bh);
            ctx.Stroke();

            var extents = font.GetTextExtents(label);
            font.SetupContext(ctx);
            capi.Gui.Text.DrawTextLine(ctx, font, label, bx + Math.Max(0, (bw - extents.Width) / 2), ry + GuiElement.scaled(3), false);
        }

        string Trunc(CairoFont font, string text, double maxPx)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (font.GetTextExtents(text).Width <= maxPx) return text;
            for (int len = text.Length - 1; len > 0; len--)
            {
                string t = text.Substring(0, len) + "..";
                if (font.GetTextExtents(t).Width <= maxPx) return t;
            }
            return "..";
        }

        static string FmtCoord(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

        static string FmtDist(double d)
            => d < 1000 ? ((int)d).ToString(CultureInfo.InvariantCulture)
                        : (d / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";

        string Disp(Waypoint wp)
            => $"{WpCommands.SafeTitle(wp.Title)} [{WpCommands.SafeIcon(wp.Icon)}] ({FmtCoord(svc.RelX(wp.Position.X))}, {FmtCoord(wp.Position.Y)}, {FmtCoord(svc.RelZ(wp.Position.Z))})";

        // ------------------------------------------------------------------ mouse handling

        // Replicates GuiDialog.OnMouseDown but inserts the custom table hit-zones BEFORE the
        // catch-all that marks any click inside the dialog as handled. Interactive elements
        // (buttons, inputs, open dropdown lists) still get first claim on the event.
        public override void OnMouseDown(MouseEvent args)
        {
            if (args.Handled) return;

            foreach (var composer in Composers.Values.ToArray())
            {
                composer.OnMouseDown(args);
                if (args.Handled) return;
            }

            if (!IsOpened()) return;

            if (screen == PmScreen.Matrix && tableBounds != null && tableBounds.PointInside(args.X, args.Y))
            {
                HandleTableClick(args);
                return;
            }
            if (screen == PmScreen.Bin && binTableBounds != null && binTableBounds.PointInside(args.X, args.Y))
            {
                HandleBinClick(args);
                return;
            }

            foreach (var composer in Composers.Values)
            {
                if (composer.Bounds.PointInside(args.X, args.Y))
                {
                    args.Handled = true;
                    break;
                }
            }
        }

        void HandleTableClick(MouseEvent args)
        {
            int rowOnPage = (int)((args.Y - tableBounds.absY) / GuiElement.scaled(RowH));
            int idx = page * PageSize + rowOnPage;
            if (rowOnPage < 0 || rowOnPage >= PageSize || idx >= gridLines.Count) { args.Handled = true; return; }

            double ux = (args.X - tableBounds.absX) / GuiElement.scaled(1);

            var group = gridLines[idx].Group;
            if (group != null)
            {
                // the Vis cell switches every copy the header stands for, together
                if (ux >= ColVisX && ux < ColVisX + ColVisW)
                {
                    ToggleVisibility(group.Rows);
                    args.Handled = true;
                    return;
                }

                // checkbox column selects the whole set; anywhere else folds it open or shut
                if (ux < ColNameX)
                {
                    bool allSel = group.Rows.All(r => selectedKeys.Contains(r.Key));
                    foreach (var r in group.Rows)
                    {
                        if (allSel) selectedKeys.Remove(r.Key); else selectedKeys.Add(r.Key);
                    }
                }
                else
                {
                    if (!expandedGroups.Add(group.Sig)) expandedGroups.Remove(group.Sig);
                    ApplyView();
                }
                anchorRow = -1;
                UpdateMatrixDynamic();
                args.Handled = true;
                return;
            }

            var row = gridLines[idx].Row;

            if (ux >= ColVisX && ux < ColVisX + ColVisW)
            {
                ToggleVisibility(new List<PinRow> { row });
                args.Handled = true;
                return;
            }

            if (ux >= ColActX)
            {
                double sub = ux - ColActX;
                if (sub < 44) OpenVanillaEdit(row);
                else if (sub >= 46 && sub < 90) ShowOnMap(row);
                else if (sub >= 92 && sub < 136) OpenMove(row);
                else if (sub >= 138 && sub < 182) OpenShare(row);
                args.Handled = true;
                return;
            }

            long now = capi.World.ElapsedMilliseconds;
            bool doubleClick = idx == lastClickRow && now - lastClickMs < 400;
            lastClickMs = now;
            lastClickRow = idx;

            if (doubleClick)
            {
                ShowOnMap(row);
                args.Handled = true;
                return;
            }

            bool shift = capi.Input.KeyboardKeyStateRaw[(int)GlKeys.LShift] || capi.Input.KeyboardKeyStateRaw[(int)GlKeys.RShift];
            if (shift && anchorRow >= 0 && anchorRow < gridLines.Count)
            {
                int a = Math.Min(anchorRow, idx), b = Math.Max(anchorRow, idx);
                for (int i = a; i <= b; i++)
                {
                    // a header inside the range takes every copy it stands for with it
                    var line = gridLines[i];
                    if (line.Group != null) foreach (var r in line.Group.Rows) selectedKeys.Add(r.Key);
                    else selectedKeys.Add(line.Row.Key);
                }
            }
            else
            {
                if (!selectedKeys.Add(row.Key)) selectedKeys.Remove(row.Key);
                anchorRow = idx;
            }
            UpdateMatrixDynamic();
            args.Handled = true;
        }

        // ------------------------------------------------------------------ row actions

        /// <summary>
        /// Flips the Vis cell for one row, or for every copy a duplicate header stands for. Mixed
        /// sets hide as a whole first — "make these go away" is the common intent, and clicking again
        /// brings all of them back.
        /// </summary>
        void ToggleVisibility(List<PinRow> rows)
        {
            if (!visibility.Available)
            {
                notice = "Hiding pins is not available on this game version — see the client log.";
                UpdateMatrixDynamic();
                return;
            }

            bool hide = rows.Any(r => !IsHidden(r));
            visibility.Set(rows.Select(r => r.Key), hide);
            anchorRow = -1;
            ApplyView();        // the row may drop out of the current view
            UpdateMatrixDynamic();
        }

        void OpenVanillaEdit(PinRow row)
        {
            int index = svc.ResolveIndex(row.Key);
            if (index < 0)
            {
                notice = "That pin no longer exists.";
                RefreshData();
                Recompose();
                return;
            }
            var layer = svc.Layer;
            if (layer == null) return;
            var dlg = new GuiDialogEditWayPointOnTop(capi, layer, svc.Own[index], index);
            dlg.TryOpen();
            // This click is a mouse-down in our table; once it finishes, the GuiManager hands
            // focus back to the matrix (the handling dialog). Re-focus the editor next tick so
            // typing lands in its title box right away.
            capi.Event.RegisterCallback(_ => { if (dlg.IsOpened()) capi.Gui.RequestFocus(dlg); }, 0);
        }

        void ShowOnMap(PinRow row)
        {
            var mm = svc.MapManager;
            if (mm == null) return;

            var dlg = mm.worldMapDlg;
            if (dlg == null || !dlg.IsOpened() || dlg.DialogType != EnumDialogType.Dialog)
            {
                mm.ToggleMap(EnumDialogType.Dialog);
            }

            var pos = row.Wp.Position.AsBlockPos;
            capi.World.RegisterCallback(_ =>
            {
                var mapDlg = mm.worldMapDlg;
                if (mapDlg == null) return;
                foreach (var compo in mapDlg.Composers.Values)
                {
                    if (compo?.GetElement("mapElem") is GuiElementMap mapElem)
                    {
                        mapElem.CenterMapTo(pos);
                    }
                }
            }, 250);

            // The matrix draws above the map — close it so the map is actually visible (P reopens it)
            notice = $"Centered map on '{WpCommands.SafeTitle(row.Wp.Title)}'.";
            TryClose();
        }

        void ExecuteMapRedraw()
        {
            try
            {
                var cmdArgs = new TextCommandCallingArgs
                {
                    Caller = new Caller { Player = capi.World.Player, Type = EnumCallerType.Player }
                };
                capi.ChatCommands.ExecuteUnparsed("map redraw", cmdArgs, result =>
                {
                    notice = result?.Status == EnumCommandStatus.Success
                        ? "Map tiles re-queued for redraw (loaded chunks only)."
                        : "Map redraw failed: " + (result?.StatusMessage ?? "unknown");
                    UpdateMatrixDynamic();
                });
            }
            catch (Exception e)
            {
                notice = "Map redraw unavailable: " + e.Message;
                UpdateMatrixDynamic();
            }
        }
    }
}
