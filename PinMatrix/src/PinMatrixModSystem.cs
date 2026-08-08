using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
        GuiDialogPinMatrix dialog;
        HudPinMatrixMapButton mapButton;
        ChatShareLinks chatShareLinks;
        long mapWatchListenerId;
        int settleTicksLeft;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            ColorSwatchComponent.EnsureTagRegistered();

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

            mapWatchListenerId = capi.Event.RegisterGameTickListener(OnMapWatchTick, 250);
            chatShareLinks = new ChatShareLinks(capi);
        }

        /// <summary>Shows/hides the "Pin Matrix Editor" button in sync with the full world map dialog.</summary>
        void OnMapWatchTick(float dt)
        {
            if (capi.World?.Player == null) return;

            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            var mapDlg = mapManager?.worldMapDlg;
            bool fullMapOpen = mapDlg != null && mapDlg.IsOpened() && mapDlg.DialogType == EnumDialogType.Dialog;

            if (fullMapOpen)
            {
                if (mapButton == null) mapButton = new HudPinMatrixMapButton(capi, OpenFromMapButton);
                if (!mapButton.IsOpened())
                {
                    mapButton.ResetOffset();    // each map-open starts from the preferred slot
                    PositionMapButton();
                    mapButton.TryOpen();
                    settleTicksLeft = 4;        // ~1s at the 250ms watcher cadence
                }
                else if (settleTicksLeft > 0)
                {
                    // Settle window: dodge panels that appear right after the map opens (layer
                    // filters, other mods' map tabs). After it, the button is FROZEN for this
                    // map session — obstacle avoidance against HUDs that move or pulse (mouse-
                    // following hover boxes, live readouts like Boat Autopilot's) degenerates
                    // into a visible dance, and a frozen button can't be dragged into one.
                    // Worst case after freezing is a static overlap, which is only cosmetic.
                    settleTicksLeft--;
                    PositionMapButton();
                }
                else
                {
                    mapButton.RecomposeIfScreenChanged();   // absolute anchoring: track resizes/scale changes
                }
            }
            else if (mapButton != null && mapButton.IsOpened())
            {
                mapButton.TryClose();
            }
        }

        /// <summary>
        /// Slides the map-screen button down the right edge until it doesn't overlap any other
        /// open dialog (e.g. Prospect Together's map panels). Rechecked every watcher tick, so
        /// panels that appear later push the button out of the way too.
        /// </summary>
        void PositionMapButton()
        {
            // Pinned via config: no scanning, no avoidance — the user owns the placement.
            if (config.MapButtonRightMargin >= 0 || config.MapButtonYOffset >= 0)
            {
                mapButton.SetOffset(
                    config.MapButtonRightMargin >= 0 ? config.MapButtonRightMargin : HudPinMatrixMapButton.DefaultRightMargin,
                    config.MapButtonYOffset >= 0 ? config.MapButtonYOffset : HudPinMatrixMapButton.DefaultYOffset);
                return;
            }

            double screenW = capi.Render.FrameWidth;
            double screenH = capi.Render.FrameHeight;

            // estimated outer rect of the button dialog (content 170x30 + padding/shading), with margin
            double w = GuiElement.scaled(196);
            double h = GuiElement.scaled(48);

            // Collect obstacle rects from ALL open dialogs, per composer. This includes the world
            // map dialog itself: layer filter panels (vanilla and mods like Prospect Together) are
            // attached to it as extra composers. Only fullscreen-sized composers — the map surface
            // itself — are ignored.
            var obstacles = new List<double[]>();
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || gui == mapButton || gui == dialog) continue;
                if (!gui.IsOpened()) continue;
                foreach (var compo in gui.Composers.Values)
                {
                    var b = compo?.Bounds;
                    if (compo == null || !compo.Enabled || b == null) continue;
                    if (b.OuterWidth * b.OuterHeight > 0.5 * screenW * screenH) continue;
                    obstacles.Add(new[] { b.absX, b.absY, b.OuterWidth, b.OuterHeight });
                }
            }

            bool Clear(double x, double y)
            {
                foreach (var o in obstacles)
                {
                    bool apart = x + w <= o[0] || o[0] + o[2] <= x || y + h <= o[1] || o[1] + o[3] <= y;
                    if (!apart) return false;
                }
                return true;
            }

            // Hysteresis: while the current slot stays clear, never move. Re-picking the topmost
            // free slot every tick caused a visible bounce against HUDs whose bounds change
            // periodically (e.g. the vanilla coordinate box recomposes to the text width as the
            // player moves) — freeing and re-blocking the top slot several times a second. A
            // button that only ever moves when actually overlapped cannot oscillate, whatever
            // other mods' HUDs do. ResetOffset() on map-open restores the preferred slot.
            {
                double curX = screenW - GuiElement.scaled(mapButton.CurrentRightMargin) - w;
                double curY = GuiElement.scaled(mapButton.CurrentYOffset);
                if (curX >= 0 && curY + h <= screenH && Clear(curX, curY)) return;
            }

            // First free slot wins: down the right edge, then a second column further left
            foreach (int rightUnscaled in new[] { HudPinMatrixMapButton.DefaultRightMargin, 320, 540 })
            {
                double x = screenW - GuiElement.scaled(rightUnscaled) - w;
                if (x < 0) continue;

                for (int yUnscaled = HudPinMatrixMapButton.DefaultYOffset; yUnscaled <= 560; yUnscaled += 45)
                {
                    double y = GuiElement.scaled(yUnscaled);
                    if (y + h > screenH) break;

                    if (Clear(x, y))
                    {
                        mapButton.SetOffset(rightUnscaled, yUnscaled);
                        return;
                    }
                }
            }
            // No slot is clear: stay where we are — jumping to the default slot would guarantee
            // an overlap AND another jump next tick.
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

        void EnsureDialog()
        {
            if (dialog != null) return;
            svc = new WaypointService(capi);
            batch = new BatchEngine(capi, config);
            bin = new RecycleBin(capi, config);
            dialog = new GuiDialogPinMatrix(capi, config, svc, batch, bin);
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
            mapButton?.Dispose();
            mapButton = null;
            chatShareLinks?.Dispose();
            chatShareLinks = null;
            dialog?.Dispose();
            dialog = null;
            base.Dispose();
        }
    }
}
