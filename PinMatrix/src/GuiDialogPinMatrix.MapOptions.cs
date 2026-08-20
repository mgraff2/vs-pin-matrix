using System;
using System.Globalization;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace PinMatrix
{
    /// <summary>
    /// The Map options screen: the things Pin Matrix can add to the map — auto-markers, and the
    /// map-screen buttons themselves.
    ///
    /// Three sections, each one switch and its settings on a single row: traders, translocator
    /// paths, window layout. Everything the first two produce is an ordinary waypoint, so it
    /// composes with the rest of the matrix rather than living beside it.
    ///
    /// Compact by the same rule the Window layout screen learned the hard way: explanations live in
    /// hover tooltips, not in paragraphs beside the controls. A paragraph per setting buries the two
    /// or three things anyone actually changes, and this screen had four of them.
    /// </summary>
    public partial class GuiDialogPinMatrix
    {
        const double TraderRowH = 27;

        /// <summary>Colour rows run in two columns; nine in one column was a third of the screen.</summary>
        const int TraderCols = 2;
        const double TraderColW = 448;
        static int TraderGridRows => (TraderMarkers.Roles.Length + TraderCols - 1) / TraderCols;

        void ComposeMapOptions(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var head = CairoFont.WhiteSmallishText();
            var tip = CairoFont.WhiteDetailText();
            double y = 42;

            // ================================================================ traders
            c.AddStaticText("Trader markers", head, EB(4, y, 300, 26));
            y += 28;

            c.AddSwitch(OnTraderMarkersToggled, EB(4, y, 26, 26), "tradersenabled", 23, 3);
            c.AddStaticText("Auto-mark traders", font, EB(36, y + 4, 170, 25));
            c.AddSwitch(OnTraderPinnedToggled, EB(216, y, 26, 26), "traderpinned", 23, 3);
            c.AddStaticText("Pinned", font, EB(248, y + 4, 64, 25));
            c.AddHoverText(
                "Walk past a trader and Pin Matrix drops a waypoint on it, coloured by specialisation. "
                + "What it produces is an ordinary waypoint, so it filters, recolours, hides, renames, "
                + "bins and exports like any hand-placed pin — switching this off later leaves nothing "
                + "behind that the matrix cannot manage. \"Pinned\" gives each one the screen-edge arrow.",
                tip, 340, EB(4, y, 308, 28).FlatCopy(), "tiptraders");

            c.AddStaticText("Mark within", font, EB(330, y + 4, 112, 25));
            c.AddNumberInput(EB(442, y, 56, 28), OnTraderMaxDistChanged, font, "tradermaxdist");
            c.AddStaticText("blocks", font, EB(504, y + 4, 60, 25));
            c.AddHoverText(
                "How close you have to get before a trader is marked. 0 means as soon as the client "
                + "loads them, which is usually well before you can see them.",
                tip, 300, EB(330, y, 234, 28).FlatCopy(), "tipmarkdist");

            c.AddStaticText("Same trader within", font, EB(584, y + 4, 178, 25));
            c.AddNumberInput(EB(762, y, 56, 28), OnTraderRadiusChanged, font, "traderradius");
            c.AddStaticText("blocks", font, EB(824, y + 4, 60, 25));
            c.AddHoverText(
                "The already-marked test, not a detection range: a trader-icon waypoint within this "
                + "distance means skip. Kept loose because traders wander around their cart.",
                tip, 300, EB(584, y, 300, 28).FlatCopy(), "tipdedupe");
            y += 34;

            c.AddStaticText("Colour per specialisation", font, EB(4, y + 4, 250, 25));
            c.AddHoverText("Edit a hex code to taste, or Reset a row to the Waypointer default — "
                + "so the same kind of trader reads as the same colour in either mod.",
                tip, 300, EB(4, y, 250, 26).FlatCopy(), "tipcolours");
            c.AddSmallButton("Reset all colours", OnResetAllTraderColors, EB(264, y, 170, 26), EnumButtonStyle.Small);
            c.AddSmallButton("Scan for traders now", OnScanTradersNow, EB(444, y, 200, 26), EnumButtonStyle.Small);
            y += 32;

            // One custom-draw element per column paints that column's chips — nine separate
            // elements would be nine textures for nine little squares, and one element spanning
            // both columns would put a wide transparent surface on top of the inputs and buttons
            // between them. Narrow strips overlap nothing.
            for (int col = 0; col < TraderCols; col++)
            {
                int c0 = col;
                c.AddDynamicCustomDraw(EB(6 + col * TraderColW, y + 3, 26, TraderGridRows * TraderRowH),
                    (ctx, surface, bounds) => DrawTraderSwatches(ctx, c0), SwatchKey(col));
            }

            for (int i = 0; i < TraderMarkers.Roles.Length; i++)
            {
                string role = TraderMarkers.Roles[i];
                // Column-major, so the names still read alphabetically down each column.
                double cx = (i / TraderGridRows) * TraderColW + 4;
                double ry = y + (i % TraderGridRows) * TraderRowH;
                c.AddStaticText(TraderMarkers.TitleFor(role), font, EB(cx + 36, ry + 4, 176, 24));
                c.AddTextInput(EB(cx + 218, ry, 96, 26), MakeTraderColorHandler(role), font, "tcol_" + role);
                c.AddSmallButton("Reset", MakeTraderColorReset(role), EB(cx + 322, ry, 74, 26), EnumButtonStyle.Small);
            }
            y += TraderGridRows * TraderRowH + 8;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ================================================================ translocator paths
            c.AddStaticText("Translocator paths", head, EB(4, y, 300, 26));
            y += 28;

            c.AddSwitch(OnTlPathsToggled, EB(4, y, 26, 26), "tlenabled", 23, 3);
            c.AddStaticText("Draw travelled translocator paths", font, EB(36, y + 4, 320, 25));
            c.AddHoverText(
                "Step through a translocator and both pads get a waypoint named after the coordinates "
                + "of the other end, with a line drawn between them on the map. Nothing is recorded "
                + "for a pad you merely walked past. The waypoint titles are the storage — no save "
                + "file, no import, and the paths follow your account to any computer you play from.",
                tip, 340, EB(4, y, 352, 28).FlatCopy(), "tiptl");

            c.AddStaticText("Highlight recent for", font, EB(376, y + 4, 196, 25));
            c.AddNumberInput(EB(572, y, 56, 28), OnTlRecentMinutesChanged, font, "tlrecentmins");
            c.AddStaticText("min", font, EB(634, y + 4, 44, 25));
            c.AddHoverText("A hop you just took is drawn in the Recent colour until this runs out, then "
                + "reverts. 0 never highlights.",
                tip, 300, EB(376, y, 302, 28).FlatCopy(), "tiptlrecent");
            y += 34;

            // Chips beside the hex boxes, the same as the trader rows above — without one, a hex
            // field does not read as a colour control at all.
            c.AddStaticText("Path colour", font, EB(36, y + 4, 110, 25));
            c.AddDynamicCustomDraw(EB(150, y + 1, 26, 26), (ctx, s, b) => DrawTlSwatch(ctx, false), TlSwatchKey(false));
            c.AddTextInput(EB(180, y, 96, 26), OnTlColorChanged, font, "tlcolor");
            c.AddStaticText("Recent", font, EB(292, y + 4, 64, 25));
            c.AddDynamicCustomDraw(EB(360, y + 1, 26, 26), (ctx, s, b) => DrawTlSwatch(ctx, true), TlSwatchKey(true));
            c.AddTextInput(EB(390, y, 96, 26), OnTlRecentColorChanged, font, "tlrecentcolor");
            c.AddHoverText("The colour of the pad markers and the line between them, and the colour a "
                + "hop is drawn in just after you take it. Six hex digits; the chip beside each box "
                + "updates as you type.",
                tip, 320, EB(36, y, 450, 26).FlatCopy(), "tiptlcolours");
            c.AddSmallButton("Reset colours", OnResetTlColors, EB(496, y, 140, 26), EnumButtonStyle.Small);
            c.AddSmallButton("Adopt TL waypoints...", OnAdoptTlWaypoints, EB(646, y, 220, 26), EnumButtonStyle.Small);
            c.AddHoverText("Converts translocator markers left behind by another tool into Pin Matrix "
                + "path markers. Always previewed first — only titles, icons and colours change.",
                tip, 300, EB(646, y, 220, 26).FlatCopy(), "tipadopt");
            y += 34;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ================================================================ window layout
            //
            // The rest of the layout system is configured from the map screen itself ("Layout
            // Options", visible while the zones are showing), because a grid can only be judged
            // while you are looking at it. But the switch that makes its button exist at all has to
            // be reachable without that button, which is why it lives here.
            c.AddStaticText("Map windows layout", head, EB(4, y, 300, 26));
            y += 28;

            c.AddSwitch(OnLayoutFeatureToggled, EB(4, y, 26, 26), "layoutfeature", 23, 3);
            c.AddStaticText("Snap map-screen windows to a grid (adds the \"Layout Zones\" button)",
                font, EB(36, y + 4, 630, 25));
            c.AddHoverText(
                "The master switch for the whole layout system, not just its button. On adds a "
                + "\"Layout Zones\" button beside the editor button, which shows a snap grid over the "
                + "map: drag any window by its title bar into a zone and it stays there. Grid size and "
                + "the rest are configured from \"Layout Options\", which appears on the map while the "
                + "zones are showing. Off stops all of it — nothing is repositioned, the Z shortcut "
                + "does nothing and the button is gone. Windows you have already placed stay exactly "
                + "where they are: their positions are saved with the game's own, so they keep them "
                + "even without this mod. Your remembered zones are kept too, and come back when you "
                + "switch this on again — \"Reset layout\" is what forgets them.",
                tip, 340, EB(4, y, 666, 28).FlatCopy(), "tiplayout");
            y += 34;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            c.AddDynamicText(MapOptionsStatusText(), font, EB(4, y, DW, 25), "mapoptstatus");
            y += 30;

            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(4, y, 80, 30));
        }

        static string SwatchKey(int col) => "traderswatches" + col;
        static string TlSwatchKey(bool recent) => recent ? "tlrecentswatch" : "tlswatch";

        /// <summary>One colour chip, at the given element-local y.</summary>
        static void PaintChip(Context ctx, int rgb, double localY)
        {
            double size = GuiElement.scaled(20);
            ctx.SetSourceRGBA(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, 1);
            ctx.Rectangle(0, localY, size, size);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(0, 0, 0, 0.65);
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.Stroke();
        }

        /// <summary>Paints one column's chips, in the same column-major order the rows use.</summary>
        void DrawTraderSwatches(Context ctx, int col)
        {
            for (int row = 0; row < TraderGridRows; row++)
            {
                int i = col * TraderGridRows + row;
                if (i >= TraderMarkers.Roles.Length) break;
                PaintChip(ctx, TraderMarkers.ParseHex(TraderColorHex(TraderMarkers.Roles[i])),
                    GuiElement.scaled(row * TraderRowH + 2));
            }
        }

        void DrawTlSwatch(Context ctx, bool recent)
        {
            string hex = recent ? config.TranslocatorRecentColor : config.TranslocatorMarkerColor;
            PaintChip(ctx, TraderMarkers.ParseHex(hex), GuiElement.scaled(2));
        }

        string TraderColorHex(string role)
        {
            if (config.TraderMarkerColors != null &&
                config.TraderMarkerColors.TryGetValue(role, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
            return TraderMarkers.DefaultRoleColors.TryGetValue(role, out var d) ? d : TraderMarkers.DefaultColor;
        }

        string MapOptionsStatusText()
        {
            int marked = Traders?.MarkedThisSession ?? 0;
            int hops = TlPaths?.RecordedThisSession ?? 0;
            int known = TlPaths?.CurrentPaths()?.Count ?? 0;

            string traderPart = config.TraderMarkersEnabled
                ? $"Traders: ON, {marked} marked this session."
                : "Traders: OFF.";
            string tlPart = config.TranslocatorPathsEnabled
                ? $"  Translocator paths: ON, {known} known, {hops} travelled this session."
                : "  Translocator paths: OFF.";
            string layoutPart = config.LayoutEnabled ? "  Map windows layout: ON." : "  Map windows layout: OFF.";
            return traderPart + tlPart + layoutPart;
        }

        void RestoreMapOptionsState()
        {
            var c = SingleComposer;
            if (c == null) return;
            c.GetSwitch("tradersenabled").SetValue(config.TraderMarkersEnabled);
            c.GetSwitch("traderpinned").SetValue(config.TraderMarkerPinned);
            c.GetNumberInput("traderradius").SetValue(
                config.TraderMarkerDedupeRadius.ToString("0.#", CultureInfo.InvariantCulture));
            c.GetNumberInput("tradermaxdist").SetValue(
                config.TraderMarkerMaxDistance.ToString("0.#", CultureInfo.InvariantCulture));

            foreach (var role in TraderMarkers.Roles)
            {
                c.GetTextInput("tcol_" + role)?.SetValue(TraderColorHex(role));
            }

            c.GetSwitch("layoutfeature").SetValue(config.LayoutEnabled);
            c.GetSwitch("tlenabled").SetValue(config.TranslocatorPathsEnabled);
            c.GetNumberInput("tlrecentmins").SetValue(
                config.TranslocatorRecentMinutes.ToString("0.#", CultureInfo.InvariantCulture));
            c.GetTextInput("tlcolor")?.SetValue(config.TranslocatorMarkerColor);
            c.GetTextInput("tlrecentcolor")?.SetValue(config.TranslocatorRecentColor);
        }

        void SaveMapOptionsConfig()
        {
            config.Clamp();
            capi.StoreModConfig(config, "pinmatrix.json");
            SingleComposer?.GetDynamicText("mapoptstatus")?.SetNewText(MapOptionsStatusText(), false, true, false);
            for (int col = 0; col < TraderCols; col++)
            {
                (SingleComposer?.GetElement(SwatchKey(col)) as GuiElementCustomDraw)?.Redraw();
            }
            (SingleComposer?.GetElement(TlSwatchKey(false)) as GuiElementCustomDraw)?.Redraw();
            (SingleComposer?.GetElement(TlSwatchKey(true)) as GuiElementCustomDraw)?.Redraw();
        }

        // ------------------------------------------------------------------ handlers

        void OnTraderMarkersToggled(bool on)
        {
            config.TraderMarkersEnabled = on;
            // A fresh switch-on should be able to re-mark traders whose markers were deleted since.
            if (on) Traders?.ClearPending();
            SaveMapOptionsConfig();
        }

        void OnTraderPinnedToggled(bool on)
        {
            config.TraderMarkerPinned = on;
            SaveMapOptionsConfig();
        }

        void OnTraderMaxDistChanged(string t)
        {
            // 0 is meaningful here (no limit), so this cannot use the "> 0" guard.
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0)
            {
                config.TraderMarkerMaxDistance = v;
                SaveMapOptionsConfig();
            }
        }

        void OnTraderRadiusChanged(string t)
        {
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
            {
                config.TraderMarkerDedupeRadius = v;
                SaveMapOptionsConfig();
            }
        }

        Action<string> MakeTraderColorHandler(string role) => text =>
        {
            string hex = (text ?? "").Trim();
            // Only commit a complete colour: this fires on every keystroke, so accepting partial
            // input would repaint the chip black while the player is halfway through typing.
            if (hex.Length == 0)
            {
                config.TraderMarkerColors.Remove(role);
                SaveMapOptionsConfig();
                return;
            }
            string body = hex.TrimStart('#');
            if (body.Length != 6) return;
            if (!int.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return;

            config.TraderMarkerColors[role] = "#" + body.ToLowerInvariant();
            SaveMapOptionsConfig();
        };

        ActionConsumable MakeTraderColorReset(string role) => () =>
        {
            config.TraderMarkerColors.Remove(role);
            SaveMapOptionsConfig();
            SingleComposer?.GetTextInput("tcol_" + role)?.SetValue(TraderColorHex(role));
            return true;
        };

        bool OnResetAllTraderColors()
        {
            config.TraderMarkerColors.Clear();
            SaveMapOptionsConfig();
            foreach (var role in TraderMarkers.Roles)
            {
                SingleComposer?.GetTextInput("tcol_" + role)?.SetValue(TraderColorHex(role));
            }
            return true;
        }

        void OnLayoutFeatureToggled(bool on)
        {
            config.LayoutEnabled = on;
            SaveMapOptionsConfig();
            // Same aftermath as the Layout screen's master switch: the zone geometry is re-derived,
            // buttons re-pack immediately, and zones cannot stay pinned through a switch-off
            // (SetLayoutPinned refuses to pin while the feature is off anyway).
            Layout?.Invalidate();
            ModSystem?.RefreshButtonPlacement();
            if (!on) ModSystem?.SetLayoutPinned(false);

        }

        void OnTlPathsToggled(bool on)
        {
            config.TranslocatorPathsEnabled = on;
            if (on) TlPaths?.ClearPending();
            SaveMapOptionsConfig();
        }

        void OnTlRecentMinutesChanged(string t)
        {
            // 0 is meaningful (never highlight), so no "> 0" guard.
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0)
            {
                config.TranslocatorRecentMinutes = v;
                SaveMapOptionsConfig();
            }
        }

        void OnTlColorChanged(string t) => SetHexIfComplete(t, h => config.TranslocatorMarkerColor = h);
        void OnTlRecentColorChanged(string t) => SetHexIfComplete(t, h => config.TranslocatorRecentColor = h);

        /// <summary>
        /// Commits a hex colour only once it is complete. These fire on every keystroke, so
        /// accepting partial input would repaint the map while the player is mid-type.
        /// </summary>
        void SetHexIfComplete(string text, Action<string> apply)
        {
            string body = (text ?? "").Trim().TrimStart('#');
            if (body.Length != 6) return;
            if (!int.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return;
            apply("#" + body.ToLowerInvariant());
            SaveMapOptionsConfig();
        }

        bool OnResetTlColors()
        {
            var defaults = new PinMatrixConfig();
            config.TranslocatorMarkerColor = defaults.TranslocatorMarkerColor;
            config.TranslocatorRecentColor = defaults.TranslocatorRecentColor;
            SaveMapOptionsConfig();
            SingleComposer?.GetTextInput("tlcolor")?.SetValue(config.TranslocatorMarkerColor);
            SingleComposer?.GetTextInput("tlrecentcolor")?.SetValue(config.TranslocatorRecentColor);
            return true;
        }

        /// <summary>
        /// Converts translocator markers left behind by another tool into Pin Matrix path markers,
        /// so dropping that tool does not mean losing the network you already mapped.
        ///
        /// Always previewed before anything is sent. The match is intentionally lenient — a
        /// translocator-ish word plus three numbers that read as a coordinate — and a lenient rule
        /// applied silently to someone's waypoint list is how you destroy work that took hours.
        /// </summary>
        bool OnAdoptTlWaypoints()
        {
            if (TlPaths == null) return true;
            if (batch.Busy) { capi.ShowChatMessage("[Pin Matrix] A bulk operation is still running..."); return true; }

            var items = TlPaths.FindAdoptable();
            if (items.Count == 0)
            {
                capi.ShowChatMessage(
                    "[Pin Matrix] Nothing to adopt — no waypoint looked like a translocator marker with "
                    + "coordinates in its name. Ones already in Pin Matrix format are skipped.");
                return true;
            }

            var commands = TlPaths.AdoptCommands(items);
            ShowConfirm(new PendingBulk
            {
                Title = $"Rename {items.Count} existing waypoint(s) into Pin Matrix translocator paths?",
                Warning = "Only the title, icon and colour change — nothing is moved or deleted, and the "
                        + "previous titles are shown below so you can check the coordinates were read correctly. "
                        + "Undo last bulk reverses it.",
                ConfirmText = $"Adopt {items.Count} waypoint(s)",
                Lines = items.ConvertAll(TlPaths.DescribeAdoptable).ToArray(),
                ReturnScreen = PmScreen.MapOptions,
                Execute = () => RunBatch(commands, $"Adopted {items.Count} translocator waypoint(s).", PmScreen.MapOptions),
            });
            return true;
        }

        bool OnScanTradersNow()
        {
            if (Traders == null) return true;
            if (!config.TraderMarkersEnabled)
            {
                capi.ShowChatMessage("[Pin Matrix] Trader auto-marking is off — switch it on first.");
                return true;
            }
            Traders.ClearPending();
            Traders.Scan();
            SingleComposer?.GetDynamicText("mapoptstatus")?.SetNewText(MapOptionsStatusText(), false, true, false);
            return true;
        }
    }
}
