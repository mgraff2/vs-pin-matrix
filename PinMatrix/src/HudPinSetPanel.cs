using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// The pin-set filter panel: a column of on/off rows down the right of the world map, one per
    /// saved set, in the spirit of the map's own Terrain / Waypoints / Prospecting toggles.
    ///
    /// WHY IT IS NOT IN THE MAP'S OWN TAB STRIP. That was the first plan and it does not survive
    /// contact with the numbers. Vanilla builds one tab per distinct <c>MapLayer.LayerGroupCode</c>,
    /// and <see cref="GuiElementVerticalTabs"/> lays them out at 25px + 5px spacing inside a strip
    /// fixed at 545px — about 18 tabs — then composes them onto a surface sized to those bounds, so
    /// tab nineteen is not scrolled, it is silently clipped. With vanilla's four, ours, and whatever
    /// other map mods contribute, a player would lose sets with nothing on screen to say why. The
    /// strip also draws labels with <c>DrawTextLineAt</c> — plain Cairo text, no icons, no counts.
    ///
    /// Our own panel has neither limit: it pages when it runs out of height, and it can draw the
    /// set's icon lit or greyed, which is the whole point of the icon option.
    ///
    /// WHY IT IS A WINDOW RATHER THAN A MAP-LAYER EXTRA. <c>MapLayer.ComposeDialogExtras</c> is the
    /// sanctioned way to hang a panel off the map dialog, but reaching it means being a MapLayer,
    /// and every MapLayer puts a tab in the strip above — which is exactly what this design is
    /// avoiding. So it is an ordinary window of ours, opened and closed with the full map by the
    /// same watcher that drives the map-screen buttons.
    ///
    /// WHY IT DOES NOT MOVE. It is the map's furniture, measured from the map's own composer and
    /// parked against its right edge, and it follows the map when GUI scale or window size moves
    /// that edge. It briefly grew a "Move" title bar in layout mode like the button windows, which
    /// was wrong twice over: a window that re-anchors itself every tick cannot honour a zone anyway,
    /// so the handle only ever fought the anchor, and the panel occupies space that was free
    /// precisely because the map does not use it. It is deliberately not a layout-grid citizen.
    /// </summary>
    public class HudPinSetPanel : GuiDialog
    {
        readonly PinMatrixConfig config;
        readonly PinSetService sets;
        readonly Action<string> toggle;
        readonly Action save;

        const double RowH = 30;
        const double IconCell = 24;
        const double HeaderH = 22;
        const double PagerH = 24;

        /// <summary>
        /// The drawer pull: a thin strip down the panel's map-facing edge that opens and closes it.
        ///
        /// Collapsed is the default, and the whole point — the map is CenterMiddle, so the free
        /// space either side is whatever the screen has left over after 1200px of map, and at higher
        /// GUI scales or in a window there is not much of it. A permanently open 150-320px column
        /// spends all of that on a list you look at twice a session. 16px does not.
        /// </summary>
        const double HandleW = 16;
        const double HandleH = 92;
        const double HandleGap = 4;

        /// <summary>
        /// The panel is sized to the longest name it is actually holding, between these bounds.
        ///
        /// A fixed width has to be wide enough for the worst name anyone might type, which makes it
        /// permanently too wide for the names most people do type. The minimum keeps a panel of
        /// three-letter sets from looking like a mistake; the maximum stops one long name dragging
        /// the whole column across the map, and names past it are ellipsized (their full text is in
        /// the row tooltip, which is where it can afford to be long).
        /// </summary>
        const double MinPanelW = 150;
        const double MaxPanelW = 320;

        /// <summary>Hard share of the screen the panel may never exceed, whatever the names say.</summary>
        const double MaxPanelScreenFraction = 0.2;

        /// <summary>Gap between the name and the count, so the two never touch on a snug fit.</summary>
        const double NameCountGap = 10;

        /// <summary>Width chosen at the last compose. Everything drawn must agree with this one number.</summary>
        double panelW = MinPanelW;

        double posX, posY;

        double composedForFrameW, composedForFrameH, composedForScale;
        string composedSignature = "";
        int page;

        /// <summary>Rows as they were composed, so the drawing and the hit-testing cannot disagree.</summary>
        readonly List<RowFace> rows = new List<RowFace>();
        readonly List<ElementBounds> rowBounds = new List<ElementBounds>();

        /// <summary>The drawer pull's hit zone. Never null while the panel is composed.</summary>
        ElementBounds handleBounds;

        /// <summary>
        /// Whether any set was hiding pins at the last compose. Snapshotted for the same reason the
        /// rows are: DrawHandle is a dynamic custom draw that can repaint at any moment, and walking
        /// the set list mid-paint would let the strip and its own tooltip disagree.
        /// </summary>
        bool handleFiltering;

        class RowFace
        {
            public string Id;
            public string Name;
            public string Icon;
            public bool On;
            public double[] Tint;
            public int Total;
            public int Visible;
        }

        public HudPinSetPanel(ICoreClientAPI capi, PinMatrixConfig config, PinSetService sets,
                              Action<string> toggle, Action save) : base(capi)
        {
            this.config = config;
            this.sets = sets;
            this.toggle = toggle;
            this.save = save;
            Compose();
        }

        /// <summary>Whether the drawer is open. Persisted, so it is a one-time click, not a habit.</summary>
        bool Expanded => config.PinSetPanelExpanded;

        public const string PanelDialogName = "pinmatrix-setpanel";

        public string DialogName => PanelDialogName;

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;

        // Same shelf as the map-screen buttons: above the world map (0.11) and the zone grid (0.15).
        public override double DrawOrder => 0.97;

        public override bool ShouldReceiveMouseEvents() => IsOpened();

        /// <summary>True when there is nothing to show, so the watcher keeps the panel closed.</summary>
        public bool IsEmpty => sets == null || sets.Buttoned.Count == 0;

        /// <summary>The rectangle the panel occupies, in unscaled units — the strip alone when shut.</summary>
        public URect OuterRect => new URect(posX, posY, AssemblyWidth() + 8, ContentHeight() + 8);

        /// <summary>Handle, then the body if it is open. The one place the two widths are added.</summary>
        double AssemblyWidth() => HandleW + (Expanded ? HandleGap + panelW : 0);

        /// <summary>Forces a rebuild — the editor calls this after the set list changes.</summary>
        public void MarkDirty() => composedSignature = null;

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Parks the panel just clear of the world map's right edge, and keeps it there.
        ///
        /// The full map is a CenterMiddle dialog nudged slightly left, so the right of the screen is
        /// genuinely free — that is what makes a side panel possible at all rather than something
        /// that has to overlap the map it belongs to. Measured from the map's own composer rather
        /// than assumed, because its width depends on GUI scale and on how much the player has
        /// resized their window — and re-measured on every watcher tick for the same reason, so a
        /// scale change moves the panel with the edge it belongs to instead of stranding it.
        /// </summary>
        public void AnchorToMap()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            panelW = PanelWidth();
            double wantW = AssemblyWidth();
            double x = screenW - wantW - 20;
            double y = 80;

            var mapBounds = MapDialogBounds(scale);
            if (mapBounds != null)
            {
                x = mapBounds[0] + mapBounds[2] + 12;
                y = mapBounds[1];
            }

            SetPosition(Math.Max(0, Math.Min(x, screenW - wantW - 8)),
                        Math.Max(0, Math.Min(y, screenH - ContentHeight() - 8)));
        }

        double[] MapDialogBounds(float scale)
        {
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            if (mapDlg == null || !mapDlg.IsOpened()) return null;

            foreach (var compo in mapDlg.Composers.Values)
            {
                var b = compo?.Bounds;
                if (compo == null || !compo.Enabled || b == null) continue;
                // The map's main composer, not its little layer extras: take the widest one.
                if (b.OuterWidth < 300 * scale) continue;
                return new[] { b.absX / scale, b.absY / scale, b.OuterWidth / scale, b.OuterHeight / scale };
            }
            return null;
        }

        void SetPosition(double unscaledX, double unscaledY)
        {
            if (Math.Abs(posX - unscaledX) < 0.5 && Math.Abs(posY - unscaledY) < 0.5) return;
            posX = unscaledX;
            posY = unscaledY;
            Recompose();
        }

        // ------------------------------------------------------------------ measuring

        ImageSurface measureSurface;
        Context measureCtx;

        /// <summary>
        /// How wide a string is in unscaled units, measured rather than estimated.
        ///
        /// The house rule elsewhere in this dialog is "~9.5 unscaled px per character", which is
        /// fine for a label whose text is known at compile time. Set names are typed by the player,
        /// so the estimate is wrong in both directions — "IIII" and "WWWW" are the same length and
        /// nothing like the same width — and here being wrong means either a truncated name or a
        /// column of dead space. Cairo already knows the answer; ask it.
        ///
        /// The scratch surface is 1x1 and lives as long as the panel: TextExtents needs a context,
        /// not a canvas, and creating one per name per compose would be the expensive way to do this.
        /// </summary>
        double MeasureUnscaled(string text, CairoFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            try
            {
                if (measureCtx == null)
                {
                    measureSurface = new ImageSurface(Format.Argb32, 1, 1);
                    measureCtx = new Context(measureSurface);
                }
                font.SetupContext(measureCtx);
                var ext = measureCtx.TextExtents(text);
                return (ext.Width + 2) / scale;
            }
            catch (Exception)
            {
                // Fall back to the house estimate rather than losing the panel over a measurement.
                return text.Length * 9.5;
            }
        }

        /// <summary>Trims a name until it fits, so an over-long one degrades instead of overflowing.</summary>
        string Fit(string text, double availableUnscaled, CairoFont font)
        {
            if (MeasureUnscaled(text, font) <= availableUnscaled) return text;

            for (int len = text.Length - 1; len > 0; len--)
            {
                string candidate = text.Substring(0, len) + "…";
                if (MeasureUnscaled(candidate, font) <= availableUnscaled) return candidate;
            }
            return "…";
        }

        /// <summary>
        /// Icon cell + the longest name + the widest count, clamped at both ends.
        ///
        /// THE CEILING IS THE SMALLER OF TWO THINGS. <see cref="MaxPanelW"/> is an absolute limit in
        /// unscaled units, and <see cref="MaxPanelScreenFraction"/> is a share of the screen. The
        /// absolute one alone is not enough: unscaled units already track GUI scale, but they say
        /// nothing about how wide the window is, so a constant that looks modest at 2560px is a
        /// quarter of the map on a small or windowed client. Whichever is tighter wins, and the
        /// minimum is applied last so a very narrow window still gets a usable panel rather than a
        /// sliver — overhanging slightly beats being unreadable.
        /// </summary>
        double PanelWidth()
        {
            var nameFont = CairoFont.WhiteSmallText();
            var countFont = CairoFont.WhiteDetailText();

            double longestName = 0, widestCount = 0;
            foreach (var r in AllRows())
            {
                longestName = Math.Max(longestName, MeasureUnscaled(r.Name, nameFont));
                widestCount = Math.Max(widestCount, MeasureUnscaled(CountText(r.Total), countFont));
            }

            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            double screenW = capi.Render.FrameWidth / scale;

            double ceiling = Math.Min(MaxPanelW, screenW * MaxPanelScreenFraction);
            double w = IconCell + 4 + longestName + NameCountGap + widestCount + 6;
            return Math.Max(MinPanelW, Math.Min(ceiling, Math.Ceiling(w)));
        }

        static string CountText(int total) => total > 999 ? "999+" : total.ToString();

        public override void Dispose()
        {
            measureCtx?.Dispose();
            measureSurface?.Dispose();
            measureCtx = null;
            measureSurface = null;
            base.Dispose();
        }

        // ------------------------------------------------------------------ contents

        int RowsPerPage()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            double screenH = capi.Render.FrameHeight / scale;

            // Whatever fits between where the panel starts and the bottom of the screen, minus the
            // header and the pager. Never fewer than three: a panel that can only ever show one row
            // at a time is worse than one that overhangs slightly.
            double room = screenH - posY - 24 - HeaderH - PagerH;
            return Math.Max(3, Math.Min(20, (int)(room / RowH)));
        }

        List<RowFace> AllRows()
        {
            var list = new List<RowFace>();
            if (sets == null) return list;

            foreach (var s in sets.Buttoned)
            {
                list.Add(new RowFace
                {
                    Id = s.Id,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? "set" : s.Name.Trim(),
                    Icon = s.ButtonIcon,
                    // Lit while anything the set matches is still on the map. A set matching nothing
                    // is greyed too — there is nothing showing, which is what grey says.
                    On = sets.VisibleCount(s) > 0,
                    Tint = PinSetService.ButtonTint(s),
                    Total = sets.TotalCount(s),
                    Visible = sets.VisibleCount(s)
                });
            }
            return list;
        }

        int PageCount()
        {
            int per = Math.Max(1, RowsPerPage());
            return Math.Max(1, (AllRows().Count + per - 1) / per);
        }

        double ContentHeight()
        {
            if (!Expanded) return HandleH;
            int shown = Math.Min(RowsPerPage(), Math.Max(1, AllRows().Count));
            return Math.Max(HandleH, HeaderH + shown * RowH + (PageCount() > 1 ? PagerH : 0));
        }

        /// <summary>What the panel is currently showing, so a rebuild happens only when it changes.</summary>
        string Signature()
        {
            var sb = new System.Text.StringBuilder();
            // The width is part of what is on screen: a rename that needs more room has to recompose
            // even when every name, count and state string is otherwise the same length.
            sb.Append(Expanded ? 'o' : 'c').Append('/')
              .Append(page).Append('/').Append(RowsPerPage()).Append('/').Append(PanelWidth()).Append(';');
            foreach (var r in AllRows())
            {
                sb.Append(r.Id).Append('|').Append(r.Name).Append('|').Append(r.Icon).Append('|')
                  .Append(r.On ? '1' : '0').Append('|').Append(r.Visible).Append('/').Append(r.Total).Append(';');
            }
            return sb.ToString();
        }

        public void RefreshIfNeeded()
        {
            if (capi.Render.FrameWidth == composedForFrameW
                && capi.Render.FrameHeight == composedForFrameH
                && RuntimeEnv.GUIScale == composedForScale
                && Signature() == composedSignature)
            {
                return;
            }
            Recompose();
        }

        void Recompose()
        {
            var old = IsEmpty ? null : SingleComposer;
            Compose();
            if (old != null) capi.World.RegisterCallback(_ => old.Dispose(), 250);
        }

        void Compose()
        {
            composedForFrameW = capi.Render.FrameWidth;
            composedForFrameH = capi.Render.FrameHeight;
            composedForScale = RuntimeEnv.GUIScale;
            composedSignature = Signature();
            panelW = PanelWidth();

            var all = AllRows();
            rows.Clear();
            rowBounds.Clear();
            handleBounds = null;

            // Same guard as the button windows: a composer with no children throws outright when the
            // background tries to size itself to them.
            if (all.Count == 0)
            {
                ClearComposers();
                return;
            }

            int per = Math.Max(1, RowsPerPage());
            int pages = Math.Max(1, (all.Count + per - 1) / per);
            page = Math.Min(Math.Max(0, page), pages - 1);

            if (Expanded)
            {
                for (int i = page * per; i < Math.Min(all.Count, (page + 1) * per); i++) rows.Add(all[i]);
            }

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(posX, posY);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui.CreateCompo(PanelDialogName, dialogBounds)
                .AddShadedDialogBG(bgBounds, false);

            composer.BeginChildElements(bgBounds);

            // The pull is the assembly's leftmost column in both states, so the drawer grows to the
            // right of it rather than the pull sliding out from under the cursor that just clicked
            // it. The one exception is a screen too narrow for the open body, where AnchorToMap's
            // clamp shifts the whole assembly left to keep it reachable.
            handleFiltering = FilteringCount(all) > 0;
            handleBounds = ElementBounds.Fixed(0, 0, HandleW, ContentHeight());
            composer.AddDynamicCustomDraw(handleBounds, (ctx, surface, b) => DrawHandle(ctx, b), "pmhandle");
            composer.AddHoverText(HandleTooltip(all), CairoFont.WhiteSmallText(), 240,
                handleBounds.FlatCopy(), "pmhandletip");

            if (!Expanded)
            {
                SingleComposer = composer.EndChildElements().Compose();
                return;
            }

            double bodyX = HandleW + HandleGap;
            double top = 0;

            var head = CairoFont.WhiteDetailText();
            composer.AddStaticText("Pin sets", head, ElementBounds.Fixed(bodyX + 2, top, panelW - 4, HeaderH));
            top += HeaderH;

            for (int i = 0; i < rows.Count; i++)
            {
                int index = i;
                var bounds = ElementBounds.Fixed(bodyX, top + i * RowH, panelW, RowH - 2);
                composer.AddDynamicCustomDraw(bounds, (ctx, surface, b) => DrawRow(ctx, b, index), "pmrow" + i);
                rowBounds.Add(bounds);

                var r = rows[i];
                string state = r.Total == 0 ? "no pins match this set right now"
                    : r.Visible == r.Total ? $"all {r.Total} showing"
                    : r.Visible == 0 ? $"all {r.Total} hidden"
                    : $"{r.Visible} of {r.Total} showing";
                string action = r.Total == 0 ? "" : (r.On ? " — click to hide them." : " — click to show them.");
                composer.AddHoverText(r.Name + "\n" + state + action, CairoFont.WhiteSmallText(), 240,
                    bounds.FlatCopy(), "pmrowtip" + i);
            }

            if (pages > 1)
            {
                double py = top + rows.Count * RowH;
                composer.AddSmallButton("<", () => { page--; Recompose(); return true; },
                    ElementBounds.Fixed(bodyX, py, 34, PagerH - 2), EnumButtonStyle.Small);
                composer.AddStaticText($"{page + 1}/{pages}",
                    CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center),
                    ElementBounds.Fixed(bodyX + 38, py + 3, panelW - 76, PagerH));
                composer.AddSmallButton(">", () => { page++; Recompose(); return true; },
                    ElementBounds.Fixed(bodyX + panelW - 34, py, 34, PagerH - 2), EnumButtonStyle.Small);
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        /// <summary>How many sets are currently holding pins off the map.</summary>
        static int FilteringCount(List<RowFace> all)
        {
            int n = 0;
            foreach (var r in all) if (r.Total > 0 && r.Visible < r.Total) n++;
            return n;
        }

        string HandleTooltip(List<RowFace> all)
        {
            int filtering = FilteringCount(all);
            string what = filtering == 0
                ? $"{all.Count} set{(all.Count == 1 ? "" : "s")}, none filtering"
                : $"{filtering} of {all.Count} hiding pins";
            return "Pin sets — " + what + (Expanded ? ".\nClick to close." : ".\nClick to open.");
        }

        /// <summary>
        /// The drawer pull, closed or open.
        ///
        /// The chevron is drawn rather than typed: a glyph depends on the font having it, and at
        /// 16px a missing one is an empty strip indistinguishable from a broken panel. It points the
        /// way the click will move the drawer.
        ///
        /// A set that is actively hiding pins re-colours the strip, because that is the one fact the
        /// collapsed state would otherwise swallow — pins missing from the map with nothing on
        /// screen saying why is exactly what the panel exists to prevent.
        /// </summary>
        void DrawHandle(Context ctx, ElementBounds bounds)
        {
            double w = bounds.InnerWidth, h = bounds.InnerHeight;
            bool filtering = handleFiltering;

            GuiElement.RoundRectangle(ctx, 0, 0, w, h, 2.0);
            if (filtering) ctx.SetSourceRGBA(0.34, 0.26, 0.13, 0.88);
            else ctx.SetSourceRGBA(0.16, 0.16, 0.15, 0.80);
            ctx.FillPreserve();
            if (filtering) ctx.SetSourceRGBA(0.92, 0.72, 0.32, 0.90);
            else ctx.SetSourceRGBA(0.62, 0.58, 0.42, 0.75);
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.Stroke();

            double cx = w / 2, cy = h / 2, r = GuiElement.scaled(5);
            double dir = Expanded ? -1 : 1;
            ctx.NewPath();
            ctx.MoveTo(cx - dir * r * 0.55, cy - r);
            ctx.LineTo(cx + dir * r * 0.55, cy);
            ctx.LineTo(cx - dir * r * 0.55, cy + r);
            ctx.LineWidth = GuiElement.scaled(2);
            ctx.LineCap = LineCap.Round;
            ctx.LineJoin = LineJoin.Round;
            ctx.SetSourceRGBA(0.94, 0.92, 0.84, 0.95);
            ctx.Stroke();
        }

        /// <summary>Opens or closes the drawer and remembers which, so it is one click per install.</summary>
        void ToggleDrawer()
        {
            config.PinSetPanelExpanded = !config.PinSetPanelExpanded;
            save?.Invoke();
            // The assembly changed width, so where it sits against the map's edge changes with it.
            AnchorToMap();
            Recompose();
        }

        /// <summary>
        /// One row: the set's icon, its name, and how many pins it holds.
        ///
        /// LIT MEANS SOMETHING IS SHOWING, GREY MEANS ALL OF IT IS HIDDEN — the same thing the map
        /// itself is telling you, said in the panel. Grey is a desaturated, dimmed glyph rather than
        /// a different colour, because colour is already carrying the set's own meaning here.
        ///
        /// Rows are read from the compose-time snapshot, never recomputed: this is a dynamic custom
        /// draw that can repaint at any moment, and asking the set service mid-paint would let the
        /// icon and its own tooltip disagree.
        /// </summary>
        void DrawRow(Context ctx, ElementBounds bounds, int index)
        {
            if (index < 0 || index >= rows.Count) return;
            var r = rows[index];

            double w = bounds.InnerWidth, h = bounds.InnerHeight;

            GuiElement.RoundRectangle(ctx, 0, 0, w, h, 2.0);
            if (r.On) ctx.SetSourceRGBA(0.26, 0.25, 0.21, 0.75);
            else ctx.SetSourceRGBA(0.10, 0.10, 0.10, 0.65);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(r.On ? 0.70 : 0.32, r.On ? 0.64 : 0.32, r.On ? 0.42 : 0.32, 0.85);
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.Stroke();

            double pad = GuiElement.scaled(3);
            double iconSize = GuiElement.scaled(IconCell) - pad * 2;
            double[] rgba = r.On ? r.Tint : new double[] { 0.55, 0.55, 0.55, 0.42 };

            if (!string.IsNullOrEmpty(r.Icon))
            {
                try
                {
                    capi.Gui.Icons.DrawIcon(ctx, "wp" + r.Icon.UcFirst(), pad, pad + GuiElement.scaled(1),
                        iconSize, iconSize, rgba);
                }
                catch (Exception)
                {
                    // An unloaded worldmap SVG throws mid-paint. A row without its glyph still
                    // carries the name and the count, and still toggles.
                }
            }
            else
            {
                // No icon chosen: a plain chip in the set's colour, so every row has something in
                // the same place and the column still scans vertically.
                GuiElement.RoundRectangle(ctx, pad, pad + GuiElement.scaled(4), iconSize * 0.7, iconSize * 0.7, 1.0);
                ctx.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba[3]);
                ctx.Fill();
            }

            // The count is placed first and the name is then fitted to what is left, so a long name
            // can never push the count off the row - at the panel's maximum width it ellipsizes
            // instead, and the full name is in the row's tooltip.
            var countFont = CairoFont.WhiteDetailText();
            string count = CountText(r.Total);
            double countW = MeasureUnscaled(count, countFont);

            var font = CairoFont.WhiteSmallText();
            font.Color = r.On ? new double[] { 1, 1, 1, 0.95 } : new double[] { 1, 1, 1, 0.45 };
            font.SetupContext(ctx);
            double textX = GuiElement.scaled(IconCell) + GuiElement.scaled(4);
            double available = panelW - IconCell - 4 - NameCountGap - countW - 4;
            capi.Gui.Text.DrawTextLine(ctx, font, Fit(r.Name, available, CairoFont.WhiteSmallText()),
                textX, GuiElement.scaled(4), false);

            countFont.Color = r.On ? new double[] { 1, 1, 1, 0.7 } : new double[] { 1, 1, 1, 0.35 };
            countFont.SetupContext(ctx);
            capi.Gui.Text.DrawTextLine(ctx, countFont, count,
                w - GuiElement.scaled(countW) - GuiElement.scaled(4), GuiElement.scaled(6), false);
        }

        // ------------------------------------------------------------------ input

        /// <summary>
        /// Ordered exactly like the editor's: composers first so the pager buttons and the title bar
        /// keep their claim, then our drawn rows, then the catch-all that swallows clicks landing on
        /// the panel background.
        /// </summary>
        public override void OnMouseDown(MouseEvent args)
        {
            if (args.Handled) return;

            foreach (var composer in Composers.Values.ToArray())
            {
                composer.OnMouseDown(args);
                if (args.Handled) return;
            }

            if (!IsOpened()) return;

            if (handleBounds != null && handleBounds.PointInside(args.X, args.Y))
            {
                args.Handled = true;
                ToggleDrawer();
                return;
            }

            for (int i = 0; i < rowBounds.Count && i < rows.Count; i++)
            {
                if (rowBounds[i] == null || !rowBounds[i].PointInside(args.X, args.Y)) continue;
                args.Handled = true;
                toggle?.Invoke(rows[i].Id);
                Recompose();
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
    }
}
