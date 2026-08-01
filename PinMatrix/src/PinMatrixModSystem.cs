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
        long mapWatchListenerId;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

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
                PositionMapButton();
                if (!mapButton.IsOpened()) mapButton.TryOpen();
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

            // First free slot wins: down the right edge, then a second column further left
            foreach (int rightUnscaled in new[] { HudPinMatrixMapButton.DefaultRightMargin, 320, 540 })
            {
                double x = screenW - GuiElement.scaled(rightUnscaled) - w;
                if (x < 0) continue;

                for (int yUnscaled = HudPinMatrixMapButton.DefaultYOffset; yUnscaled <= 560; yUnscaled += 45)
                {
                    double y = GuiElement.scaled(yUnscaled);
                    if (y + h > screenH) break;

                    bool clear = true;
                    foreach (var o in obstacles)
                    {
                        bool apart = x + w <= o[0] || o[0] + o[2] <= x || y + h <= o[1] || o[1] + o[3] <= y;
                        if (!apart) { clear = false; break; }
                    }
                    if (clear)
                    {
                        mapButton.SetOffset(rightUnscaled, yUnscaled);
                        return;
                    }
                }
            }
            mapButton.SetOffset(HudPinMatrixMapButton.DefaultRightMargin, HudPinMatrixMapButton.DefaultYOffset);
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
            dialog?.Dispose();
            dialog = null;
            base.Dispose();
        }
    }
}
