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

        /// <summary>
        /// The grid this drop was made on. Zero in configs written before this existed, which is
        /// read as "whatever the grid is now" — the old behaviour, and correct for them.
        ///
        /// A cell index only means a position relative to a particular grid: column 16 is four
        /// fifths of the way across a 20-wide grid and two fifths of the way across a 40-wide one.
        /// Keeping the grid alongside the index is what lets the window stay where the player put it
        /// when the grid is resized — and keeping the ORIGINAL rather than rewriting it on every
        /// resize is what stops the position drifting: 20 to 4 and back to 20 lands on the same
        /// column it started on, because nothing in between was ever rounded into the stored value.
        /// </summary>
        public int GridCols { get; set; }
        public int GridRows { get; set; }
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

        /// <summary>
        /// Positions we wrote purely to unlock another mod's title bar, and what we wrote.
        ///
        /// Making a window movable means giving it a stored position, so showing the zones once
        /// leaves a mark on every dialog that was open at the time — including ones the player never
        /// touched. <see cref="ClearUntouchedSeeds"/> takes those back when the zones go.
        /// </summary>
        readonly Dictionary<string, Vec2i> seededPositions = new Dictionary<string, Vec2i>();
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

        // ------------------------------------------------------------------ gui scale

        /// <summary>Vanilla's own slider units: the scale times eight, in whole steps.</summary>
        public const int MinScaleStep = 4;

        /// <summary>
        /// Vanilla allows a taller range on a wide screen (GuiCompositeSettings: 4-24 above 3000px,
        /// 4-16 otherwise). Matched exactly, so this control can never reach a value the game's own
        /// slider would refuse to show.
        /// </summary>
        public int MaxScaleStep() => capi.Render.FrameWidth > 3000 ? 24 : 16;

        public static int ScaleToStep(double scale) => (int)Math.Round(scale * 8);
        public int ClampStep(int step) => Math.Max(MinScaleStep, Math.Min(MaxScaleStep(), step));

        /// <summary>
        /// Sets the game's GUI scale, which is all vanilla's own slider does.
        ///
        /// ClientSettings.Inst is exactly what ICoreClientAPI.Settings returns, so writing the value
        /// fires both watchers the game registers on it: ScreenManager's sets RuntimeEnv.GUIScale,
        /// ClientMain's calls MarkAllDialogsForRecompose. There is nothing else to call - vanilla's
        /// onGuiScaleChanged sets the property and updates its own button widths, nothing more.
        /// </summary>
        public void SetGuiScale(double scale)
        {
            settingOurOwnScale = true;
            try { capi.Settings.Float["guiScale"] = (float)scale; }
            finally { settingOurOwnScale = false; }
        }

        // ---- honouring a scale the player set somewhere else

        /// <summary>
        /// True while *we* are the ones writing the setting, so the watcher below can tell a fit of
        /// ours from a preference of theirs. Without it, every automatic fit would re-capture itself
        /// as the new reference - which is the compounding-drift bug, arrived at from the other side.
        /// </summary>
        bool settingOurOwnScale;

        /// <summary>
        /// Registered exactly once per process, and re-pointed at whichever manager is current.
        ///
        /// ClientSettings is a static singleton that outlives a world, and ISettings offers no
        /// RemoveWatcher - so registering per world would leave a dead watcher behind for every world
        /// joined in a session, each holding a stale config to write to.
        /// </summary>
        static LayoutManager scaleWatchTarget;
        static bool scaleWatcherRegistered;

        /// <summary>
        /// Watches the game's own GUI scale setting so a scale the player set ANYWHERE - vanilla's
        /// Settings > Interface slider, another mod, a hand-edited clientsettings.json - becomes the
        /// new reference.
        ///
        /// Without this the honouring is only half true. Auto-fit already ignores a scale change as
        /// it happens (it answers resolution changes only), so a manual change survives... until the
        /// next resolution change recomputes from the old reference and silently throws it away.
        /// The player would set 1.5x, reconnect at another resolution, and find their choice gone
        /// with nothing on screen explaining why.
        /// </summary>
        public void WatchGuiScale()
        {
            scaleWatchTarget = this;
            if (scaleWatcherRegistered) return;
            scaleWatcherRegistered = true;
            capi.Settings.AddWatcher<float>("guiScale", v => scaleWatchTarget?.OnScaleChangedExternally(v));
        }

        /// <summary>Stops the shared watcher writing into a world that has been left.</summary>
        public void StopWatchingGuiScale()
        {
            if (scaleWatchTarget == this) scaleWatchTarget = null;
        }

        void OnScaleChangedExternally(float value)
        {
            if (settingOurOwnScale) return;
            CaptureScaleBase(value);
        }

        /// <summary>
        /// Remembers "this scale, at this screen size" as the reference every later fit derives from.
        /// </summary>
        public void CaptureScaleBase() => CaptureScaleBase(RuntimeEnv.GUIScale);

        /// <summary>
        /// As above, for a scale handed to us rather than read back.
        ///
        /// The watcher must pass its own value: ScreenManager's watcher is what assigns
        /// RuntimeEnv.GUIScale, and nothing promises it runs before ours - reading RuntimeEnv from
        /// inside a watcher could capture the value being replaced.
        /// </summary>
        public void CaptureScaleBase(double scale)
        {
            if (scale <= 0) scale = 1;
            int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
            if (w <= 0 || h <= 0) return;

            // Nothing to write when nothing moved. A watcher can fire per drag step, and this saves
            // to disk.
            if (Math.Abs(config.LayoutBaseScale - scale) < 0.0001 &&
                config.LayoutBaseScreenW == w && config.LayoutBaseScreenH == h) return;

            config.LayoutBaseScale = scale;
            config.LayoutBaseScreenW = w;
            config.LayoutBaseScreenH = h;
            save?.Invoke();
        }

        /// <summary>The scale auto-fit would choose for the current screen, or null with no base yet.</summary>
        public double? FittedScale() => FittedScale(out _);

        /// <summary>
        /// As <see cref="FittedScale()"/>, also reporting whether the answer had to be clamped.
        ///
        /// Clamping is the one case where the promise breaks. Everything else about this feature is
        /// proportional, and proportion is what keeps the *unscaled* screen size constant - which is
        /// the coordinate space every stored window position lives in, and therefore the reason
        /// windows land where they were. Once the ideal scale falls outside what the game will
        /// accept, that stops being true and windows really can end up off the edge.
        /// </summary>
        public double? FittedScale(out bool clamped)
        {
            clamped = false;
            if (config.LayoutBaseScale <= 0 || config.LayoutBaseScreenW <= 0 || config.LayoutBaseScreenH <= 0) return null;

            int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
            if (w <= 0 || h <= 0) return null;

            // The smaller of the two ratios. Taking height alone would overflow the sides when the
            // aspect ratio changes too, which a remote session is quite capable of doing.
            double ratio = Math.Min((double)w / config.LayoutBaseScreenW,
                                    (double)h / config.LayoutBaseScreenH);
            int ideal = ScaleToStep(config.LayoutBaseScale * ratio);
            int step = ClampStep(ideal);
            clamped = step != ideal;
            return step / 8.0;
        }

        /// <summary>
        /// Re-derives the GUI scale for a screen that has just changed size.
        ///
        /// NOT gated on LayoutEnabled, unlike the zones button, the Z shortcut and the overlay. It
        /// lives on the Map options screen and answers a problem that has nothing to do with snap
        /// zones: no dialog in this engine resizes, so a smaller screen needs a smaller scale whether
        /// or not you arrange anything on a grid. Its own switch is the only thing that gates it.
        ///
        /// Silent when it changes nothing, and it does NOT re-capture the base - that is the whole
        /// reason the result round-trips instead of drifting.
        /// </summary>
        void AutoFitScale()
        {
            if (!config.LayoutAutoScale) return;
            if (capi.Render.FrameWidth <= 0 || capi.Render.FrameHeight <= 0) return;

            double? fitted = FittedScale(out bool clamped);
            if (fitted == null)
            {
                // First run with the feature on: this screen becomes the reference rather than
                // something to correct towards.
                CaptureScaleBase();
                return;
            }

            if (ScaleToStep(fitted.Value) == ScaleToStep(RuntimeEnv.GUIScale)) return;

            SetGuiScale(fitted.Value);
            capi.ShowChatMessage(
                $"[Pin Matrix] Screen is now {capi.Render.FrameWidth}x{capi.Render.FrameHeight} - "
                + $"GUI scale set to {fitted.Value:0.###}. Use the scale slider on the Layout screen "
                + "if that is not the size you want here; moving it re-sets the reference.");

            // Said out loud because it is the one case where windows can still end up off the edge:
            // the proportion that keeps everything in place could not be reached.
            if (clamped)
            {
                capi.ShowChatMessage(
                    "[Pin Matrix] That is as far as the game's own scale range goes, so this screen "
                    + "cannot hold quite what the last one did - some windows may need "
                    + ".pinmatrix rescue.");
            }
        }

        // ------------------------------------------------------------------ rescue

        /// <summary>
        /// Drags every off-screen window back into view, and returns how many it moved.
        ///
        /// The zones already clamp what they place, but a window nobody snapped keeps whatever
        /// absolute position its own mod stored - and those were stored on whatever screen you were
        /// using at the time. Come back on a smaller one and they can sit entirely outside it, with
        /// no title bar left on screen to drag them home by. This is the way back, and it
        /// deliberately does not care whose window it is.
        ///
        /// HUDs are skipped, as everywhere else: they re-assert their own position every tick, so
        /// moving one achieves nothing but a flicker.
        /// </summary>
        public int RescueOffscreen()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            int moved = 0;
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                if (gui.DialogType != EnumDialogType.Dialog) continue;

                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    if (compo == null || !compo.Enabled || compo.Bounds == null) continue;

                    double w = compo.Bounds.OuterWidth / scale;
                    double h = compo.Bounds.OuterHeight / scale;
                    double x = compo.Bounds.absX / scale;
                    double y = compo.Bounds.absY / scale;

                    // Clamped into the screen, but never past a zero origin: a window taller than the
                    // screen gets its top-left pinned at 0 so its title bar is at least reachable.
                    double nx = Clamp(x, 0, Math.Max(0, screenW - w));
                    double ny = Clamp(y, 0, Math.Max(0, screenH - h));
                    if (Math.Abs(nx - x) < 0.5 && Math.Abs(ny - y) < 0.5) continue;

                    MoveComposer(compo, nx, ny);
                    if (!string.IsNullOrEmpty(compo.DialogName))
                    {
                        capi.Gui.SetDialogPosition(compo.DialogName,
                            new Vec2i((int)Math.Round(nx), (int)Math.Round(ny)));
                    }
                    moved++;
                }
            }

            // Straight to disk: the point of this is recovering from a mess, and a mess is exactly
            // the situation where the session might not end tidily.
            if (moved > 0) FlushPositionsNow();
            return moved;
        }

        // ------------------------------------------------------------------ grid & invalidation

        public void Invalidate() => dirty = true;

        /// <summary>
        /// Rebuilds the grid at once instead of waiting for the next watcher tick.
        ///
        /// The Layout screen's status line reads Grid.Cols/Rows, and Invalidate only *marks* the grid
        /// stale — so typing 40 into a box showed "4 x 20 grid": the label was written from the grid
        /// as it stood one keystroke ago, and nothing rewrote it once the rebuild happened. The
        /// overlay was a tick behind for the same reason. Still leaves `dirty` set, so the re-apply
        /// of remembered zones stays on the tick where it can be deferred properly after a scale
        /// change.
        /// </summary>
        public void InvalidateNow()
        {
            dirty = true;
            RebuildGrid();
            RecomputeBlocked();
        }

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

            // Captured before RebuildGrid moves the goalposts: auto-fit answers a *resolution*
            // change only. Reacting to a scale change too would fight the player's own slider, and
            // since setting the scale is itself a scale change, it would also never settle.
            bool resolutionChanged = capi.Render.FrameWidth != lastFrameW ||
                                     capi.Render.FrameHeight != lastFrameH;

            if (dirty || ScreenChanged())
            {
                dirty = false;
                // Before the rebuild, not after: the watchers vanilla registers on "guiScale" run
                // synchronously, so RuntimeEnv.GUIScale is already the new value here and the grid
                // below is built once, at the size it will actually be drawn at.
                if (resolutionChanged) AutoFitScale();
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

                    var seed = new Vec2i((int)Math.Round(b.absX / scale), (int)Math.Round(b.absY / scale));
                    capi.Gui.SetDialogPosition(compo.DialogName, seed);
                    seededPositions[compo.DialogName] = seed;
                    positionsUnflushed = true;
                }
            }
        }

        /// <summary>
        /// Takes back the movable-ness we granted to windows that were never actually moved, and
        /// returns how many.
        ///
        /// WHY THIS IS NOT "PUT EVERYTHING BACK TO FIXED". It cannot be. Decompiled 1.22.7:
        /// GuiElementDialogTitleBar.movable is a private field with no setter, and vanilla's only
        /// route to Fixed — its own title-bar menu — runs the "auto" branch, which restores
        /// parentBoundsBefore and calls SetDialogPosition(name, null). Vanilla's definition of Fixed
        /// is literally "forget where you were put and go back where you started", so making every
        /// window Fixed when the zones are hidden would throw away the arrangement that was the
        /// entire point of showing them. That trade-off was investigated and rejected; see CLAUDE.md.
        ///
        /// What IS safe is the subset where nothing is lost: a window still sitting exactly where we
        /// seeded it has not been moved by anybody, so clearing its seed returns it to Fixed *and*
        /// leaves it precisely where it already is. A window that was dragged, or that has a zone,
        /// keeps its stored position and stays movable — losing those is the thing this must not do.
        ///
        /// The tolerance is not slack for its own sake: a movable title bar rewrites its own stored
        /// position on its first rendered frame, and that round-trips through an int, so a window
        /// nobody touched can come back a pixel out.
        /// </summary>
        public int ClearUntouchedSeeds()
        {
            const int Tolerance = 2;

            var names = new List<string>(seededPositions.Keys);
            int cleared = 0;

            foreach (string name in names)
            {
                var seed = seededPositions[name];
                seededPositions.Remove(name);

                // Ours to place: it keeps its position, and its title bar with it.
                if (Find(name) != null) continue;

                var stored = capi.Gui.GetDialogPosition(name);
                if (stored == null) continue;
                if (Math.Abs(stored.X - seed.X) > Tolerance || Math.Abs(stored.Y - seed.Y) > Tolerance) continue;

                capi.Gui.SetDialogPosition(name, null);
                positionsUnflushed = true;
                cleared++;
            }

            if (cleared > 0) FlushPositionsNow();
            return cleared;
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

        /// <summary>
        /// Why this window may not go in this zone, or null if it may.
        ///
        /// ONE RULE, TWO READERS. Snap enforces it and the overlay paints it, so a cell can never
        /// look droppable and then refuse the drop — or worse, take it and strand the window.
        /// </summary>
        public string WhyCannotSnap(string dialogName, int col, int row, int colSpan = 1, int rowSpan = 1)
        {
            if (string.IsNullOrEmpty(dialogName)) return "That window has no name to remember it by.";

            // The tab row is the button bar's, and the bar is stretched across the whole of it. A
            // window dropped there lands UNDER a full-screen-width strip of our buttons, which sit
            // at DrawOrder 0.97 against the map dialog's 0.11 and therefore take the mouse first —
            // so it is not merely overlapped, it is unclickable, and the only way back out is a
            // layout reset. Refusing the drop is the fix; the row is already tinted as special.
            if (IsButtonRow(row) && dialogName != ButtonsDialogName)
            {
                return "That row is the Pin Matrix tab row — the button bar is stretched across it, "
                     + "so a window dropped there would end up underneath it. Drop it on another row, "
                     + "or move the bar to a different row first.";
            }

            if (config.LayoutAvoidHuds && IsBlocked(col, row, colSpan, rowSpan))
            {
                return "That zone is disabled because a HUD is there. Untick \"Do not cover HUDs\" "
                     + "on the Map windows layout screen to use it anyway.";
            }

            return null;
        }

        /// <summary>
        /// Windows released by the last <see cref="Snap"/> because the button bar landed on top of
        /// them. Read straight after the call: the drop is the only moment a player can be told.
        /// </summary>
        public readonly List<string> LastSnapEvicted = new List<string>();

        /// <summary>Snaps a window into a zone and remembers it. Refuses per <see cref="WhyCannotSnap"/>.</summary>
        public bool Snap(string dialogName, int col, int row, int colSpan = 1, int rowSpan = 1)
        {
            LastSnapEvicted.Clear();
            if (WhyCannotSnap(dialogName, col, row, colSpan, rowSpan) != null) return false;

            // The bar taking a row makes that row the tab row, and the tab row holds nothing else —
            // WhyCannotSnap refuses new drops onto it, but the bar can also arrive on a row that was
            // already occupied. Those windows are released rather than left underneath a full-width
            // strip that takes the mouse before they do; being back where their own mod put them is
            // recoverable, being invisible and unclickable is not.
            if (dialogName == ButtonsDialogName && config.LayoutButtonMode == "row")
            {
                foreach (var other in config.LayoutAssignments.ToArray())
                {
                    if (other.Dialog == dialogName || other.Row != row) continue;
                    LastSnapEvicted.Add(other.Dialog);
                }
                foreach (var name in LastSnapEvicted) Unsnap(name);
            }

            var a = Find(dialogName);
            if (a == null)
            {
                a = new ZoneAssignment { Dialog = dialogName };
                config.LayoutAssignments.Add(a);
            }
            a.Col = col; a.Row = row; a.ColSpan = colSpan; a.RowSpan = rowSpan;
            // The drop is expressed in the grid it was dropped on, and stays that way.
            a.GridCols = Grid.Cols; a.GridRows = Grid.Rows;

            // Windows we place ourselves (the button bar) only need the assignment recorded — the
            // mod system re-places them from it on the next tick, and letting both write the same
            // window is how it ends up twitching between two nearly-equal answers.
            bool ok = SelfPlaced.Contains(dialogName) || ApplyAssignment(a, force: true, ZoneFor(a));
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

        /// <summary>
        /// Un-snaps whatever one window a player's query names, for the chat command.
        ///
        /// Matched case-insensitively against the remembered zones, exact first and then substring,
        /// because the names worth typing are composer DialogNames like "ProspectTogether Settings"
        /// — someone else's, with spaces and capitals nobody should have to reproduce. Ambiguity is
        /// reported rather than guessed at: silently un-snapping the wrong window is a worse outcome
        /// than being asked to be more specific.
        /// </summary>
        public string UnsnapMatching(string query)
        {
            if (config.LayoutAssignments.Count == 0) return "No windows are snapped.";

            if (string.IsNullOrWhiteSpace(query))
            {
                var sb = new StringBuilder("Snapped windows — .pinmatrix unsnap <name> to release one:");
                foreach (var a in config.LayoutAssignments) sb.Append("\n  ").Append(a.Dialog);
                return sb.ToString();
            }

            query = query.Trim();
            var hits = new List<string>();
            foreach (var a in config.LayoutAssignments)
            {
                if (string.Equals(a.Dialog, query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Clear();
                    hits.Add(a.Dialog);
                    break;
                }
                if (a.Dialog != null && a.Dialog.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add(a.Dialog);
                }
            }

            if (hits.Count == 0) return $"No snapped window matches \"{query}\". Run .pinmatrix unsnap with no name to list them.";
            if (hits.Count > 1)
            {
                var sb = new StringBuilder($"\"{query}\" matches {hits.Count} snapped windows — be more specific:");
                foreach (var h in hits) sb.Append("\n  ").Append(h);
                return sb.ToString();
            }

            return Unsnap(hits[0])
                ? $"Released \"{hits[0]}\" — it is back where its own mod put it."
                : $"Could not release \"{hits[0]}\".";
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

        // ------------------------------------------------------------------ the tab row

        /// <summary>The main button window's name — the one whose row nominates the tab strip.</summary>
        public const string ButtonsDialogName = "pinmatrix-buttons";

        /// <summary>
        /// Which row is the tab strip: <b>the row the Pin Matrix button bar is snapped to</b>.
        ///
        /// It used to be a number typed on the Layout screen. Dragging says it better — the bar is
        /// visible, the grid is on screen, and "put the strip here" is one drop rather than counting
        /// rows and typing the index. Until the bar has been dragged anywhere it defaults to the
        /// bottom row, which is where a tab strip almost always wants to be.
        ///
        /// -1 outside tab-row mode: a row that is not currently acting as a strip is an ordinary row
        /// of cells, and must neither be tinted as special nor spread the windows dropped on it.
        /// </summary>
        public int ButtonRowIndex
        {
            get
            {
                if (config.LayoutButtonMode != "row") return -1;
                var a = Find(ButtonsDialogName);
                // Mapped, like every other assignment: the bar's row is four fifths of the way down
                // a 20-row grid, not row 16 of whatever grid happens to be on screen.
                int row = a != null ? MappedRow(a) : Grid.Rows - 1;
                return Math.Min(Grid.Rows - 1, Math.Max(0, row));
            }
        }

        public bool IsButtonRow(int row) => ButtonRowIndex >= 0 && row == ButtonRowIndex;

        /// <summary>True when the tab row is actually in use, for the overlay to tint it.</summary>
        public bool ButtonRowActive => ButtonRowIndex >= 0;

        /// <summary>
        /// The strip the button bar occupies: the whole width of its row, inset by the zone padding.
        ///
        /// A tab row is a bar, and a bar spans its row — that is the whole difference between this
        /// mode and Parallel, which is the same window of side-by-side buttons snapped to one cell.
        /// An intermediate design divided the row into equal slots shared with anything else dropped
        /// on it: the bar shrank the moment another window joined the row, and the slot edges fell at
        /// fractions of the screen rather than on grid lines, so the bar stopped lining up with the
        /// lattice the player had just dropped it onto. The row belongs to the bar. Anything else
        /// dropped on that row snaps to the cell it was dropped on, exactly as on any other row.
        /// </summary>
        public URect ButtonRowRect()
        {
            double pad = config.LayoutZonePadding;
            return new URect(pad,
                             Math.Max(0, ButtonRowIndex) * Grid.CellH + pad,
                             Math.Max(1, Grid.ScreenW - 2 * pad),
                             Math.Max(1, Grid.CellH - 2 * pad));
        }

        /// <summary>The cell the button stack anchors to in "cell" mode.</summary>


        // ------------------------------------------------------------------ regridding

        /// <summary>
        /// One axis of an assignment re-expressed in the current grid: the nearest cell to where it
        /// actually sits, proportionally.
        ///
        /// Rounded, not truncated — a window four fifths of the way across should land on the cell
        /// nearest four fifths, not the one before it. Always computed from the stored grid, never
        /// from the last answer, so repeated resizes cannot walk a window across the screen.
        /// </summary>
        static int MapIndex(int index, int storedCount, int currentCount)
        {
            if (storedCount <= 0 || storedCount == currentCount) return Clamp(index, 0, currentCount - 1);
            return Clamp((int)Math.Round(index * (double)currentCount / storedCount), 0, currentCount - 1);
        }

        static int MapSpan(int span, int storedCount, int currentCount, int from)
        {
            if (storedCount <= 0 || storedCount == currentCount) span = Math.Max(1, span);
            else span = Math.Max(1, (int)Math.Round(span * (double)currentCount / storedCount));
            return Math.Min(span, Math.Max(1, currentCount - from));
        }

        static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        public int MappedCol(ZoneAssignment a) => MapIndex(a.Col, a.GridCols, Grid.Cols);
        public int MappedRow(ZoneAssignment a) => MapIndex(a.Row, a.GridRows, Grid.Rows);

        /// <summary>Where an assignment actually goes, in the grid as it stands now.</summary>
        public URect ZoneFor(ZoneAssignment a)
        {
            int col = MappedCol(a), row = MappedRow(a);
            return Grid.Cell(col, row,
                             MapSpan(a.ColSpan, a.GridCols, Grid.Cols, col),
                             MapSpan(a.RowSpan, a.GridRows, Grid.Rows, row));
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

            double w = compo.Bounds.OuterWidth / scale;
            double h = compo.Bounds.OuterHeight / scale;

            // The button bar in tab-row mode does not land at a corner and keep its width — it
            // stretches over the whole row it is dropped on, so the preview has to say so. Reading
            // the row from the drop rather than from ButtonRowIndex is what makes the outline follow
            // the pointer up and down the grid before the drop has been made.
            if (dialogName == ButtonsDialogName && config.LayoutButtonMode == "row")
            {
                double pad = config.LayoutZonePadding;
                landing = new URect(pad, row * Grid.CellH + pad,
                                    Math.Max(1, Grid.ScreenW - 2 * pad), h);
                return true;
            }

            URect zone = Grid.Cell(col, row);
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
