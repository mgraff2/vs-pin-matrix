using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace PinMatrix
{
    public class PinMatrixModSystem : ModSystem
    {
        ICoreClientAPI capi;
        PinMatrixConfig config;
        WaypointService svc;
        BatchEngine batch;
        RecycleBin bin;
        WaypointVisibility visibility;
        PinSetService pinSets;
        HudPinSetPanel setPanel;
        GuiDialogPinMatrix dialog;
        readonly List<HudPinMatrixButtonWindow> buttons = new List<HudPinMatrixButtonWindow>();
        ChatShareLinks chatShareLinks;
        LayoutManager layout;
        TraderMarkers traders;
        TranslocatorPaths tlPaths;
        HudLayoutOverlay layoutOverlay;
        WaypointVisibilityRenderer visibilityRenderer;
        long mapWatchListenerId;
        long visibilityListenerId;
        bool layoutPinned;
        bool layerOrdered;
        string builtForMode;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            ColorSwatchComponent.EnsureTagRegistered();
            IconGlyphComponent.EnsureTagRegistered();

            try
            {
                config = capi.LoadModConfig<PinMatrixConfig>("pinmatrix.json") ?? new PinMatrixConfig();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Bad config file, using defaults: {0}", e.Message);
                config = new PinMatrixConfig();
            }
            config.Clamp();
            capi.StoreModConfig(config, "pinmatrix.json");

            // Registered unbound by default: the map-screen button is the primary entry point,
            // and players can assign a key of their choice in Settings > Controls.
            capi.Input.RegisterHotKey("pinmatrix", "Pin Matrix (waypoint manager)", GlKeys.Unknown, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("pinmatrix", OnHotkey);

            layout = new LayoutManager(capi, config, () => capi.StoreModConfig(config, "pinmatrix.json"));
            RegisterCommands();

            // The mod's only registration into another system's list. Safe here: WorldMapManager
            // instantiates registered layers at LevelFinalize, well after StartClientSide. The
            // layer draws nothing at all until the feature is switched on and a hop is recorded,
            // and it is registered unconditionally — no "is some mod present" branch, in keeping
            // with every other cross-mod behaviour in this codebase.
            TranslocatorPathLayer.Config = config;
            capi.ModLoader.GetModSystem<WorldMapManager>()?.RegisterMapLayer<TranslocatorPathLayer>("pinmatrixtl", 1.1);

            mapWatchListenerId = capi.Event.RegisterGameTickListener(OnMapWatchTick, 250);
            visibilityListenerId = capi.Event.RegisterGameTickListener(OnVisibilityTick, 20);
            // Re-hiding the switched-off pins is NOT on the tick above, and must not be moved onto
            // it: it is a race against the render frame rather than against wall-clock, so it runs
            // once per frame just before the GUI draws. See WaypointVisibilityRenderer for why.
            visibilityRenderer = new WaypointVisibilityRenderer(() => visibility);
            capi.Event.RegisterRenderer(visibilityRenderer, EnumRenderStage.Ortho, "pinmatrix-hidden-pins");
            chatShareLinks = new ChatShareLinks(capi);
        }

        /// <summary>Shows/hides the "Pin Matrix Editor" button in sync with the full world map dialog.</summary>
        /// <summary>Keeps the translocator paths and the zone overlay in step with the world.</summary>
        void OnVisibilityTick(float dt)
        {
            if (capi.World?.Player == null) return;
            EnsureServices();
            EnsureTlPaths().Tick();
            UpdateLayoutOverlay();
        }

        /// <summary>
        /// Keeps the zone overlay in sync with the toggle. There is no global hotkey for it: the
        /// map-screen "Layout Zones (Z)" button and its map-only Z shortcut are the entry points,
        /// so nothing of ours competes for a binding in a heavily modded controls list.
        ///
        /// The overlay lives and dies with the world map. Zones exist to arrange the map screen, so
        /// leaving it — Escape, or opening the editor, which closes the map — switches them off
        /// rather than leaving a grid hanging over whatever comes next. Switching *off* rather than
        /// merely hiding matters: the next map open then starts clean, and the button label cannot
        /// disagree with what is on screen.
        ///
        /// "Layout Options" deliberately leaves the map open, so configuring the grid does not
        /// dismiss the grid being configured.
        /// </summary>
        void UpdateLayoutOverlay()
        {
            if (!FullMapOpen() && layoutPinned)
            {
                layoutPinned = false;
                RefreshButtons();
            }

            bool want = config.LayoutEnabled && layoutPinned && FullMapOpen();
            bool isOpen = layoutOverlay != null && layoutOverlay.IsOpened();
            if (want == isOpen) return;

            if (want)
            {
                if (layoutOverlay == null) layoutOverlay = new HudLayoutOverlay(capi, config, layout);
                layoutOverlay.TryOpen();
            }
            else if (layoutOverlay != null)
            {
                layoutOverlay.TryClose();
            }
        }

        bool FullMapOpen()
        {
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            return mapDlg != null && mapDlg.IsOpened() && mapDlg.DialogType == EnumDialogType.Dialog;
        }

        /// <summary>
        /// Re-runs button placement immediately, so toggling layout off visibly puts the buttons
        /// back instead of leaving them docked until the next time the map is opened.
        /// </summary>
        public void RefreshButtonPlacement() => RefreshButtons();

        /// <summary>
        /// Turns the zone overlay on or off. Entry point for the map button, Z, the Layout screen
        /// and chat. Pinning requires the feature to be on — otherwise the overlay would refuse to
        /// show while the buttons re-labelled to "Hide Zones", each half telling a different story.
        /// </summary>
        public void SetLayoutPinned(bool on)
        {
            layoutPinned = on && config.LayoutEnabled;
            UpdateLayoutOverlay();
            RefreshButtons();
        }

        void ToggleLayoutZones() => SetLayoutPinned(!layoutPinned);

        /// <summary>Whether the zone overlay is currently showing; read by the Layout screen.</summary>
        public bool LayoutPinned => layoutPinned;

        void OnMapWatchTick(float dt)
        {
            if (capi.World?.Player == null) return;

            // Samples HUD rects, notices resolution/scale changes, and re-applies remembered zones
            // to windows that have been rebuilt since we last positioned them.
            layout.Tick();

            // Showing the zones is a statement of intent: the player wants to rearrange. Unlock
            // dragging for them rather than making them find "Movable" in each window's title menu.
            // Re-run each tick so windows opened after the zones appeared are covered too.
            if (layoutPinned && config.LayoutEnabled) layout.MakeOpenDialogsMovable();
            PositionButtons();
            if (!layerOrdered)
            {
                // Layers only exist after LevelFinalize, so this is done on the first watcher tick
                // rather than at registration time.
                TranslocatorPathLayer.OrderBehindWaypoints(capi);
                layerOrdered = true;
            }
            // Cheap and returns immediately while trader auto-marking is switched off.
            EnsureTraders().Scan();

            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            var mapDlg = mapManager?.worldMapDlg;
            bool fullMapOpen = mapDlg != null && mapDlg.IsOpened() && mapDlg.DialogType == EnumDialogType.Dialog;

            if (fullMapOpen)
            {
                // One pass over the waypoints for every set's count, ahead of the labels that read
                // it. Only while the map is open, because that is the only time a label is on screen.
                pinSets?.Recount();
                // The panel can be the first thing in the session to paint a waypoint icon — the
                // editor need never have been opened — and an unloaded SVG paints as a blank chip.
                if (pinSets != null && pinSets.Buttoned.Any(s => s.UsesIconButton))
                {
                    WaypointIconAssets.EnsureLoaded(capi);
                }
                EnsureButtons();
                UpdateSetPanel(true);
                foreach (var b in buttons)
                {
                    // A window with nothing visible in it must never be opened: its background sizes
                    // itself to its children and throws when there are none. The Options-only window
                    // empties whenever the zones are hidden, so this is a live case, not a theory.
                    bool wanted = !b.IsEmpty;

                    if (wanted && !b.IsOpened()) b.TryOpen();
                    else if (!wanted && b.IsOpened()) b.TryClose();
                    if (b.IsOpened()) b.RefreshIfNeeded();
                }
            }
            else
            {
                foreach (var b in buttons) if (b.IsOpened()) b.TryClose();
                UpdateSetPanel(false);
            }
        }

        /// <summary>
        /// Shows the pin-set filter panel while the full map is open, and only then.
        ///
        /// It is the map's own furniture — a column of on/off rows down the right, in the spirit of
        /// the Terrain / Waypoints / Prospecting toggles on the left — so it lives and dies with the
        /// map exactly as those do. Unlike the map-screen buttons it is NOT gated on the layout
        /// feature: it replaces no space that was not already free, and a filter list is not the kind
        /// of thing a player should have to opt into a window manager to get.
        /// </summary>
        void UpdateSetPanel(bool mapOpen)
        {
            if (!mapOpen)
            {
                if (setPanel != null && setPanel.IsOpened()) setPanel.TryClose();
                return;
            }

            EnsureServices();
            if (setPanel == null)
            {
                setPanel = new HudPinSetPanel(capi, config, pinSets, ToggleSet,
                    () => capi.StoreModConfig(config, "pinmatrix.json"));
                // OwnDialogs keeps it out of the HUD obstacle scan; SelfPlaced keeps the layout
                // manager's per-tick re-apply off it. It is not a snap target — see the class doc.
                layout.OwnDialogs.Add(setPanel);
                layout.SelfPlaced.Add(HudPinSetPanel.PanelDialogName);
                // A development build did let it be snapped. Drop any zone left over from one, so an
                // old config cannot strand the panel away from the map edge it now always follows.
                layout.Unsnap(HudPinSetPanel.PanelDialogName);
            }

            // Nothing saved means nothing to filter: an empty panel is an advert for a feature, and
            // the map screen has no room for one.
            if (setPanel.IsEmpty)
            {
                if (setPanel.IsOpened()) setPanel.TryClose();
                return;
            }

            if (!setPanel.IsOpened()) setPanel.TryOpen();

            // Always beside the map, re-measured each tick: the panel belongs to the map's right
            // edge, and that edge moves with GUI scale and window size.
            setPanel.AnchorToMap();
            setPanel.RefreshIfNeeded();
        }

        /// <summary>
        /// Builds the button windows for the current placement mode, rebuilding when the mode
        /// changes. All that differs between modes is how many windows there are and which way
        /// their buttons run:
        ///
        ///   Fixed cell — one window, buttons in a column, snapped to a cell
        ///   Tab row    — one window, buttons in a row, stretched across the row it is dropped on
        ///   Floating   — one window per button, each placed independently
        ///
        /// Every one is an ordinary named window, so the zone grid and the saved-position store
        /// handle them exactly as they handle any other window.
        /// </summary>
        void EnsureButtons()
        {
            // `mode` is compared against "row"/"parallel"/"float" all through this method. It must
            // hold nothing but the mode: folding anything else into it (a set-list signature, during
            // 1.6.0 development) makes every one of those comparisons false and silently collapses
            // every placement mode to stacked.
            string mode = ButtonMode();
            if (buttons.Count > 0 && builtForMode == mode) return;

            foreach (var b in buttons)
            {
                if (b.IsOpened()) b.TryClose();
                layout.OwnDialogs.Remove(b);
                b.Dispose();
            }
            buttons.Clear();
            builtForMode = mode;

            Func<bool> zonesVisible = () => layoutPinned;
            Func<bool> layoutModeOn = () => layoutPinned && config.LayoutEnabled;
            System.Func<PmButton, Action> actionFor = which =>
                which == PmButton.Editor ? (Action)OpenFromMapButton
                : which == PmButton.Zones ? (Action)ToggleLayoutZones
                : (Action)OpenLayoutOptions;

            var all = new[] { PmButton.Editor, PmButton.Zones, PmButton.Options };

            if (mode == "float")
            {
                foreach (var which in all)
                {
                    buttons.Add(new HudPinMatrixButtonWindow(capi, config,
                        "pinmatrix-btn-" + which.ToString().ToLowerInvariant(),
                        new[] { which }, false, actionFor, zonesVisible, layoutModeOn));
                }
            }
            else
            {
                bool horizontal = mode == "row" || mode == "parallel";
                buttons.Add(new HudPinMatrixButtonWindow(capi, config, "pinmatrix-buttons",
                    all, horizontal, actionFor, zonesVisible, layoutModeOn));
            }

            foreach (var b in buttons)
            {
                layout.OwnDialogs.Add(b);
                layout.SelfPlaced.Add(b.DialogName);
                // Hiding the zones is exactly "the player has stopped arranging", which is what
                // FlushPositionsNow is for.
                b.PositionsChanged = () => layout.FlushPositionsNow();
            }
        }

        /// <summary>Layout off falls back to the plain stacked window at its default spot.</summary>
        string ButtonMode() => config.LayoutEnabled ? config.LayoutButtonMode : "stacked";

        /// <summary>
        /// A row of the map's pin-set panel being clicked: hide the set if any of it is showing,
        /// otherwise show all of it. The row already shows which — lit or greyed — so there is
        /// nothing to confirm, and clicking again undoes it exactly, which is what makes an
        /// unconfirmed bulk action safe here.
        /// </summary>
        public void ToggleSet(string setId)
        {
            EnsureServices();
            var set = pinSets.ById(setId);
            if (set == null) return;

            if (!visibility.Available)
            {
                capi.ShowChatMessage("[Pin Matrix] Hiding pins is not available on this game version - see the client log.");
                return;
            }

            bool hiding = pinSets.WouldHide(set);
            int changed = pinSets.Apply(set, hiding);
            if (changed == 0) capi.ShowChatMessage("[Pin Matrix] No pins match \"" + set.Name + "\" right now.");
        }

        /// <summary>Re-labels and re-chromes the buttons after the zone overlay is toggled.</summary>
        void RefreshButtons()
        {
            EnsureButtons();
            foreach (var b in buttons)
            {
                if (b.IsEmpty)
                {
                    if (b.IsOpened()) b.TryClose();
                    continue;
                }
                if (b.IsOpened()) b.RefreshIfNeeded();
            }
        }

        /// <summary>
        /// Puts each button window back where it was last left.
        ///
        /// Buttons are windows, so their positions live in the same place every other window's does
        /// — vanilla's dialogPositions store — rather than in a bespoke offset of their own. A
        /// button with no stored position keeps the default right-edge stack it was created with.
        /// </summary>
        void PositionButtons()
        {
            if (buttons.Count == 0 || layout == null) return;

            string mode = ButtonMode();

            foreach (var b in buttons)
            {
                if (!b.IsOpened()) continue;

                // A window the player has actually dragged into a zone keeps that zone, whatever the
                // mode would otherwise dictate. Snapping is a deliberate act; a mode default is not.
                var snapped = layout.Find(b.DialogName);
                if (snapped != null && mode != "row")
                {
                    var zone = layout.ZoneFor(snapped);
                    b.SetStretchWidth(0);
                    b.SetPosition(zone.X, zone.Y);
                    continue;
                }

                if (mode == "row")
                {
                    // The tab row spans the whole screen and divides itself between the buttons,
                    // rather than being a fixed-width strip parked on a row. That is the point of a
                    // tab row: it reads as a bar, not as a window that happens to sit low down.
                    //
                    // WHICH row is no longer a number typed on the Layout screen — it is the row the
                    // bar has been dragged onto, read back from its own snap assignment
                    // (LayoutManager.ButtonRowIndex). Dropping it anywhere on a row puts the bar
                    // across that row, which is why the drop is not treated as a corner to sit in.
                    var strip = layout.ButtonRowRect();
                    // SetStretchWidth is the inner content width; the window pads itself by 8, and
                    // the two have to agree or the bar lands one padding short of the row it was
                    // asked to fill.
                    b.SetStretchWidth(strip.W - 8);
                    b.SetPosition(strip.X, strip.Y);
                }
                else if (mode == "stacked" || mode == "parallel")
                {
                    // Snapped to a cell like any other window, and draggable while the grid is up,
                    // so a position the player dragged it to wins over the configured cell. It has
                    // to be *their* position, not merely a stored one — layout mode seeds a stored
                    // position to make the title bar draggable at all, and treating that as a
                    // placement would stop the Cell setting working the moment the grid was shown.
                    b.SetStretchWidth(0);
                    if (b.TryGetPlayerPosition(out int sx, out int sy)) b.SetPosition(sx, sy);
                    else
                    {
                        var cell = layout.OwnButtonCell();
                        // Both windows are configured to the same cell, so the sets window starts
                        // just clear of the fixed one instead of underneath it. Only until it is
                        // dragged — a player position wins above, and the grid is right there.
                        b.SetPosition(cell.X, cell.Y);
                    }
                }
                else
                {
                    // Separate windows are the player's to put anywhere; only restore what was saved.
                    b.SetStretchWidth(0);
                    if (b.TryGetPlayerPosition(out int sx, out int sy)) b.SetPosition(sx, sy);
                }
            }

            if (mode == "float") SpreadFloatingButtons();
        }

        /// <summary>
        /// Keeps un-placed floating button windows off each other and off everything else on screen.
        ///
        /// Only windows with no saved position of their own are moved: once the player has put one
        /// somewhere, that is where it stays even if something later overlaps it. Anything else
        /// would mean a window creeping away from where it was deliberately dropped.
        /// </summary>
        void SpreadFloatingButtons()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            var taken = new List<double[]>();

            // Obstacles are everything on screen EXCEPT our own button windows.
            //
            // Including them was a genuine oscillation: a window is always in OpenedGuis, so it
            // overlapped itself, could never find its current slot free, moved on, and did it again
            // every tick — the Layout Options button marched around the screen forever. Our windows
            // are added to the list one at a time below, after each is settled.
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened() || gui == layoutOverlay) continue;
                if (gui is HudPinMatrixButtonWindow) continue;

                foreach (var pair in gui.Composers)
                {
                    var bounds = pair.Value?.Bounds;
                    if (pair.Value == null || !pair.Value.Enabled || bounds == null) continue;
                    if (bounds.OuterWidth * bounds.OuterHeight > 0.5 * capi.Render.FrameWidth * capi.Render.FrameHeight) continue;
                    taken.Add(new[] { bounds.absX / scale, bounds.absY / scale,
                                      bounds.OuterWidth / scale, bounds.OuterHeight / scale });
                }
            }

            foreach (var b in buttons)
            {
                if (!b.IsOpened()) continue;
                var cur = b.OuterRect;

                // Player-placed windows are fixed points: they never move, they only occupy space.
                // Again their own placement, not the one layout mode seeds to unlock dragging —
                // otherwise showing the grid once would permanently stop the buttons dodging.
                if (b.PlayerPlaced)
                {
                    taken.Add(new[] { cur.X, cur.Y, cur.W, cur.H });
                    continue;
                }

                // Hysteresis, and it is load-bearing: while a window is not actually overlapping
                // anything it must not move at all. Re-picking the best slot every tick is what
                // makes a button dance whenever some other HUD resizes itself.
                if (!Overlaps(taken, cur.X, cur.Y, cur.W, cur.H))
                {
                    taken.Add(new[] { cur.X, cur.Y, cur.W, cur.H });
                    continue;
                }

                bool placed = false;
                for (int slot = 0; slot < 40 && !placed; slot++)
                {
                    double x = screenW - 100 - cur.W;
                    double y = 120 + slot * (cur.H + 6);
                    if (y + cur.H > screenH) break;
                    if (Overlaps(taken, x, y, cur.W, cur.H)) continue;

                    b.SetPosition(x, y);
                    taken.Add(new[] { x, y, cur.W, cur.H });
                    placed = true;
                }

                // Nothing free: stay put. Moving to a slot we already know is occupied would
                // guarantee both an overlap and another move next tick.
                if (!placed) taken.Add(new[] { cur.X, cur.Y, cur.W, cur.H });
            }
        }

        static bool Overlaps(List<double[]> rects, double x, double y, double w, double h)
        {
            foreach (var r in rects)
            {
                bool apart = x + w <= r[0] || r[0] + r[2] <= x || y + h <= r[1] || r[1] + r[3] <= y;
                if (!apart) return true;
            }
            return false;
        }

        /// <summary>
        /// Opens the editor straight onto the Window layout screen, leaving the map open — the
        /// windows being arranged are the map-screen ones, so closing the map to configure their
        /// layout would hide the very thing you are working on.
        /// </summary>
        void OpenLayoutOptions()
        {
            EnsureDialog();
            dialog.OpenLayoutScreen();
        }

        void OpenFromMapButton()
        {
            EnsureDialog();
            if (svc.Layer == null) return;

            // close the world map first, then open the editor (the watcher hides this button)
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            if (mapDlg != null && mapDlg.IsOpened() && mapDlg.DialogType == EnumDialogType.Dialog)
            {
                mapDlg.TryClose();
            }

            if (!dialog.IsOpened()) dialog.TryOpen();
        }

        /// <summary>
        /// Built on the first watcher tick rather than in <c>StartClientSide</c>: the hidden-pin file
        /// is named after the savegame, which is only known once the world is actually joined.
        /// </summary>
        void EnsureServices()
        {
            if (svc == null) svc = new WaypointService(capi);
            if (visibility == null) visibility = new WaypointVisibility(capi, svc);
            if (pinSets == null)
            {
                pinSets = new PinSetService(config, svc, visibility, () => capi.StoreModConfig(config, "pinmatrix.json"));
            }
        }

        /// <summary>Saved filters and their map buttons; read by the editor's Pin sets screen.</summary>
        public PinSetService Sets { get { EnsureServices(); return pinSets; } }

        /// <summary>
        /// Re-reads the sets after the editor changes them, so the map panel is right the moment the
        /// player gets back to the map. Called by the editor rather than polled — the alternative is
        /// diffing the config four times a second forever.
        /// </summary>
        public void RefreshSetPanel()
        {
            pinSets?.Recount();
            setPanel?.MarkDirty();
        }

        TraderMarkers EnsureTraders()
        {
            EnsureServices();
            if (traders == null) traders = new TraderMarkers(capi, config, svc);
            return traders;
        }

        TranslocatorPaths EnsureTlPaths()
        {
            EnsureServices();
            if (tlPaths == null)
            {
                tlPaths = new TranslocatorPaths(capi, config, svc);
                TranslocatorPathLayer.Service = tlPaths;
            }
            return tlPaths;
        }

        void EnsureDialog()
        {
            if (dialog != null) return;
            EnsureServices();
            batch = new BatchEngine(capi, config);
            bin = new RecycleBin(capi, config);
            dialog = new GuiDialogPinMatrix(capi, config, svc, batch, bin, visibility);
            dialog.Layout = layout;
            dialog.ModSystem = this;
            dialog.Traders = EnsureTraders();
            dialog.TlPaths = EnsureTlPaths();

            // Our own windows are not obstacles to ourselves. Nothing is ever resized to fit a
            // zone, this window included — zones move things and leave their size alone.
            layout.OwnDialogs.Add(dialog);
        }

        void RegisterCommands()
        {
            capi.ChatCommands.GetOrCreate("pinmatrix")
                .WithDescription("Pin Matrix client utilities")

                .BeginSubCommand("dialogs")
                    .WithDescription("List every open window with the facts the layout grid decides on")
                    .HandleWith(_ =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        // Printed line by line: a single multi-line chat message is truncated.
                        foreach (var line in layout.Dump().Split('\n')) capi.ShowChatMessage(line.TrimEnd());
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()

                .BeginSubCommand("resetlayout")
                    .WithDescription("Forget every remembered window zone and put the windows back")
                    .HandleWith(_ =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        int n = layout.ResetAll();
                        return TextCommandResult.Success($"[Pin Matrix] Cleared {n} remembered window zone(s).");
                    })
                .EndSubCommand()

                .BeginSubCommand("unsnap")
                    .WithDescription("Release one window from its zone: .pinmatrix unsnap [name] (no name lists them)")
                    .WithArgs(capi.ChatCommands.Parsers.OptionalAll("name"))
                    .HandleWith(args =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        string query = args.ArgCount > 0 ? args[0] as string : null;
                        // Line by line: a single multi-line chat message is truncated.
                        foreach (var line in layout.UnsnapMatching(query).Split('\n')) capi.ShowChatMessage(line.TrimEnd());
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()

                .BeginSubCommand("rescanhuds")
                    .WithDescription("Re-measure the HUDs that disable grid cells")
                    .HandleWith(_ =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        layout.RecomputeBlocked();
                        return TextCommandResult.Success(
                            $"[Pin Matrix] {layout.StableHudCount} HUD rect(s) measured, {layout.BlockedCount} cell(s) disabled.");
                    })
                .EndSubCommand()

                .BeginSubCommand("tlpaths")
                    .WithDescription("List translocator paths: marker position vs where its label points")
                    .HandleWith(_ =>
                    {
                        if (tlPaths == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        foreach (var line in tlPaths.Explain().Split('\n')) capi.ShowChatMessage(line.TrimEnd());
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()

                .BeginSubCommand("whyblocked")
                    .WithDescription("Explain why a grid cell is disabled: .pinmatrix whyblocked <col> <row>")
                    .WithArgs(capi.ChatCommands.Parsers.Int("col"), capi.ChatCommands.Parsers.Int("row"))
                    .HandleWith(args =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        foreach (var line in layout.ExplainCell((int)args[0], (int)args[1]).Split('\n'))
                            capi.ShowChatMessage(line.TrimEnd());
                        return TextCommandResult.Success();
                    })
                .EndSubCommand()

                .BeginSubCommand("zones")
                    .WithDescription("Show or hide the layout zone overlay")
                    .HandleWith(_ =>
                    {
                        if (layout == null) return TextCommandResult.Error("Pin Matrix is not active.");
                        if (!config.LayoutEnabled) return TextCommandResult.Error("Window layout is switched off — tick \"Snap map-screen windows to a grid\" on the editor's Map options screen first.");
                        if (!FullMapOpen()) return TextCommandResult.Error("Open the world map first — the zones only apply to the map screen.");
                        SetLayoutPinned(!layoutPinned);
                        return TextCommandResult.Success($"[Pin Matrix] Zone overlay {(layoutPinned ? "shown" : "hidden")}.");
                    })
                .EndSubCommand();
        }

        bool OnHotkey(KeyCombination comb)
        {
            if (capi.World?.Player == null) return false;

            EnsureDialog();

            if (svc.Layer == null)
            {
                capi.ShowChatMessage("[Pin Matrix] Waypoint map layer not ready yet.");
                return true;
            }

            if (dialog.IsOpened()) dialog.TryClose();
            else dialog.TryOpen();
            return true;
        }

        public override void Dispose()
        {
            if (mapWatchListenerId != 0 && capi != null)
            {
                capi.Event.UnregisterGameTickListener(mapWatchListenerId);
                mapWatchListenerId = 0;
            }
            if (visibilityListenerId != 0 && capi != null)
            {
                capi.Event.UnregisterGameTickListener(visibilityListenerId);
                visibilityListenerId = 0;
            }
            if (visibilityRenderer != null && capi != null)
            {
                capi.Event.UnregisterRenderer(visibilityRenderer, EnumRenderStage.Ortho);
                visibilityRenderer = null;
            }
            layout?.FlushPositionsNow();
            setPanel?.Dispose();
            setPanel = null;
            foreach (var b in buttons) b.Dispose();
            buttons.Clear();
            layoutOverlay?.Dispose();
            layoutOverlay = null;
            layout = null;
            // Rebuilt on the next world: the hidden-pin list is per savegame
            visibility = null;
            traders = null;
            tlPaths = null;
            TranslocatorPathLayer.Service = null;
            svc = null;
            chatShareLinks?.Dispose();
            chatShareLinks = null;
            dialog?.Dispose();
            dialog = null;
            base.Dispose();
        }
    }
}
