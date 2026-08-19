using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace PinMatrix
{
    /// <summary>Where one window sits, stored as a grid cell rather than as coordinates.</summary>
    public class ZoneAssignment
    {
        /// <summary>The composer's DialogName — the same key vanilla uses in clientsettings.json.</summary>
        public string Dialog { get; set; }
        public int Col { get; set; }
        public int Row { get; set; }
        public int ColSpan { get; set; } = 1;
        public int RowSpan { get; set; } = 1;
    }

    /// <summary>
    /// Window layout: a FancyZones-style snap grid for the game's dialogs.
    ///
    /// The whole feature rests on one piece of public API that vanilla already uses for its own
    /// movable dialogs: IGuiAPI.SetDialogPosition / GetDialogPosition, persisted per composer name
    /// in clientsettings.json. Moving a window live is the same three lines
    /// GuiElementDialogTitleBar runs while you drag it — set Alignment to None, write fixedX/fixedY,
    /// CalcWorldBounds. No Harmony, no patching, nothing mod-specific.
    ///
    /// Two things this deliberately does NOT do:
    ///
    /// - It never resizes anything, our own window included. Vintage Story has no resizable-dialog
    ///   concept: a composer's size falls out of its child element bounds at compose time, and there
    ///   is no per-dialog scale. Zones move windows; they do not change their size.
    /// - It never moves a HUD. HUDs generally re-assert their own position every tick from their
    ///   own config, so moving them is a fight we would lose and the player would see as jitter.
    ///   They are treated as obstacles instead — see LayoutHudSampler.
    ///
    /// Storage is per machine and always has been: our config goes to ModConfig/pinmatrix.json and
    /// vanilla's positions to clientsettings.json, both under VintagestoryData. Nothing syncs, so a
    /// laptop and a desktop keep independent layouts.
    /// </summary>
    public class LayoutManager
    {
        /// <summary>Enough of an ElementBounds to put a window back where its own mod wanted it.</summary>
        class BoundsSnapshot
        {
            public EnumDialogArea Alignment;
            public double FixedX, FixedY, OffsetX, OffsetY, MarginX, MarginY;
        }

        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;
        readonly Action save;

        readonly LayoutHudSampler huds = new LayoutHudSampler();
        readonly HashSet<int> blocked = new HashSet<int>();
        readonly Dictionary<string, BoundsSnapshot> originals = new Dictionary<string, BoundsSnapshot>();

        /// <summary>
        /// The ElementBounds instance we last positioned, per dialog name. Reference identity is the
        /// recompose detector: when a mod rebuilds its dialog it constructs fresh bounds, so a
        /// different instance means "it was rebuilt, re-apply". The same instance means the only
        /// thing that could have moved it is the player dragging it — and we leave that alone.
        /// </summary>
        readonly Dictionary<string, ElementBounds> appliedTo = new Dictionary<string, ElementBounds>();

        /// <summary>Our own dialogs, excluded from HUD obstacle scanning.</summary>
        public readonly HashSet<GuiDialog> OwnDialogs = new HashSet<GuiDialog>();

        /// <summary>
        /// Dialog names whose placement is owned elsewhere — the button windows, which the mod
        /// system positions per mode. Snap still records and stores their zone, but the per-tick
        /// re-apply skips them: two things writing one window's position is how it ends up
        /// twitching between two nearly-equal answers.
        /// </summary>
        public readonly HashSet<string> SelfPlaced = new HashSet<string>();

        public ZoneGrid Grid { get; private set; }

        bool dirty = true;
        double lastFrameW, lastFrameH;
        float lastScale;

        /// <summary>Set when we have written positions that vanilla has not been asked to flush yet.</summary>
        bool positionsUnflushed;
        long lastFlushAt;

        /// <summary>Don't rewrite the settings file more than this often.</summary>
        const long FlushIntervalMs = 5000;

        public LayoutManager(ICoreClientAPI capi, PinMatrixConfig config, Action save)
        {
            this.capi = capi;
            this.config = config;
            this.save = save;

            RebuildGrid();

            // GUI scale and font size both change geometry, and both have proper setting watchers,
            // so neither needs polling. fontSize is the easy one to forget: it moves nothing, but
            // autosized dialogs are FitToChildren, so text width changes their actual size — which
            // shifts both the HUD rects we sample and how a window fits its zone.
            capi.Settings.AddWatcher<float>("guiScale", _ => Invalidate());
            capi.Settings.AddWatcher<float>("fontSize", _ => Invalidate());
            // Resolution has no event at all — IClientEventAPI offers ReloadShader/Textures/Shapes
            // and nothing for a window resize — so it is polled in Tick, two float comparisons.
        }

        // ------------------------------------------------------------------ grid & invalidation

        public void Invalidate() => dirty = true;

        void RebuildGrid()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            lastFrameW = capi.Render.FrameWidth;
            lastFrameH = capi.Render.FrameHeight;
            lastScale = scale;

            Grid = new ZoneGrid(config.LayoutCols, config.LayoutRows,
                                lastFrameW / scale, lastFrameH / scale, config.LayoutZonePadding);
        }

        bool ScreenChanged() =>
            capi.Render.FrameWidth != lastFrameW ||
            capi.Render.FrameHeight != lastFrameH ||
            RuntimeEnv.GUIScale != lastScale;

        /// <summary>Called on the 250ms watcher tick.</summary>
        public void Tick()
        {
            huds.Sample(capi, OwnDialogs);

            if (dirty || ScreenChanged())
            {
                dirty = false;
                RebuildGrid();
                RecomputeBlocked();
                // Vanilla needs a beat to finish recomposing after a scale change before the sizes
                // we would measure are the new ones, so re-apply on the next callback rather than
                // synchronously (same deferral trick as the focus call in GuiDialogPinMatrix).
                capi.Event.RegisterCallback(_ => ApplyAll(force: true), 50);
                return;
            }

            ApplyAll(force: false);
            FlushPositionsIfNeeded();
        }

        /// <summary>
        /// Makes the game actually persist the window positions we asked it to remember.
        ///
        /// IGuiAPI.SetDialogPosition only writes ClientSettings' in-memory dictionary — it does not
        /// mark the settings store dirty, so nothing reaches clientsettings.json and the positions
        /// are lost on exit. That is why windows snap back to their defaults once this mod is
        /// removed: nothing was ever saved natively, whatever the API's name suggests.
        ///
        /// Writing the backing setting back over itself marks the store dirty, so vanilla's own
        /// save includes the dialog positions. It touches the very setting being changed rather
        /// than inventing a scratch key, and it is debounced because the alternative is rewriting
        /// the settings file several times a second.
        ///
        /// Consequence worth keeping: positions then outlive this mod. Uninstall it and your windows
        /// stay where you put them, which is the point — the layout grid is a tool for arranging
        /// them, not a runtime they depend on.
        /// </summary>
        void FlushPositionsIfNeeded()
        {
            if (!positionsUnflushed) return;
            long now = capi.World.ElapsedMilliseconds;
            if (now - lastFlushAt < FlushIntervalMs) return;

            lastFlushAt = now;
            positionsUnflushed = false;
            try
            {
                capi.Settings.Strings["dialogPositions"] = capi.Settings.Strings["dialogPositions"];
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[pinmatrix] could not mark dialog positions dirty: {0}", e.Message);
            }
        }

        /// <summary>
        /// Makes open windows draggable, so showing the zones is all it takes to start arranging.
        ///
        /// Vanilla dialogs default to "Fixed" and only become movable once a position is stored for
        /// them — GuiElementDialogTitleBar reads GetDialogPosition on its first compose and flips
        /// itself movable when it finds one. Storing each window's *current* position therefore
        /// unlocks dragging without moving anything, since the position it restores to is the one
        /// it already has.
        ///
        /// Existing positions are never overwritten: if the player has already placed a window, that
        /// is theirs to keep.
        ///
        /// Caveat worth knowing: a title bar initialises once, so a window that is already on screen
        /// becomes draggable at its next compose rather than instantly. Anything opened after the
        /// zones are shown is draggable immediately.
        /// </summary>
        public void MakeOpenDialogsMovable()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                if (gui.DialogType != EnumDialogType.Dialog) continue;   // HUDs are never ours to move
                if (OwnDialogs.Contains(gui)) continue;

                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    var b = compo?.Bounds;
                    if (compo == null || !compo.Enabled || b == null || compo.DialogName == null) continue;
                    if (capi.Gui.GetDialogPosition(compo.DialogName) != null) continue;

                    capi.Gui.SetDialogPosition(compo.DialogName,
                        new Vec2i((int)Math.Round(b.absX / scale), (int)Math.Round(b.absY / scale)));
                    positionsUnflushed = true;
                }
            }
        }

        /// <summary>Flushes immediately, for shutdown and for the moment the player stops arranging.</summary>
        public void FlushPositionsNow()
        {
            positionsUnflushed = true;
            lastFlushAt = 0;
            FlushPositionsIfNeeded();
        }

        /// <summary>
        /// Freezes the current stable HUD rects and recomputes which cells they veto. Called on
        /// invalidation and whenever the overlay opens — never per frame, so the blocked set cannot
        /// flicker while a live HUD resizes itself.
        /// </summary>
        public void RecomputeBlocked()
        {
            blocked.Clear();
            huds.Commit();
            if (!config.LayoutAvoidHuds) return;

            double threshold = config.LayoutHudCoverageThreshold / 100.0;
            for (int row = 0; row < Grid.Rows; row++)
            {
                for (int col = 0; col < Grid.Cols; col++)
                {
                    if (huds.CoverageOf(Grid.Cell(col, row)) >= threshold) blocked.Add(Grid.Index(col, row));
                }
            }
        }

        public bool IsBlocked(int col, int row) => blocked.Contains(Grid.Index(col, row));

        /// <summary>A multi-cell zone is unavailable if any cell it spans is blocked.</summary>
        public bool IsBlocked(int col, int row, int colSpan, int rowSpan)
        {
            for (int r = row; r < row + rowSpan && r < Grid.Rows; r++)
                for (int c = col; c < col + colSpan && c < Grid.Cols; c++)
                    if (blocked.Contains(Grid.Index(c, r))) return true;
            return false;
        }

        public int BlockedCount => blocked.Count;
        public int StableHudCount => huds.StableCount;

        // ------------------------------------------------------------------ finding & moving

        /// <summary>Finds an open composer by its DialogName. Null when that window is not open.</summary>
        public GuiComposer FindComposer(string dialogName)
        {
            if (string.IsNullOrEmpty(dialogName)) return null;
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    if (compo != null && compo.Enabled && compo.DialogName == dialogName) return compo;
                }
            }
            return null;
        }

        /// <summary>
        /// Moves a composer to an unscaled position. Mirrors what GuiElementDialogTitleBar does on
        /// drag: drop the alignment, clear the offsets and margins that would fight fixedX/fixedY,
        /// then recalculate.
        /// </summary>
        static void MoveComposer(GuiComposer compo, double ux, double uy)
        {
            var b = compo.Bounds;
            b.Alignment = EnumDialogArea.None;
            b.fixedOffsetX = 0;
            b.fixedOffsetY = 0;
            b.absMarginX = 0;
            b.absMarginY = 0;
            b.fixedX = ux;
            b.fixedY = uy;
            b.MarkDirtyRecursive();
            b.CalcWorldBounds();
        }

        static BoundsSnapshot Snapshot(ElementBounds b) => new BoundsSnapshot
        {
            Alignment = b.Alignment,
            FixedX = b.fixedX,
            FixedY = b.fixedY,
            OffsetX = b.fixedOffsetX,
            OffsetY = b.fixedOffsetY,
            MarginX = b.absMarginX,
            MarginY = b.absMarginY,
        };

        static void Restore(ElementBounds b, BoundsSnapshot s)
        {
            b.Alignment = s.Alignment;
            b.fixedX = s.FixedX;
            b.fixedY = s.FixedY;
            b.fixedOffsetX = s.OffsetX;
            b.fixedOffsetY = s.OffsetY;
            b.absMarginX = s.MarginX;
            b.absMarginY = s.MarginY;
            b.MarkDirtyRecursive();
            b.CalcWorldBounds();
        }

        // ------------------------------------------------------------------ assignments

        public ZoneAssignment Find(string dialogName)
        {
            foreach (var a in config.LayoutAssignments)
            {
                if (a.Dialog == dialogName) return a;
            }
            return null;
        }

        /// <summary>Snaps a window into a zone and remembers it. Refuses zones a HUD is sitting in.</summary>
        public bool Snap(string dialogName, int col, int row, int colSpan = 1, int rowSpan = 1)
        {
            if (string.IsNullOrEmpty(dialogName)) return false;
            if (config.LayoutAvoidHuds && IsBlocked(col, row, colSpan, rowSpan)) return false;

            var a = Find(dialogName);
            if (a == null)
            {
                a = new ZoneAssignment { Dialog = dialogName };
                config.LayoutAssignments.Add(a);
            }
            a.Col = col; a.Row = row; a.ColSpan = colSpan; a.RowSpan = rowSpan;

            bool ok = ApplyAssignment(a, force: true, ZoneFor(a));
            // The strip re-spaces itself when something joins or leaves it.
            if (IsButtonRow(row)) ApplyAll(force: true);
            save?.Invoke();
            return ok;
        }

        /// <summary>Forgets one window's zone and puts it back where its own mod wanted it.</summary>
        public bool Unsnap(string dialogName)
        {
            var a = Find(dialogName);
            if (a == null) return false;
            config.LayoutAssignments.Remove(a);
            RestoreOne(dialogName);
            save?.Invoke();
            return true;
        }

        void RestoreOne(string dialogName)
        {
            // Vanilla's own un-stick: this is exactly what the title bar's "Fixed" menu option does.
            // Clearing it matters more than it looks — the title bar reads GetDialogPosition on its
            // first compose, so leaving an entry behind would silently restore our position and make
            // the reset look broken.
            capi.Gui.SetDialogPosition(dialogName, null);
            positionsUnflushed = true;

            var compo = FindComposer(dialogName);
            if (compo != null && originals.TryGetValue(dialogName, out var snap)) Restore(compo.Bounds, snap);

            originals.Remove(dialogName);
            appliedTo.Remove(dialogName);
        }

        /// <summary>Clears every remembered zone. The recovery path when something has gone wrong.</summary>
        public int ResetAll()
        {
            var names = new List<string>();
            foreach (var a in config.LayoutAssignments) names.Add(a.Dialog);

            config.LayoutAssignments.Clear();
            foreach (var n in names) RestoreOne(n);

            originals.Clear();
            appliedTo.Clear();
            save?.Invoke();
            return names.Count;
        }

        public void ApplyAll(bool force)
        {
            if (!config.LayoutEnabled) return;
            for (int i = 0; i < config.LayoutAssignments.Count; i++)
            {
                var a = config.LayoutAssignments[i];
                if (SelfPlaced.Contains(a.Dialog)) continue;
                ApplyAssignment(a, force, ZoneFor(a));
            }
        }

        // ------------------------------------------------------------------ the dock row

        /// <summary>
        /// Is this row the nominated tab strip?
        ///
        /// Only in tab-row mode. The row number is remembered in every mode so switching back does
        /// not lose it, but a row that is not currently being used as a strip is just an ordinary
        /// row of cells — it must not be tinted as special, and dropping a window on it must snap
        /// normally rather than being spread along a strip that is not in use.
        /// </summary>
        public bool IsButtonRow(int row) =>
            config.LayoutButtonMode == "row" && config.LayoutButtonRow >= 0 && row == config.LayoutButtonRow;

        /// <summary>True when the dock row is actually in use, for the overlay to tint it.</summary>
        public bool ButtonRowActive => config.LayoutButtonMode == "row" && config.LayoutButtonRow >= 0;

        /// <summary>
        /// Everything currently assigned to the dock row, in the order it will be laid out. The
        /// column something was dropped on is only an ordering key here, not a position — that is
        /// what makes the strip stay evenly spaced as items are added and removed. Ties break on
        /// name so the order cannot wobble between frames.
        /// </summary>
        List<ZoneAssignment> DockedItems()
        {
            var docked = new List<ZoneAssignment>();
            if (!ButtonRowActive) return docked;

            foreach (var a in config.LayoutAssignments)
            {
                if (a.Row == config.LayoutButtonRow) docked.Add(a);
            }
            docked.Sort((x, y) =>
            {
                int byCol = x.Col.CompareTo(y.Col);
                return byCol != 0 ? byCol : string.CompareOrdinal(x.Dialog, y.Dialog);
            });
            return docked;
        }

        /// <summary>The slot an item gets on the dock row: the row split into n equal parts.</summary>
        URect DockSlot(int index, int count)
        {
            double pad = config.LayoutZonePadding;
            double slotW = Grid.ScreenW / Math.Max(1, count);
            return new URect(
                index * slotW + pad,
                config.LayoutButtonRow * Grid.CellH + pad,
                slotW - 2 * pad,
                Grid.CellH - 2 * pad);
        }

        /// <summary>
        /// The slot on the button row reserved for our own map-screen button stack. It always takes
        /// the first slot, and counts as one strip item alongside anything else docked there.
        /// </summary>
        public URect OwnButtonDock()
        {
            if (!ButtonRowActive) return Grid.Cell(0, 0);
            return DockSlot(0, DockedItems().Count + 1);
        }

        /// <summary>The cell the button stack anchors to in "cell" mode.</summary>
        public URect OwnButtonCell() => Grid.Cell(config.LayoutButtonCol, config.LayoutButtonCellRow);

        /// <summary>Where an assignment actually goes — a dock slot if it is on the strip, else its cell.</summary>
        public URect ZoneFor(ZoneAssignment a)
        {
            if (IsButtonRow(a.Row))
            {
                var docked = DockedItems();
                int idx = docked.FindIndex(d => d.Dialog == a.Dialog);
                // Slot 0 belongs to our own button stack (OwnButtonDock), so everything else starts
                // at 1 and the strip is sized for one more item than the assignment list holds.
                if (idx >= 0) return DockSlot(idx + 1, docked.Count + 1);
            }
            return Grid.Cell(a.Col, a.Row, a.ColSpan, a.RowSpan);
        }

        /// <summary>
        /// The rectangle a window would actually occupy if dropped on this cell right now, used by
        /// the overlay to preview the landing spot. On a fine lattice a single cell is much smaller
        /// than most windows, so showing the cell alone would tell the player almost nothing.
        /// </summary>
        public bool TryLandingRect(string dialogName, int col, int row, out URect landing)
        {
            landing = default;
            var compo = FindComposer(dialogName);
            if (compo == null) return false;

            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            URect zone;
            if (IsButtonRow(row))
            {
                var docked = DockedItems();
                int idx = docked.FindIndex(d => d.Dialog == dialogName);
                // A newcomer joins the strip, so the slots get one narrower.
                zone = idx >= 0 ? DockSlot(idx + 1, docked.Count + 1) : DockSlot(docked.Count + 1, docked.Count + 2);
            }
            else
            {
                zone = Grid.Cell(col, row);
            }

            double w = compo.Bounds.OuterWidth / scale;
            double h = compo.Bounds.OuterHeight / scale;
            double x = Clamp(zone.X, 0, Math.Max(0, Grid.ScreenW - w));
            double y = Clamp(zone.Y, 0, Math.Max(0, Grid.ScreenH - h));
            landing = new URect(x, y, w, h);
            return true;
        }

        bool ApplyAssignment(ZoneAssignment a, bool force, URect zone)
        {
            var compo = FindComposer(a.Dialog);
            if (compo == null) return false;

            // Not forced and the bounds instance is the one we already positioned: nothing has been
            // rebuilt, so any movement since is the player's own drag. Leave it be.
            if (!force && appliedTo.TryGetValue(a.Dialog, out var prev) && ReferenceEquals(prev, compo.Bounds)) return false;

            // Capture the mod's own placement the first time we ever touch it, so reset can undo it
            // properly. Only trustworthy while vanilla has no stored position of its own — once it
            // does, the bounds we can see may already be a restored one rather than the default.
            if (!originals.ContainsKey(a.Dialog) && capi.Gui.GetDialogPosition(a.Dialog) == null)
            {
                originals[a.Dialog] = Snapshot(compo.Bounds);
            }

            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            // No window is ever resized, our own included. Vintage Story has no resizable-dialog
            // concept, and the one exception we did have — rebuilding our own table at a different
            // row and column count to fill a zone — was more disruptive than useful. Zones move
            // windows; they do not change their size.
            double w = compo.Bounds.OuterWidth / scale;
            double h = compo.Bounds.OuterHeight / scale;

            // Top-left anchoring, not centring. One predictable rule ("the corner lands here") beats
            // a rule that silently changes behaviour with window size, and on a fine lattice it is
            // the only one that means anything — a single cell is far smaller than most windows.
            double x = zone.X;
            double y = zone.Y;

            // Never off-screen, whatever the zone maths said — a window you cannot reach is worse
            // than one that overlaps something.
            x = Clamp(x, 0, Math.Max(0, Grid.ScreenW - w));
            y = Clamp(y, 0, Math.Max(0, Grid.ScreenH - h));

            MoveComposer(compo, x, y);

            // Hand the position to vanilla's own store as well, so dialogs with a title bar restore
            // themselves on their next compose without us being involved at all.
            capi.Gui.SetDialogPosition(a.Dialog, new Vec2i((int)Math.Round(x), (int)Math.Round(y)));
            positionsUnflushed = true;
            appliedTo[a.Dialog] = compo.Bounds;
            return true;
        }

        static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        /// <summary>
        /// Names every measured HUD rect overlapping a cell, with how much of it each covers. The
        /// answer to "why is this cell red when there is nothing there" — which is a real question,
        /// because vanilla keeps invisible HUDs permanently open (see LayoutHudSampler).
        /// </summary>
        public string ExplainCell(int col, int row)
        {
            if (col < 0 || row < 0 || col >= Grid.Cols || row >= Grid.Rows)
                return $"[Pin Matrix] Cell {col},{row} is outside the {Grid.Cols}x{Grid.Rows} grid.";

            var cell = Grid.Cell(col, row);
            var sb = new StringBuilder();
            sb.AppendLine($"[Pin Matrix] Cell {col},{row} = {cell} unscaled. "
                        + $"Coverage {huds.CoverageOf(cell) * 100:0.#}%, threshold {config.LayoutHudCoverageThreshold}%, "
                        + $"blocked={IsBlocked(col, row)}");

            bool any = false;
            foreach (var hr in huds.Rects)
            {
                double overlap = hr.Rect.OverlapArea(cell);
                if (overlap <= 0) continue;
                any = true;
                sb.AppendLine($"    {hr.Key} rect={hr.Rect} covers {overlap / cell.Area * 100:0.#}%");
            }
            if (!any) sb.AppendLine("    nothing measured overlaps this cell");
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>
        /// Every open window with the facts the layout system decides on. This is the support tool:
        /// when someone reports that a mod's panel will not snap, this output says whether we can
        /// see it, what we think it is, and whether a position was ever stored for it.
        /// </summary>
        public string Dump()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            var sb = new StringBuilder();
            sb.AppendLine($"[Pin Matrix] screen {capi.Render.FrameWidth}x{capi.Render.FrameHeight} px, "
                        + $"guiScale {scale:0.##}, fontSize {capi.Settings.Float["fontSize"]:0.##} "
                        + $"-> {Grid.ScreenW:0}x{Grid.ScreenH:0} unscaled");
            sb.AppendLine($"[Pin Matrix] grid {Grid.Cols}x{Grid.Rows}, cell {Grid.CellW:0}x{Grid.CellH:0} unscaled, "
                        + $"{blocked.Count} blocked, {huds.StableCount} stable HUD rects, "
                        + $"avoidHuds={config.LayoutAvoidHuds} threshold={config.LayoutHudCoverageThreshold}%");

            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    var b = compo?.Bounds;
                    if (compo == null || b == null) continue;

                    string name = compo.DialogName ?? "(unnamed)";
                    var stored = capi.Gui.GetDialogPosition(name);
                    var zone = Find(name);

                    sb.AppendLine(
                        $"  {gui.DialogType,-6} {name,-34} key={pair.Key,-18} "
                        + $"align={b.Alignment,-12} rect={b.absX / scale:0},{b.absY / scale:0} "
                        + $"{b.OuterWidth / scale:0}x{b.OuterHeight / scale:0} "
                        + $"enabled={compo.Enabled} "
                        + $"stored={(stored == null ? "-" : stored.X + "," + stored.Y)} "
                        + $"zone={(zone == null ? "-" : zone.Col + "," + zone.Row)}");
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
