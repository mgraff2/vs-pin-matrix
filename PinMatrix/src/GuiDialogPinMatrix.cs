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
        Matrix, Confirm, SetColor, SetIcon, Rename, NewPin, Bin, ImportExport
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

    public partial class GuiDialogPinMatrix : GuiDialog
    {
        // ---- layout constants (unscaled) ----
        const double DW = 850;          // content width
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
        const double ColActX = 706, ColActW = 140;

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
        Vec3d playerPos = new Vec3d();

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

        int PageSize => config.RowsPerPage;
        int MaxPage => Math.Max(0, (viewRows.Count - 1) / PageSize);

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
            RefreshData();
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
            ApplyView();
        }

        void ApplyView()
        {
            IEnumerable<PinRow> q = allRows;

            if (searchText.Length > 0) q = q.Where(r => (r.Wp.Title ?? "").IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (iconFilter.Count > 0) q = q.Where(r => iconFilter.Contains(WpCommands.SafeIcon(r.Wp.Icon)));
            if (colorFilter.Count > 0) q = q.Where(r => colorFilter.Contains(WpCommands.ColorHex(r.Wp.Color)));
            if (pinnedOnly) q = q.Where(r => r.Wp.Pinned);
            if (radius > 0) q = q.Where(r => r.Dist <= radius);

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
            page = Math.Min(page, MaxPage);
        }

        IEnumerable<PinRow> Sorted<TKey>(IEnumerable<PinRow> q, System.Func<PinRow, TKey> key, IComparer<TKey> cmp)
            => sortAsc ? q.OrderBy(key, cmp) : q.OrderByDescending(key, cmp);

        List<PinRow> SelectedAllRows() => allRows.Where(r => selectedKeys.Contains(r.Key)).ToList();

        void OnPollTick(float dt)
        {
            if (!IsOpened() || batch.Busy) return;

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

            // filter bar
            c.AddStaticText("Search", font, EB(4, y + 4, 52, 25));
            c.AddTextInput(EB(58, y, 165, 28), OnSearchChanged, font, "search");

            var colorHexes = allRows.Select(r => WpCommands.ColorHex(r.Wp.Color)).Distinct().OrderBy(s => s).ToArray();
            var colorNames = colorHexes.Select(h => h + " (" + allRows.Count(r => WpCommands.ColorHex(r.Wp.Color) == h) + ")").ToArray();
            if (colorHexes.Length == 0) { colorHexes = new[] { "#ffffff" }; colorNames = new[] { "#ffffff (0)" }; }
            c.AddMultiSelectDropDown(colorHexes, colorNames, 0, OnColorFilterChanged, EB(233, y, 145, 28), "colorfilter");

            c.AddSwitch(OnPinnedOnlyToggled, EB(390, y, 28, 28), "pinnedonly", 25, 3);
            c.AddStaticText("Pinned only", font, EB(423, y + 4, 110, 25));
            c.AddStaticText("Within", font, EB(541, y + 4, 46, 25));
            c.AddNumberInput(EB(589, y, 72, 28), OnRadiusChanged, font, "radius");
            c.AddStaticText("blocks", font, EB(667, y + 4, 60, 25));

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
            c.AddDynamicText(StatusText(), font.Clone().WithOrientation(EnumTextOrientation.Right), EB(512, y + 4, 338, 24), "status");

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

            // action row B — non-mutating / other
            y += 34;
            c.AddSmallButton("New pin...", () => { OpenNewPin(); return true; }, EB(4, y, 100, 28));
            c.AddSmallButton("Export / Import...", () => { screen = PmScreen.ImportExport; Recompose(); return true; }, EB(110, y, 160, 28));
            c.AddSmallButton($"Recycle bin ({bin.Entries.Count})...", () => { OpenBin(); return true; }, EB(276, y, 160, 28));
            if (config.EnableMapRefresh)
            {
                c.AddSmallButton("Redraw map", () => { ExecuteMapRedraw(); return true; }, EB(442, y, 120, 28));
            }
            c.AddSmallButton("Back to map", () => { OnBackToMap(); return true; }, EB(704, y, 142, 28));
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
            if (radius > 0) c.GetNumberInput("radius").SetValue(radius.ToString("0.#", CultureInfo.InvariantCulture));
            if (colorFilter.Count > 0) c.GetDropDown("colorfilter").SetSelectedValue(colorFilter.ToArray());
        }

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
            string name = "wp" + code.UcFirst();
            if (capi.Gui.Icons.CustomIcons.ContainsKey(name))
            {
                capi.Gui.Icons.DrawIcon(ctx, name, xPx, yPx, sizePx, sizePx, rgba);
            }
            else
            {
                var font = CairoFont.WhiteSmallText();
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, code.Length > 3 ? code.Substring(0, 3) : code, xPx, yPx, false);
            }
        }

        void OnColorFilterChanged(string code, bool selected)
        {
            if (selected) colorFilter.Add(code); else colorFilter.Remove(code);
            ApplyView();
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
            ApplyView();
            UpdateMatrixDynamic();
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
            RefreshData();
            Recompose();
            return true;
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
            int count = Math.Min(PageSize, Math.Max(0, viewRows.Count - start));

            if (count == 0)
            {
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, allRows.Count == 0 ? "No waypoints yet — create one with 'New pin'." : "No pins match the current filters.",
                    GuiElement.scaled(10), GuiElement.scaled(8), false);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var row = viewRows[start + i];
                var wp = row.Wp;
                double ry = i * rh;
                bool isSel = selectedKeys.Contains(row.Key);

                if (isSel)
                {
                    ctx.SetSourceRGBA(0.45, 0.62, 0.3, 0.32);
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

                DrawCell(ctx, font, wp.Title ?? "", ColNameX, ColNameW, ry, rh);
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

                DrawMiniButton(ctx, font, "Edit", ColActX, ry, rh);
                DrawMiniButton(ctx, font, "Map", ColActX + 46, ry, rh);
                DrawMiniButton(ctx, font, "Move", ColActX + 92, ry, rh);
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
            if (rowOnPage < 0 || rowOnPage >= PageSize || idx >= viewRows.Count) { args.Handled = true; return; }

            var row = viewRows[idx];
            double ux = (args.X - tableBounds.absX) / GuiElement.scaled(1);

            if (ux >= ColActX)
            {
                double sub = ux - ColActX;
                if (sub < 44) OpenVanillaEdit(row);
                else if (sub >= 46 && sub < 90) ShowOnMap(row);
                else if (sub >= 92 && sub < 136) OpenMove(row);
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
            if (shift && anchorRow >= 0 && anchorRow < viewRows.Count)
            {
                int a = Math.Min(anchorRow, idx), b = Math.Max(anchorRow, idx);
                for (int i = a; i <= b; i++) selectedKeys.Add(viewRows[i].Key);
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
            new GuiDialogEditWayPoint(capi, layer, svc.Own[index], index).TryOpen();
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
