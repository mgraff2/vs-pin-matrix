using System;
using System.Collections.Generic;
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
            //
            // Every section header carries its own live status as a tooltip. It used to be one line
            // of text along the bottom naming all four at once, which meant the whole screen had to
            // stay on screen to read the state of any one part of it - and it was another row on a
            // screen whose problem is rows.
            c.AddStaticText("Trader markers", head, EB(4, y, 300, 26));
            c.AddHoverText(TraderStatusText(), tip, 300, EB(4, y, 300, 26).FlatCopy(), "tipstatustraders");
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

            // Folded away by default. Nine colours is the longest block on this screen and the one
            // least often touched; leaving it open spends a fifth of the screen's height on it.
            bool coloursOpen = config.TraderColorsExpanded;
            c.AddStaticText("Colour per specialisation", font, EB(4, y + 4, 250, 25));
            c.AddHoverText("Pick a colour per trade specialisation, or Reset a row to the Waypointer "
                + "default — so the same kind of trader reads as the same colour in either mod.",
                tip, 300, EB(4, y, 250, 26).FlatCopy(), "tipcolours");
            c.AddSmallButton(coloursOpen ? "Hide colours" : "Show colours", OnToggleTraderColors,
                EB(264, y, 150, 26), EnumButtonStyle.Small);
            // Scanning is not part of the colours, so it stays out here where it can be reached
            // without unfolding anything.
            c.AddSmallButton("Scan for traders now", OnScanTradersNow, EB(424, y, 200, 26), EnumButtonStyle.Small);
            y += 32;

            if (coloursOpen)
            {
                for (int i = 0; i < TraderMarkers.Roles.Length; i++)
                {
                    string role = TraderMarkers.Roles[i];
                    // Column-major, so the names still read alphabetically down each column.
                    double cx = (i / TraderGridRows) * TraderColW + 4;
                    double ry = y + (i % TraderGridRows) * TraderRowH;
                    c.AddStaticText(TraderMarkers.TitleFor(role), font, EB(cx + 4, ry + 4, 176, 24));

                    // A dropdown of drawn colours, not a hex box. The swatch that used to sit beside
                    // the box is gone with it: the dropdown paints the colour on every entry and on
                    // the closed control, so a separate chip would be the same fact twice.
                    string[] vals = TraderColorValues(role);
                    c.AddDropDown(vals, ColorLabels(vals, DefaultTraderColor(role)),
                        Math.Max(0, Array.IndexOf(vals, NormHex(TraderColorHex(role)))),
                        MakeTraderColorHandler(role), EB(cx + 186, ry, 140, 26), "tcol_" + role);
                    c.AddSmallButton("Reset", MakeTraderColorReset(role), EB(cx + 334, ry, 74, 26), EnumButtonStyle.Small);
                }
                y += TraderGridRows * TraderRowH + 4;

                // Inside the fold: it resets what the fold contains, so it belongs with it.
                c.AddSmallButton("Reset all colours", OnResetAllTraderColors, EB(4, y, 170, 26), EnumButtonStyle.Small);
                y += 32;
            }

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ================================================================ translocator paths
            c.AddStaticText("Translocator paths", head, EB(4, y, 300, 26));
            c.AddHoverText(TlStatusText(), tip, 300, EB(4, y, 300, 26).FlatCopy(), "tipstatustl");
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

            c.AddSwitch(OnTlAntsToggled, EB(4, y, 26, 26), "tlants", 23, 3);
            c.AddStaticText("Recent paths crawl (marching ants)", font, EB(36, y + 4, 330, 25));
            c.AddHoverText(
                "Draws a recently-used path as alternating bands of the Recent colour and the Path "
                + "colour that crawl from the pad you left towards the one you arrived at — so the "
                + "line shows the direction you travelled, which a flat line never could. Set both "
                + "colours the same and the bands stop being visible; this switch is the way to mean "
                + "it. Only recent paths are banded, so the drawing cost follows the hop you care "
                + "about and not the size of your network.",
                tip, 340, EB(4, y, 362, 28).FlatCopy(), "tiptlants");

            c.AddStaticText("Band", font, EB(376, y + 4, 60, 25));
            c.AddNumberInput(EB(440, y, 52, 28), OnTlAntsDashChanged, font, "tlantsdash");
            c.AddStaticText("px, speed", font, EB(498, y + 4, 88, 25));
            c.AddNumberInput(EB(590, y, 52, 28), OnTlAntsSpeedChanged, font, "tlantsspeed");
            c.AddStaticText("px/s", font, EB(648, y + 4, 50, 25));
            c.AddHoverText("How wide one colour band is and how fast the pattern crawls. Speed 0 leaves "
                + "them as static stripes, which still marks the hop out without anything moving on "
                + "the map.",
                tip, 300, EB(376, y, 322, 28).FlatCopy(), "tiptlantsnums");
            y += 34;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            // ================================================================ herty cups
            c.AddStaticText("Herty cups", head, EB(4, y, 300, 26));
            c.AddHoverText(HertyStatusText(), tip, 300, EB(4, y, 300, 26).FlatCopy(), "tipstatushc");
            y += 28;

            c.AddSwitch(OnHertyCupsToggled, EB(4, y, 26, 26), "hcenabled", 23, 3);
            c.AddStaticText("Mark Herty cups I place or collect from", font, EB(36, y + 4, 360, 25));
            c.AddHoverText(
                "Places a waypoint on a Herty cup the moment you put one up or take resin out of "
                + "one, titled after the tree it is tapping. Both triggers are things you did, which "
                + "is the point: a client cannot be told who placed a block, so marking cups you "
                + "have actually handled is the only way to get yours and not every tap on the "
                + "server. Nothing is marked for a cup you merely walked past, and nothing happens "
                + "at all without the Herty Cups mod installed. Requires that mod.",
                tip, 340, EB(4, y, 392, 28).FlatCopy(), "tiphc");

            c.AddStaticText("Skip if one within", font, EB(406, y + 4, 176, 25));
            c.AddNumberInput(EB(582, y, 56, 28), OnHertyCupDedupeChanged, font, "hcdedupe");
            c.AddStaticText("blocks", font, EB(644, y + 4, 60, 25));
            c.AddHoverText("Stops a second marker appearing for a cup that already has one. Kept "
                + "tight on purpose: a cup never moves, and two cups on neighbouring faces of the "
                + "same trunk are two real cups a block apart.",
                tip, 300, EB(406, y, 298, 28).FlatCopy(), "tiphcdedupe");

            y += 34;

            // Colour and icon on one row, both drawn rather than spelled: the same rule the trader
            // rows now follow. The title prefix and whether markers are pinned stay in pinmatrix.json
            // — they are typed once and never looked at again.
            c.AddStaticText("Marker", font, EB(36, y + 4, 70, 25));
            string[] hcCols = HertyColorValues();
            c.AddDropDown(hcCols, ColorLabels(hcCols, DefaultHertyColor),
                Math.Max(0, Array.IndexOf(hcCols, NormHex(config.HertyCupMarkerColor))),
                OnHertyCupColorChanged, EB(110, y, 140, 26), "hccolor");
            string[] hcIcons = HertyIconValues();
            c.AddDropDown(hcIcons, IconLabels(hcIcons, DefaultHertyIcon),
                Math.Max(0, Array.IndexOf(hcIcons, config.HertyCupMarkerIcon ?? DefaultHertyIcon)),
                OnHertyCupIconChanged, EB(258, y, 190, 26), "hcicon");
            c.AddSmallButton("Reset", OnResetHertyMarker, EB(456, y, 74, 26), EnumButtonStyle.Small);
            c.AddHoverText("The colour and icon every cup marker gets. Both lists show the real thing "
                + "rather than a code — the icons are every waypoint icon the game has, drawn.",
                tip, 320, EB(36, y, 494, 28).FlatCopy(), "tiphcmarker");
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
            c.AddHoverText(LayoutStatusTip(), tip, 300, EB(4, y, 300, 26).FlatCopy(), "tipstatuslayout");
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

            // Deliberately NOT gated on the switch above. Nothing in the engine resizes a dialog, so
            // a smaller screen needs a smaller interface scale whether or not you snap anything to a
            // grid — this answers that, and stands on its own switch.
            c.AddSwitch(OnAutoScaleToggled, EB(4, y, 26, 26), "layoutautoscale", 23, 3);
            c.AddStaticText("Fit interface scale to the screen", font, EB(36, y + 4, 320, 25));
            c.AddHoverText(
                "When the screen changes size — a remote session at a lower resolution, a different "
                + "monitor, a resized window — the game's GUI scale is re-derived so the interface "
                + "keeps the proportions you chose. No window in Vintage Story resizes itself, so "
                + "without this a screen half as wide leaves every dialog taking twice the share of "
                + "it, and the fix is a trip into Settings every time.\n\n"
                + "It measures from a reference: the scale you last set by hand and the screen you "
                + "set it on. Going to a smaller screen and back returns exactly where you were, "
                + "because each answer comes from that fixed reference rather than from the last "
                + "answer. Set the reference with the scale slider on the Map windows layout screen.",
                tip, 360, EB(4, y, 356, 28).FlatCopy(), "tipautoscale");
            c.AddDynamicText(AutoScaleStatusText(), tip, EB(366, y + 5, DW - 380, 25), "autoscalestatus");
            y += 34;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 14;

            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(4, y, 80, 30));
        }

        static string SwatchKey(int col) => "traderswatches" + col;
        static string TlSwatchKey(bool recent) => recent ? "tlrecentswatch" : "tlswatch";
        const string HertyCupSwatchKey = "hcswatch";

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

        void DrawHertyCupSwatch(Context ctx) =>
            PaintChip(ctx, TraderMarkers.ParseHex(config.HertyCupMarkerColor), GuiElement.scaled(2));

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

        // ---------------------------------------------------------------- section status tooltips
        //
        // One per section header, replacing the single line that used to run along the bottom naming
        // all four at once. A tooltip on the thing it describes is read where you are already
        // looking, and costs no rows on a screen that is short of them.

        public string TraderStatusText() => config.TraderMarkersEnabled
            ? $"ON — {Traders?.MarkedThisSession ?? 0} trader(s) marked this session."
            : "OFF — no traders are being marked.";

        public string TlStatusText() => config.TranslocatorPathsEnabled
            ? $"ON — {TlPaths?.CurrentPaths()?.Count ?? 0} path(s) known, "
              + $"{TlPaths?.RecordedThisSession ?? 0} travelled this session."
            : "OFF — no translocator paths are being recorded.";

        public string HertyStatusText() => config.HertyCupMarkersEnabled
            ? $"ON — {HertyCups?.MarkedThisSession ?? 0} cup(s) marked this session."
            : "OFF — no Herty cups are being marked.";

        public string LayoutStatusTip() => config.LayoutEnabled
            ? $"ON — {config.LayoutAssignments.Count} window zone(s) remembered."
            : "OFF — no windows are being snapped.";

        /// <summary>
        /// Repaints the four header tooltips. They are baked at compose like any other label, so a
        /// count that moves while the screen is open needs saying again.
        /// </summary>
        void RefreshStatusTooltips()
        {
            var c = SingleComposer;
            if (c == null) return;
            c.GetHoverText("tipstatustraders")?.SetNewText(TraderStatusText());
            c.GetHoverText("tipstatustl")?.SetNewText(TlStatusText());
            c.GetHoverText("tipstatushc")?.SetNewText(HertyStatusText());
            c.GetHoverText("tipstatuslayout")?.SetNewText(LayoutStatusTip());
        }

        // ---------------------------------------------------------------- colour and icon choices

        /// <summary>Lower-cased "#rrggbb", so a stored value and a palette entry compare equal.</summary>
        static string NormHex(string hex)
        {
            string body = (hex ?? "").Trim().TrimStart('#');
            return body.Length == 6 ? "#" + body.ToLowerInvariant() : null;
        }

        /// <summary>
        /// The colours a marker dropdown offers: this marker's own default first, then every colour
        /// in the game's waypoint palette, then whatever is currently set if it is none of those.
        ///
        /// That last one matters — a hex typed into pinmatrix.json by hand, or set by an older build
        /// that had a text box here, must still appear in the list or opening this screen would
        /// silently change it to something else.
        /// </summary>
        string[] ColorChoices(string defaultHex, string currentHex)
        {
            var vals = new List<string>();
            void Add(string h) { h = NormHex(h); if (h != null && !vals.Contains(h)) vals.Add(h); }

            Add(defaultHex);
            foreach (int argb in svc.PaletteColors()) Add(WpCommands.ColorHex(argb));
            Add(currentHex);
            return vals.ToArray();
        }

        /// <summary>Dropdown labels: the colour drawn, its hex, and which one is the default.</summary>
        static string[] ColorLabels(string[] values, string defaultHex)
        {
            string def = NormHex(defaultHex);
            return values.Select(h =>
                $"<{ColorSwatchComponent.TagName} color=\"{h}\"/> {h}" + (h == def ? "  (default)" : ""))
                .ToArray();
        }

        static string[] IconLabels(string[] values, string defaultIcon) => values
            .Select(code => $"<{IconGlyphComponent.TagName} code=\"{code}\"/> {code}"
                          + (code == defaultIcon ? "  (default)" : ""))
            .ToArray();

        string DefaultTraderColor(string role) =>
            TraderMarkers.DefaultRoleColors.TryGetValue(role, out var d) ? d : TraderMarkers.DefaultColor;

        string[] TraderColorValues(string role) => ColorChoices(DefaultTraderColor(role), TraderColorHex(role));

        public const string DefaultHertyColor = "#c98b3a";
        public const string DefaultHertyIcon = "vessel";

        string[] HertyColorValues() => ColorChoices(DefaultHertyColor, config.HertyCupMarkerColor);

        /// <summary>Every waypoint icon the game can actually draw, with the default first.</summary>
        string[] HertyIconValues()
        {
            var list = new List<string>();
            if (DrawableIconCodes().Contains(DefaultHertyIcon)) list.Add(DefaultHertyIcon);
            foreach (string code in DrawableIconCodes()) if (!list.Contains(code)) list.Add(code);
            if (list.Count == 0) list.Add(DefaultHertyIcon);
            return list.ToArray();
        }

        bool OnToggleTraderColors()
        {
            config.TraderColorsExpanded = !config.TraderColorsExpanded;
            SaveMapOptionsConfig();
            Recompose();
            return true;
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
                c.GetDropDown("tcol_" + role)?.SetSelectedValue(NormHex(TraderColorHex(role)));
            }

            c.GetSwitch("layoutfeature").SetValue(config.LayoutEnabled);
            c.GetSwitch("tlenabled").SetValue(config.TranslocatorPathsEnabled);
            c.GetNumberInput("tlrecentmins").SetValue(
                config.TranslocatorRecentMinutes.ToString("0.#", CultureInfo.InvariantCulture));
            c.GetTextInput("tlcolor")?.SetValue(config.TranslocatorMarkerColor);
            c.GetTextInput("tlrecentcolor")?.SetValue(config.TranslocatorRecentColor);
            c.GetSwitch("layoutautoscale")?.SetValue(config.LayoutAutoScale);
            c.GetSwitch("hcenabled")?.SetValue(config.HertyCupMarkersEnabled);
            c.GetDropDown("hccolor")?.SetSelectedValue(NormHex(config.HertyCupMarkerColor));
            c.GetDropDown("hcicon")?.SetSelectedValue(config.HertyCupMarkerIcon ?? DefaultHertyIcon);
            c.GetNumberInput("hcdedupe")?.SetValue(
                config.HertyCupDedupeRadius.ToString("0.#", CultureInfo.InvariantCulture));
            c.GetSwitch("tlants")?.SetValue(config.TranslocatorRecentAnts);
            c.GetNumberInput("tlantsdash")?.SetValue(
                config.TranslocatorAntsDashPx.ToString("0.#", CultureInfo.InvariantCulture));
            c.GetNumberInput("tlantsspeed")?.SetValue(
                config.TranslocatorAntsSpeed.ToString("0.#", CultureInfo.InvariantCulture));
        }

        void SaveMapOptionsConfig()
        {
            config.Clamp();
            capi.StoreModConfig(config, "pinmatrix.json");
            RefreshStatusTooltips();
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

        /// <summary>
        /// A colour picked for one specialisation.
        ///
        /// Choosing the default *removes* the override rather than storing it, so the row keeps
        /// tracking the Waypointer palette if that ever moves — the same thing Reset does, reached
        /// by picking the entry marked (default).
        /// </summary>
        SelectionChangedDelegate MakeTraderColorHandler(string role) => (code, selected) =>
        {
            string hex = NormHex(code);
            if (hex == null) return;

            if (hex == NormHex(DefaultTraderColor(role))) config.TraderMarkerColors.Remove(role);
            else config.TraderMarkerColors[role] = hex;
            SaveMapOptionsConfig();
        };

        ActionConsumable MakeTraderColorReset(string role) => () =>
        {
            config.TraderMarkerColors.Remove(role);
            SaveMapOptionsConfig();
            SingleComposer?.GetDropDown("tcol_" + role)?.SetSelectedValue(NormHex(TraderColorHex(role)));
            return true;
        };

        bool OnResetAllTraderColors()
        {
            config.TraderMarkerColors.Clear();
            SaveMapOptionsConfig();
            foreach (var role in TraderMarkers.Roles)
            {
                SingleComposer?.GetDropDown("tcol_" + role)?.SetSelectedValue(NormHex(TraderColorHex(role)));
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

        void OnAutoScaleToggled(bool on)
        {
            config.LayoutAutoScale = on;
            // Switching it on with no reference yet adopts the screen you are on now — the size you
            // are looking at is, by definition, the size you are happy with.
            if (on && Layout != null && Layout.FittedScale() == null) Layout.CaptureScaleBase();
            SaveMapOptionsConfig();
            SingleComposer?.GetDynamicText("autoscalestatus")?.SetNewText(AutoScaleStatusText());
        }

        string AutoScaleStatusText()
        {
            if (!config.LayoutAutoScale) return "";
            if (config.LayoutBaseScreenW <= 0 || config.LayoutBaseScale <= 0) return "reference not set yet";
            return $"reference {config.LayoutBaseScale:0.###}x at "
                 + $"{config.LayoutBaseScreenW}x{config.LayoutBaseScreenH}";
        }

        void OnHertyCupsToggled(bool on)
        {
            config.HertyCupMarkersEnabled = on;
            // Same courtesy as the translocator switch: turning it back on forgets what we thought
            // we had already added, so markers deleted in between can be re-made by using the cup.
            if (on) HertyCups?.ClearPending();
            SaveMapOptionsConfig();
            RefreshStatusTooltips();
        }

        void OnHertyCupColorChanged(string code, bool selected)
        {
            string hex = NormHex(code);
            if (hex == null) return;
            config.HertyCupMarkerColor = hex;
            SaveMapOptionsConfig();
        }

        void OnHertyCupIconChanged(string code, bool selected)
        {
            if (string.IsNullOrEmpty(code)) return;
            config.HertyCupMarkerIcon = code;
            SaveMapOptionsConfig();
        }

        bool OnResetHertyMarker()
        {
            config.HertyCupMarkerColor = DefaultHertyColor;
            config.HertyCupMarkerIcon = DefaultHertyIcon;
            SaveMapOptionsConfig();
            SingleComposer?.GetDropDown("hccolor")?.SetSelectedValue(DefaultHertyColor);
            SingleComposer?.GetDropDown("hcicon")?.SetSelectedValue(DefaultHertyIcon);
            return true;
        }

        void OnHertyCupDedupeChanged(string t) => SetClampedNumber(t, v => config.HertyCupDedupeRadius = v);

        void OnTlAntsToggled(bool on)
        {
            config.TranslocatorRecentAnts = on;
            SaveMapOptionsConfig();
        }

        void OnTlAntsDashChanged(string t) => SetClampedNumber(t, v => config.TranslocatorAntsDashPx = v);
        void OnTlAntsSpeedChanged(string t) => SetClampedNumber(t, v => config.TranslocatorAntsSpeed = v);

        /// <summary>
        /// Shared by every plain numeric box on this screen. Half-typed numbers are ignored rather
        /// than clamped: clamping mid-keystroke rewrites the box under the player, so "20" becomes
        /// unreachable if you pass through a rejected "2". Config.Clamp has the last word when the
        /// value is read.
        /// </summary>
        void SetClampedNumber(string t, Action<double> set)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
            set(v);
            config.Clamp();
            SaveMapOptionsConfig();
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
            RefreshStatusTooltips();
            return true;
        }
    }
}
