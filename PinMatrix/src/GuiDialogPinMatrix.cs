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
        Matrix, Confirm, SetColor, SetIcon, Rename, NewPin, Bin, ImportExport, Share
    }

    public class PendingBulk
    {
        public string Title;
        public string Warning;
        public string[] Lines;
        public string ConfirmText;
        public Action Execute;
        public PmScreen ReturnScreen = PmScreen.Matrix;
    }

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

        // table columns: x, width
        const double ColSelX = 4, ColSelW = 26;
        const double ColNameX = 34, ColNameW = 240;
        const double ColIconX = 278, ColIconW = 78;
        const double ColColorX = 360, ColColorW = 60;
        const double ColXX = 424, ColXW = 60;
        const double ColYX = 488, ColYW = 46;
        const double ColZX = 538, ColZW = 60;
        const double ColDistX = 602, ColDistW = 62;
        const double ColPinX = 668, ColPinW = 34;
        const double ColActX = 706, ColActW = 186;

        readonly PinMatrixConfig config;
        readonly WaypointService svc;
        readonly BatchEngine batch;
        readonly RecycleBin bin;

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

        // sort: 0 name, 1 icon, 2 color, 3 x, 4 y, 5 z, 6 dist, 7 pinned
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

        // icon filter strip
        const double IconCellW = 28;
        const double IconCellH = 26;
        const int IconsPerStripRow = 27;
        static readonly double[] IconWhite = { 1, 1, 1, 1 };
        static readonly double[] IconDim = { 1, 1, 1, 0.45 };
        string[] stripIcons = new string[0];
        ElementBounds iconStripBounds;
        readonly HashSet<string> probedIcons = new HashSet<string>();
        readonly HashSet<string> brokenIcons = new HashSet<string>();
        bool iconAssetsLoaded;

        // colour filter dropdown — values are the colours actually in use, labels carry live counts
        string[] filterColorHexes = { "#ffffff" };
        bool colorLabelsStale;

        int PageSize => config.RowsPerPage;
        int MaxPage => Math.Max(0, (gridLines.Count - 1) / PageSize);

        public GuiDialogPinMatrix(ICoreClientAPI capi, PinMatrixConfig config, WaypointService svc, BatchEngine batch, RecycleBin bin)
            : base(capi)
        {
            this.config = config;
            this.svc = svc;
            this.batch = batch;
            this.bin = bin;
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

            ColorSwatchComponent.EnsureTagRegistered();   // the tag table is wiped on leaving a world
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
            anchorRow = -1;
            RebuildColorFilterValues();
            ApplyView();
        }

        // ------------------------------------------------------------------ colour filter dropdown

        /// <summary>
        /// The dropdown lists only colours some waypoint actually uses — the full vanilla palette
        /// plus every custom colour is an unreadable wall of hex. Recomputed whenever the waypoint
        /// list changes, dropping any filtered colour that no longer exists.
        /// </summary>
        void RebuildColorFilterValues()
        {
            var hexes = allRows
                .Select(r => WpCommands.ColorHex(r.Wp.Color))
                .Distinct()
                .OrderBy(HueKey)
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

        IEnumerable<PinRow> Filtered(bool useColor, bool useRadius)
        {
            IEnumerable<PinRow> q = allRows;
            if (searchText.Length > 0) q = q.Where(r => (r.Wp.Title ?? "").IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (iconFilter.Count > 0) q = q.Where(r => iconFilter.Contains(WpCommands.SafeIcon(r.Wp.Icon)));
            if (useColor && colorFilter.Count > 0) q = q.Where(r => colorFilter.Contains(WpCommands.ColorHex(r.Wp.Color)));
            if (pinnedOnly) q = q.Where(r => r.Wp.Pinned);
            if (useRadius && radius > 0) q = q.Where(r => r.Dist <= radius);
            return q;
        }

        /// <summary>All filters except the radius — also the candidate set for "Next pin".</summary>
        IEnumerable<PinRow> FilteredExceptRadius() => Filtered(useColor: true, useRadius: false);

        /// <summary>
        /// All filters except the colour filter — the base set the colour dropdown counts against.
        /// Excluding the colour filter itself is what makes the counts useful: they answer "how many
        /// more pins would this colour add", so picking one colour doesn't zero out all the others.
        /// </summary>
        IEnumerable<PinRow> FilteredExceptColor() => Filtered(useColor: false, useRadius: true);

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
        static string DupSignature(Waypoint wp)
            => string.Join("|",
                WpCommands.SafeTitle(wp.Title),
                WpCommands.SafeIcon(wp.Icon),
                WpCommands.ColorHex(wp.Color),
                wp.Pinned ? "1" : "0",
                wp.Position.X.ToString("0.##", CultureInfo.InvariantCulture),
                wp.Position.Y.ToString("0.##", CultureInfo.InvariantCulture),
                wp.Position.Z.ToString("0.##", CultureInfo.InvariantCulture));

        /// <summary>Groups <paramref name="rows"/> by duplicate signature, keeping first-seen order.</summary>
        static List<DupGroup> GroupByDuplicate(IEnumerable<PinRow> rows)
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

        IEnumerable<PinRow> Sorted<TKey>(IEnumerable<PinRow> q, System.Func<PinRow, TKey> key, IComparer<TKey> cmp)
            => sortAsc ? q.OrderBy(key, cmp) : q.OrderByDescending(key, cmp);

        List<PinRow> SelectedAllRows() => allRows.Where(r => selectedKeys.Contains(r.Key)).ToList();

        void OnPollTick(float dt)
        {
            if (!IsOpened() || batch.Busy) return;

            // deferred from a filter change made while the dropdown was expanded
            if (screen == PmScreen.Matrix && colorLabelsStale) RefreshColorFilterLabels();

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
                default: return "Pin Matrix — Waypoint manager";
            }
        }

        void OnTitleBarClose()
        {
            if (screen == PmScreen.Matrix) TryClose();
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

            // Filter bar. The colour dropdown's extra width is taken from the search box and the
            // radius slider — NOT from the labels: a static text narrower than its string wraps to a
            // second line and overruns the row below it, so these widths carry real slack on purpose.
            c.AddStaticText("Search", font, EB(4, y + 4, 52, 25));
            c.AddTextInput(EB(58, y, 124, 28), OnSearchChanged, font, "search");

            // Width is load-bearing. GuiElementListMenu sizes the expanded list to the widest entry
            // but forgets to add the multi-select checkbox column it then shifts every entry by, so
            // the tail of each label — the count — is clipped unless the element's own width already
            // covers swatch + hex + count + that offset. -1 preselects nothing: index 0 would tick
            // the first colour's checkbox without actually filtering on it.
            c.AddMultiSelectDropDown(filterColorHexes, ColorFilterLabels(), -1, OnColorFilterChanged, EB(190, y, 200, 28), "colorfilter");

            c.AddSwitch(OnPinnedOnlyToggled, EB(398, y, 28, 28), "pinnedonly", 25, 3);
            c.AddStaticText("Pinned only", font, EB(430, y + 4, 110, 25));
            c.AddStaticText("Within", font, EB(546, y + 4, 46, 25));
            c.AddNumberInput(EB(594, y, 60, 28), OnRadiusChanged, font, "radius");
            c.AddSlider(OnRadiusSlider, EB(660, y + 4, 138, 20), "radiusslider");
            c.AddSmallButton("Next pin", OnRadiusNextPin, EB(804, y, 81, 28), EnumButtonStyle.Small);

            // icon filter strip (click icons to toggle; multi-select)
            y += 36;
            stripIcons = svc.IconCodes();
            int stripRows = Math.Max(1, (stripIcons.Length + IconsPerStripRow - 1) / IconsPerStripRow);
            double stripH = stripRows * IconCellH;
            c.AddStaticText("Icons", font, EB(4, y + 2, 52, 25));
            c.AddInset(EB(56, y - 2, IconsPerStripRow * IconCellW + 8, stripH + 4), 3);
            iconStripBounds = EB(60, y, IconsPerStripRow * IconCellW, stripH);
            c.AddDynamicCustomDraw(iconStripBounds, DrawIconStrip, "iconstrip");

            // selection row
            y += stripH + 10;
            c.AddSmallButton("Select all filtered", OnSelectAllFiltered, EB(4, y, 148, 26));
            c.AddSmallButton("Clear selection", OnClearSelection, EB(158, y, 132, 26));
            c.AddSmallButton("Clear filters", OnClearFilters, EB(296, y, 116, 26));
            c.AddSmallButton("Refresh", OnRefreshClicked, EB(418, y, 88, 26));
            c.AddSwitch(OnGroupDuplicatesToggled, EB(510, y - 1, 26, 26), "groupdupes", 23, 3);
            c.AddStaticText("Group dupes", font, EB(540, y + 4, 92, 24));
            c.AddDynamicText(StatusText(), font.Clone().WithOrientation(EnumTextOrientation.Right), EB(636, y + 4, DW - 636, 24), "status");

            // header row (sort buttons)
            y += 34;
            AddHeaderButton(c, "Name", 0, ColNameX, ColNameW, y);
            AddHeaderButton(c, "Icon", 1, ColIconX, ColIconW, y);
            AddHeaderButton(c, "Color", 2, ColColorX, ColColorW, y);
            AddHeaderButton(c, "X", 3, ColXX, ColXW, y);
            AddHeaderButton(c, "Y", 4, ColYX, ColYW, y);
            AddHeaderButton(c, "Z", 5, ColZX, ColZW, y);
            AddHeaderButton(c, "Dist", 6, ColDistX, ColDistW, y);
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
            c.AddSmallButton("< Prev", OnPrevPage, EB(4, y, 78, 26));
            c.AddDynamicText(PageText(), font.Clone().WithOrientation(EnumTextOrientation.Center), EB(86, y + 4, 110, 24), "pageinfo");
            c.AddSmallButton("Next >", OnNextPage, EB(200, y, 78, 26));
            c.AddDynamicText(notice, font.Clone().WithOrientation(EnumTextOrientation.Right), EB(286, y + 4, DW - 286, 24), "notice");

            // action row A — mutations
            y += 34;
            c.AddSmallButton("Delete", () => { BuildDelete(); return true; }, EB(4, y, 84, 28));
            c.AddSmallButton("Set color...", () => { OpenSetColor(); return true; }, EB(94, y, 104, 28));
            c.AddSmallButton("Set icon...", () => { OpenSetIcon(); return true; }, EB(204, y, 100, 28));
            c.AddSmallButton("Pin", () => { BuildPin(true); return true; }, EB(310, y, 58, 28));
            c.AddSmallButton("Unpin", () => { BuildPin(false); return true; }, EB(374, y, 72, 28));
            c.AddSmallButton("Rename...", () => { OpenRename(); return true; }, EB(452, y, 100, 28));
            c.AddSmallButton("Undo last bulk", () => { BuildUndo(); return true; }, EB(558, y, 134, 28));
            int dupes = DuplicateCount();
            c.AddSmallButton(dupes > 0 ? $"Fix duplicates ({dupes})..." : "Fix duplicates...",
                () => { BuildFixDuplicates(); return true; }, EB(698, y, 170, 28));

            // action row B — non-mutating / other
            y += 34;
            c.AddSmallButton("New pin...", () => { OpenNewPin(); return true; }, EB(4, y, 100, 28));
            c.AddSmallButton("Export / Import...", () => { screen = PmScreen.ImportExport; Recompose(); return true; }, EB(110, y, 160, 28));
            c.AddSmallButton($"Recycle bin ({bin.Entries.Count})...", () => { OpenBin(); return true; }, EB(276, y, 160, 28));
            if (config.EnableMapRefresh)
            {
                c.AddSmallButton("Redraw map", () => { ExecuteMapRedraw(); return true; }, EB(442, y, 120, 28));
            }
            c.AddSmallButton("Back to map", () => { OnBackToMap(); return true; }, EB(DW - 146, y, 142, 28));
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
            c.GetSwitch("groupdupes").SetValue(groupDuplicates);
        }

        // The folded-copy count lives in the toggle's notice and the "x N copies" headers rather than
        // here — the status line has no room for it once the grouping switch takes its left edge.
        string StatusText() => $"{allRows.Count} pins · {viewRows.Count} shown · {selectedKeys.Count} selected";
        string PageText() => $"Page {page + 1}/{MaxPage + 1}";

        void UpdateMatrixDynamic()
        {
            if (screen != PmScreen.Matrix || SingleComposer == null) return;
            (SingleComposer.GetElement("table") as GuiElementCustomDraw)?.Redraw();
            (SingleComposer.GetElement("iconstrip") as GuiElementCustomDraw)?.Redraw();
            SingleComposer.GetDynamicText("status")?.SetNewText(StatusText(), false, true, false);
            SingleComposer.GetDynamicText("pageinfo")?.SetNewText(PageText(), false, true, false);
            SingleComposer.GetDynamicText("notice")?.SetNewText(notice, false, true, false);
            RefreshColorFilterLabels();
        }

        // ------------------------------------------------------------------ filter/sort/paging handlers

        void OnSearchChanged(string text)
        {
            if (text == searchText) return;
            searchText = text ?? "";
            ApplyView();
            UpdateMatrixDynamic();
        }

        void DrawIconStrip(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            for (int i = 0; i < stripIcons.Length; i++)
            {
                string code = stripIcons[i];
                double cx = GuiElement.scaled((i % IconsPerStripRow) * IconCellW);
                double cy = GuiElement.scaled((i / IconsPerStripRow) * IconCellH);
                double cw = GuiElement.scaled(IconCellW - 2);
                double ch = GuiElement.scaled(IconCellH - 2);
                bool isSel = iconFilter.Contains(code);

                if (isSel)
                {
                    ctx.SetSourceRGBA(0.45, 0.62, 0.3, 0.55);
                    ctx.Rectangle(cx, cy, cw, ch);
                    ctx.Fill();
                    ctx.SetSourceRGBA(0.7, 0.9, 0.5, 0.9);
                    ctx.LineWidth = 1.5;
                    ctx.Rectangle(cx, cy, cw, ch);
                    ctx.Stroke();
                }

                DrawIconGlyph(ctx, code, cx + GuiElement.scaled(3), cy + GuiElement.scaled(2.5),
                    GuiElement.scaled(20), isSel ? IconWhite : IconDim);
            }
        }

        void HandleIconStripClick(MouseEvent args)
        {
            int col = (int)((args.X - iconStripBounds.absX) / GuiElement.scaled(IconCellW));
            int row = (int)((args.Y - iconStripBounds.absY) / GuiElement.scaled(IconCellH));
            int idx = row * IconsPerStripRow + col;
            args.Handled = true;
            if (col < 0 || col >= IconsPerStripRow || idx < 0 || idx >= stripIcons.Length) return;

            string code = stripIcons[idx];
            if (!iconFilter.Add(code)) iconFilter.Remove(code);
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

        /// <summary>
        /// Loads the worldmap icon SVGs, which is what actually makes them drawable.
        ///
        /// <see cref="WaypointMapLayer"/> indexes <c>textures/icons/worldmap/</c> with
        /// <c>loadAsset: false</c> and registers a custom GUI icon per asset whose renderer draws the
        /// <c>IAsset</c> *object* — so until something loads that object's data, painting the icon
        /// throws `ArgumentNullException("svgAsset")`. Vanilla loads them lazily, when the world map
        /// first builds its waypoint icon textures, so a session that opens Pin Matrix without ever
        /// opening the map has most of the set still unloaded. <c>GetMany</c> hands back the *cached*
        /// asset instances and fills them in place, so this populates exactly the objects those
        /// renderers closed over.
        /// </summary>
        void EnsureIconAssetsLoaded()
        {
            if (iconAssetsLoaded) return;
            iconAssetsLoaded = true;
            try
            {
                capi.Assets.GetMany("textures/icons/worldmap/", null, true);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not preload the waypoint icon assets: {0}", e.Message);
            }
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

        // ------------------------------------------------------------------ table drawing

        void DrawTable(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            var font = CairoFont.WhiteSmallText();
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

                DrawCell(ctx, font, wp.Title ?? "", ColNameX + nameIndent, ColNameW - nameIndent, ry, rh);
                DrawIconGlyph(ctx, WpCommands.SafeIcon(wp.Icon), GuiElement.scaled(ColIconX + 24), ry + GuiElement.scaled(2.5), GuiElement.scaled(20), IconWhite);

                // color swatch
                int col = wp.Color;
                ctx.SetSourceRGBA(((col >> 16) & 0xff) / 255.0, ((col >> 8) & 0xff) / 255.0, (col & 0xff) / 255.0, 1);
                ctx.Rectangle(GuiElement.scaled(ColColorX + 8), ry + GuiElement.scaled(5), GuiElement.scaled(38), rh - GuiElement.scaled(10));
                ctx.Fill();
                ctx.SetSourceRGBA(0, 0, 0, 0.5);
                ctx.LineWidth = 1;
                ctx.Rectangle(GuiElement.scaled(ColColorX + 8), ry + GuiElement.scaled(5), GuiElement.scaled(38), rh - GuiElement.scaled(10));
                ctx.Stroke();

                DrawCell(ctx, font, FmtCoord(svc.RelX(wp.Position.X)), ColXX, ColXW, ry, rh);
                DrawCell(ctx, font, FmtCoord(wp.Position.Y), ColYX, ColYW, ry, rh);
                DrawCell(ctx, font, FmtCoord(svc.RelZ(wp.Position.Z)), ColZX, ColZW, ry, rh);
                DrawCell(ctx, font, FmtDist(row.Dist), ColDistX, ColDistW, ry, rh);
                DrawCell(ctx, font, wp.Pinned ? "Y" : "", ColPinX, ColPinW, ry, rh);

                if (group != null)
                {
                    // no per-row actions on a header: they would be ambiguous across N identical pins
                    DrawCell(ctx, font, $"x {group.Rows.Count} copies", ColActX, ColActW, ry, rh);
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
            if (screen == PmScreen.Matrix && iconStripBounds != null && iconStripBounds.PointInside(args.X, args.Y))
            {
                HandleIconStripClick(args);
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
