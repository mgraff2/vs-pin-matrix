using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace PinMatrix
{
    /// <summary>
    /// The Map windows layout screen: configure the snap grid, and recover from it.
    /// </summary>
    public partial class GuiDialogPinMatrix
    {
        /// <summary>Opens this dialog straight onto the Map windows layout screen.</summary>
        public void OpenLayoutScreen()
        {
            // Order matters and is not obvious: OnGuiOpened resets screen to Matrix on every open,
            // so the screen has to be selected AFTER TryOpen or the dialog lands on the table.
            if (!IsOpened()) TryOpen();
            screen = PmScreen.Layout;
            Recompose();
            capi.Event.RegisterCallback(_ => { if (IsOpened()) capi.Gui.RequestFocus(this); }, 0);
        }

        /// <summary>
        /// Compact by design. An earlier version explained every control in a paragraph beside it;
        /// the result overlapped itself and buried the two or three settings anyone actually
        /// changes. The explanations now live in tooltips, where they are available on demand and
        /// cost no space when they are not wanted.
        /// </summary>
        void ComposeLayout(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var tip = CairoFont.WhiteDetailText();
            double y = 42;

            // ---- master switch, alone on the top row so it plainly governs everything below
            c.AddSwitch(OnLayoutEnabledToggled, EB(4, y, 28, 28), "layoutenabled", 25, 3);
            // Same wording as the Map options switch: it is the same boolean, and one setting
            // wearing a different name on each screen is how a player concludes there are two.
            c.AddStaticText("Snap map-screen windows to a grid", CairoFont.WhiteSmallishText(), EB(40, y + 3, 340, 26));
            c.AddHoverText(
                "Master switch for window snapping. Off puts everything back to normal: the Pin Matrix "
                + "buttons return to their floating position and nothing is repositioned. Windows you "
                + "already snapped stay where they are — their positions are saved with the game's own, "
                + "so they keep them even without this mod.",
                tip, 340, EB(4, y, 376, 28).FlatCopy(), "tipenabled");
            y += 40;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ---- grid
            c.AddStaticText("Grid", font, EB(4, y + 4, 44, 25));
            c.AddNumberInput(EB(52, y, 54, 28), OnLayoutColsChanged, font, "layoutcols");
            c.AddStaticText("x", font, EB(112, y + 4, 16, 25));
            c.AddNumberInput(EB(130, y, 54, 28), OnLayoutRowsChanged, font, "layoutrows");
            c.AddHoverText($"Columns x rows, 1 to {ZoneGrid.MaxCols} each. The screen is split evenly. "
                + "A fine grid places windows precisely; a coarse one gives you big drop targets.",
                tip, 300, EB(4, y, 184, 28).FlatCopy(), "tipgrid");

            c.AddSwitch(OnAvoidHudsToggled, EB(210, y, 28, 28), "layoutavoidhuds", 25, 3);
            // Spelled out to match what every doc and the ModDB page call it. "Avoid HUDs" was
            // shorter and meant nothing to a player hunting for the control the text described.
            c.AddStaticText("Do not cover HUDs", font, EB(246, y + 4, 180, 25));
            c.AddNumberInput(EB(430, y, 50, 28), OnHudThresholdChanged, font, "layoutthreshold");
            c.AddStaticText("%", font, EB(484, y + 4, 18, 25));
            c.AddHoverText(
                "Disables grid cells a HUD is sitting in, so a snapped window never covers your "
                + "coordinates, hotbar or chat. The percentage is how much of a cell a HUD must cover "
                + "before it counts — low values cost you a whole row, since the hotbar is wide but short. "
                + "HUDs are only measured, never moved.",
                tip, 340, EB(210, y, 292, 28).FlatCopy(), "tiphuds");
            y += 40;

            // ---- button placement
            c.AddStaticText("Buttons", font, EB(4, y + 4, 74, 25));
            c.AddSmallButton(ButtonModeLabel(), OnCycleButtonMode, EB(82, y, 180, 28));
            c.AddStaticText("Tab row", font, EB(274, y + 4, 74, 25));
            c.AddNumberInput(EB(352, y, 50, 28), OnButtonRowChanged, font, "layoutbuttonrow");
            c.AddStaticText("Cell", font, EB(414, y + 4, 46, 25));
            c.AddNumberInput(EB(462, y, 50, 28), OnButtonColChanged, font, "layoutbuttoncol");
            c.AddNumberInput(EB(518, y, 50, 28), OnButtonCellRowChanged, font, "layoutbuttoncellrow");
            c.AddHoverText(
                "How the Pin Matrix map-screen buttons are packaged.  "
                + "Stacked: one window, buttons in a column, snapped to the Cell on the right.  "
                + "Parallel: the same window with its buttons side by side.  "
                + "Tab row: one bar stretched right across the row numbered beside it, buttons spread evenly.  "
                + "Floating: a window per button, each placed and snapped on its own.  "
                + "Show the zones and every window grows a drag handle; hide the zones and they are "
                + "plain buttons again, right where you left them.",
                tip, 360, EB(4, y, 258, 28).FlatCopy(), "tipbuttons");
            c.AddHoverText(
                "Which row is a tab strip. This is a property of the row, not of any one window: "
                + "drop ANY window on the nominated row and it spreads into an even slot along the "
                + "full width instead of sitting in the single cell you dropped it on, re-spacing "
                + "itself as things join and leave. The column you drop on sets the order along the "
                + "strip. Dragging cannot express this on its own — it says where one window goes, "
                + "not that a whole row has changed meaning. -1 = no strip.",
                tip, 340, EB(274, y, 128, 28).FlatCopy(), "tiptabrow");
            c.AddHoverText(
                "Column and row of the cell the button window anchors to in Stacked and Parallel "
                + "modes. Dragging the window somewhere overrides this, so it is the starting point "
                + "rather than the last word.",
                tip, 320, EB(414, y, 154, 28).FlatCopy(), "tipbuttoncell");
            y += 42;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ---- status and actions
            c.AddDynamicText(LayoutStatusText(), font, EB(4, y, DW - 8, 46), "layoutstatus");
            y += 50;

            c.AddSmallButton(ModSystem != null && ModSystem.LayoutPinned ? "Hide zones" : "Show zones",
                OnToggleZones, EB(4, y, 130, 30));
            c.AddSmallButton("Rescan HUDs", OnRescanHuds, EB(142, y, 140, 30));
            c.AddSmallButton("List open windows", OnDumpDialogs, EB(290, y, 190, 30));
            c.AddSmallButton($"Reset layout ({LayoutAssignmentCount()})", OnResetLayout, EB(488, y, 190, 30));
            c.AddHoverText(
                "Shows the zone grid over the map screen so you can drag windows into it. Same as the "
                + "Layout Zones button on the map. This only displays the grid — it does not turn the "
                + "system on or off; that is the switch at the top.",
                tip, 320, EB(4, y, 130, 30).FlatCopy(), "tipshow");
            c.AddHoverText("Re-measures which HUDs are on screen and which cells they block.",
                tip, 300, EB(142, y, 140, 30).FlatCopy(), "tiprescan");
            c.AddHoverText("Prints every open window and its position to chat. Use this when a window will not snap.",
                tip, 300, EB(290, y, 190, 30).FlatCopy(), "tipdump");
            c.AddHoverText("Forgets every remembered zone and puts those windows back. "
                + "Also available in chat as .pinmatrix resetlayout, for when a window lands somewhere you cannot click.",
                tip, 320, EB(488, y, 190, 30).FlatCopy(), "tipreset");
            y += 40;

            // Back to the map, not to the matrix: this screen is only ever reached from the map's
            // own "Layout Options" button, and the grid being configured is on the map behind it.
            c.AddSmallButton("Back to map", () => { GoBack(); return true; }, EB(4, y, 142, 30));
        }

        int LayoutAssignmentCount() => config.LayoutAssignments?.Count ?? 0;

        string LayoutStatusText()
        {
            if (Layout == null) return "Layout system unavailable.";
            var g = Layout.Grid;
            string dock = config.LayoutButtonMode == "row" ? $"tab row {config.LayoutButtonRow}"
                        : config.LayoutButtonMode == "cell" ? $"cell {config.LayoutButtonCol},{config.LayoutButtonCellRow}"
                        : "floating";
            // Kept terse deliberately: this sits in a fixed box, and a status line that grows with
            // its own numbers is exactly how a dialog ends up overrunning the buttons below it.
            return $"{g.Cols} x {g.Rows} grid, cell {g.CellW:0} x {g.CellH:0}, buttons {dock}, "
                 + $"{Layout.BlockedCount} cells blocked by {Layout.StableHudCount} HUDs, "
                 + $"{LayoutAssignmentCount()} windows placed";
        }

        void RestoreLayoutState()
        {
            var c = SingleComposer;
            if (c == null) return;
            c.GetSwitch("layoutenabled").SetValue(config.LayoutEnabled);
            c.GetSwitch("layoutavoidhuds").SetValue(config.LayoutAvoidHuds);
            c.GetNumberInput("layoutcols").SetValue(config.LayoutCols.ToString(CultureInfo.InvariantCulture));
            c.GetNumberInput("layoutrows").SetValue(config.LayoutRows.ToString(CultureInfo.InvariantCulture));
            c.GetNumberInput("layoutthreshold").SetValue(config.LayoutHudCoverageThreshold.ToString(CultureInfo.InvariantCulture));
            c.GetNumberInput("layoutbuttonrow").SetValue(config.LayoutButtonRow.ToString(CultureInfo.InvariantCulture));
            c.GetNumberInput("layoutbuttoncol").SetValue(config.LayoutButtonCol.ToString(CultureInfo.InvariantCulture));
            c.GetNumberInput("layoutbuttoncellrow").SetValue(config.LayoutButtonCellRow.ToString(CultureInfo.InvariantCulture));
        }

        void SaveLayoutConfig(bool regrid)
        {
            config.Clamp();
            capi.StoreModConfig(config, "pinmatrix.json");
            if (regrid) Layout?.Invalidate();
            SingleComposer?.GetDynamicText("layoutstatus")?.SetNewText(LayoutStatusText(), false, true, false);
        }

        // ------------------------------------------------------------------ handlers

        void OnLayoutEnabledToggled(bool on)
        {
            config.LayoutEnabled = on;
            SaveLayoutConfig(true);

            // Off means everything goes back to normal now: buttons float again and nothing is
            // repositioned. Windows already snapped are deliberately left where they are — their
            // positions are saved with the game's own, so turning the grid off is not meant to
            // rearrange your screen a second time.
            ModSystem?.RefreshButtonPlacement();
            if (!on) ModSystem?.SetLayoutPinned(false);
            Recompose();
        }

        void OnAvoidHudsToggled(bool on)
        {
            config.LayoutAvoidHuds = on;
            config.Clamp();
            capi.StoreModConfig(config, "pinmatrix.json");
            // Grid geometry is unchanged, so assignments stay valid and nothing moves; only which
            // cells are selectable changes. That is exactly why the grid is not inset around HUDs.
            Layout?.RecomputeBlocked();
            SingleComposer?.GetDynamicText("layoutstatus")?.SetNewText(LayoutStatusText(), false, true, false);
        }

        void OnLayoutColsChanged(string t)
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
            {
                config.LayoutCols = v;
                SaveLayoutConfig(true);
            }
        }

        void OnLayoutRowsChanged(string t)
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
            {
                config.LayoutRows = v;
                SaveLayoutConfig(true);
            }
        }

        string ButtonModeLabel()
        {
            // No "Buttons:" prefix - there is already a Buttons label immediately to the left.
            switch (config.LayoutButtonMode)
            {
                case "row": return "Tab row";
                case "parallel": return "Parallel";
                case "float": return "Floating";
                default: return "Stacked";
            }
        }

        bool OnCycleButtonMode()
        {
            switch (config.LayoutButtonMode)
            {
                case "stacked": config.LayoutButtonMode = "parallel"; break;
                case "parallel": config.LayoutButtonMode = "row"; break;
                case "row": config.LayoutButtonMode = "float"; break;
                default: config.LayoutButtonMode = "stacked"; break;
            }

            if (config.LayoutButtonMode == "row" && config.LayoutButtonRow < 0)
            {
                config.LayoutButtonRow = Math.Max(0, config.LayoutRows - 1);
            }
            SaveLayoutConfig(true);
            ModSystem?.RefreshButtonPlacement();
            Recompose();
            return true;
        }

        void OnButtonColChanged(string t)
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0)
            {
                config.LayoutButtonCol = v;
                SaveLayoutConfig(true);
            }
        }

        void OnButtonCellRowChanged(string t)
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0)
            {
                config.LayoutButtonCellRow = v;
                SaveLayoutConfig(true);
            }
        }

        void OnButtonRowChanged(string t)
        {
            // Accepts -1, so this cannot use the "> 0" guard the size inputs use.
            if (int.TryParse(t, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                             CultureInfo.InvariantCulture, out int v))
            {
                config.LayoutButtonRow = v;
                SaveLayoutConfig(true);
            }
        }

        void OnHudThresholdChanged(string t)
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
            {
                config.LayoutHudCoverageThreshold = v;
                config.Clamp();
                capi.StoreModConfig(config, "pinmatrix.json");
                Layout?.RecomputeBlocked();
                SingleComposer?.GetDynamicText("layoutstatus")?.SetNewText(LayoutStatusText(), false, true, false);
            }
        }

        bool OnToggleZones()
        {
            ModSystem?.SetLayoutPinned(!(ModSystem?.LayoutPinned ?? false));
            Recompose();
            return true;
        }

        bool OnRescanHuds()
        {
            if (Layout == null) return true;
            Layout.RecomputeBlocked();
            // The status line under the toggles is the visible confirmation; `notice` belongs to the
            // matrix screen and would never be seen from here.
            SingleComposer?.GetDynamicText("layoutstatus")?.SetNewText(LayoutStatusText(), false, true, false);
            return true;
        }

        bool OnDumpDialogs()
        {
            if (Layout == null) return true;
            foreach (var line in Layout.Dump().Split('\n')) capi.ShowChatMessage(line.TrimEnd());
            return true;
        }

        bool OnResetLayout()
        {
            int n = Layout?.ResetAll() ?? 0;
            capi.ShowChatMessage($"[Pin Matrix] Cleared {n} remembered window zone(s).");
            Recompose();   // refreshes the button's own count and the status line
            return true;
        }

    }
}
