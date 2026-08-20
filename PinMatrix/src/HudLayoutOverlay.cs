using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace PinMatrix
{
    /// <summary>
    /// The zone overlay: translucent cells with glowing edges, shown while layout mode is on.
    ///
    /// Deliberately invisible to the input system. ShouldReceiveMouseEvents returns false, so this
    /// dialog is never even offered a mouse event and therefore cannot swallow one — dragging a
    /// window is detected by *watching*, not by intercepting. When the left button goes down it
    /// records where every open dialog composer sits; whichever one has moved away from that
    /// baseline while the button is still down is the one being dragged. That is worth the small
    /// awkwardness: the alternative
    /// (sitting early in the dispatch chain and politely declining every event) is one careless
    /// edit away from eating another mod's clicks, and this mod already carries a scar from
    /// exactly that class of bug on the keyboard side (see HudPinMatrixMapButton.TextInputHasFocus).
    /// </summary>
    public class HudLayoutOverlay : GuiDialog
    {
        readonly LayoutManager layout;
        readonly PinMatrixConfig config;

        int whiteTexId;
        bool lastLeftDown;
        string dragging;
        int hoverCol = -1, hoverRow = -1;

        /// <summary>Where each composer sat when the button went down — the drag baseline.</summary>
        readonly Dictionary<string, double[]> pressBaseline = new Dictionary<string, double[]>();

        /// <summary>Unscaled pixels a window must move before it counts as "being dragged".</summary>
        const double DragThreshold = 3.0;

        public HudLayoutOverlay(ICoreClientAPI capi, PinMatrixConfig config, LayoutManager layout) : base(capi)
        {
            this.config = config;
            this.layout = layout;
        }

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;

        // The grid is a backdrop, not a scrim. It has to clear the world map surface (0.11) or it
        // would be invisible on the very screen it exists for, but it must stay *under* real
        // windows — the Window layout screen configuring the grid should not be sitting behind it,
        // and neither should the editor or the escape menu.
        //
        // Vanilla's ladder (GuiDialog.DrawOrder docs): worldmap HUD 0.07, most dialogs 0.1,
        // worldmap dialog 0.11, inventories/config dialogs 0.2, escape menu 0.89. Our editor is 0.2
        // and the waypoint editor 0.25, so 0.15 sits in the gap: above the map, below every window.
        // The map-screen button stack stays at 0.97 deliberately — it is fixed furniture the player
        // cannot drag out from under the grid.
        public override double DrawOrder => 0.15;

        // Not in the input path at all. See the class comment.
        public override bool ShouldReceiveMouseEvents() => false;
        public override bool ShouldReceiveKeyboardEvents() => false;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // Freeze the obstacle set at the moment the player asks to see it, rather than letting
            // it drift while they look at it.
            layout.RecomputeBlocked();
            pressBaseline.Clear();
            dragging = null;
            lastLeftDown = false;
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            dragging = null;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            // No composers of our own — base would iterate an empty dictionary. Everything here is
            // drawn directly, which also keeps the overlay out of the GUI element/focus machinery.
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            UpdateHover(scale);
            TrackDrag(scale);
            DrawZones(scale);
            DrawLabels(scale);
            DrawHint(scale);
        }

        // ------------------------------------------------------------------ interaction

        void UpdateHover(float scale)
        {
            double ux = capi.Input.MouseX / scale;
            double uy = capi.Input.MouseY / scale;
            if (!layout.Grid.CellAt(ux, uy, out hoverCol, out hoverRow)) { hoverCol = hoverRow = -1; }
        }

        /// <summary>
        /// Watches for a window being dragged and snaps it on release. Purely observational: the
        /// dragging itself is done by whichever mod owns the window, using its own title bar.
        /// </summary>
        void TrackDrag(float scale)
        {
            bool down = capi.Input.MouseButton.Left;

            var now = new Dictionary<string, double[]>();
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened() || gui == this) continue;

                // Other mods' HUDs are never moved by us, so they are never drag candidates. Our own
                // button windows are the exception: they are HUDs so they do not steal focus or the
                // mouse grab during normal play, but they are ours to move and the player expects
                // them to snap like anything else.
                if (gui.DialogType != EnumDialogType.Dialog && !(gui is HudPinMatrixButtonWindow)) continue;

                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    var b = compo?.Bounds;
                    if (compo == null || !compo.Enabled || b == null || compo.DialogName == null) continue;
                    now[compo.DialogName] = new[] { b.absX, b.absY };
                }
            }

            if (down && !lastLeftDown)
            {
                // Button just went down: remember where everything was, so we can tell what moves.
                pressBaseline.Clear();
                foreach (var pair in now) pressBaseline[pair.Key] = pair.Value;
                dragging = null;
            }
            else if (down)
            {
                if (dragging == null)
                {
                    double threshold = DragThreshold * scale;
                    foreach (var pair in now)
                    {
                        if (!pressBaseline.TryGetValue(pair.Key, out var from)) continue;
                        if (Math.Abs(pair.Value[0] - from[0]) > threshold ||
                            Math.Abs(pair.Value[1] - from[1]) > threshold)
                        {
                            dragging = pair.Key;
                            break;
                        }
                    }
                }
            }
            else if (lastLeftDown)
            {
                // Released. If something was dragged onto a usable cell, that is the snap.
                if (dragging != null && hoverCol >= 0 && hoverRow >= 0)
                {
                    // Deferred out of the render loop on purpose: a snap can recompose the window it
                    // lands on (a snap re-anchors and re-composes it), and rebuilding a composer while the
                    // GUI is mid-render is the kind of thing that works until it does not.
                    string target = dragging;
                    int col = hoverCol, row = hoverRow;
                    capi.Event.RegisterCallback(_ =>
                    {
                        // One rule, one wording: the refusal the player reads is the same string the
                        // cell was painted red from, so the grid can never look droppable and then
                        // refuse without saying why.
                        string why = layout.WhyCannotSnap(target, col, row);
                        if (why != null)
                        {
                            capi.ShowChatMessage("[Pin Matrix] " + why);
                            return;
                        }

                        layout.Snap(target, col, row);
                        foreach (var evicted in layout.LastSnapEvicted)
                        {
                            capi.ShowChatMessage("[Pin Matrix] Released \"" + evicted + "\" — the tab row is the "
                                + "button bar's, so it is back where its own mod put it.");
                        }
                    }, 0);
                }
                dragging = null;
                pressBaseline.Clear();
            }

            lastLeftDown = down;
        }

        // ------------------------------------------------------------------ drawing

        void DrawZones(float scale)
        {
            EnsureTexture();
            var grid = layout.Grid;
            bool avoiding = config.LayoutAvoidHuds;

            float screenW = (float)(grid.ScreenW * scale);
            float screenH = (float)(grid.ScreenH * scale);

            // Drawn as a lattice, not as one quad per cell. At 20x20 that is 400 cells; a fill plus
            // an outline each would be 800 draw calls every frame. Lines are 42, and a fine grid
            // reads better as a lattice anyway. Only the cells that mean something get filled:
            // blocked ones, the tab row, and the landing preview.
            for (int col = 0; col <= grid.Cols; col++)
            {
                float x = (float)(col * grid.CellW * scale);
                capi.Render.RenderRectangle(x, 0, 150f, 1, screenH, GridLineColor);
            }
            for (int row = 0; row <= grid.Rows; row++)
            {
                float y = (float)(row * grid.CellH * scale);
                capi.Render.RenderRectangle(0, y, 150f, screenW, 1, GridLineColor);
            }

            // The tab row gets a standing tint so it is obvious which row the button bar spans —
            // but only while tab-row mode is actually on. Tinting it in the other modes advertises
            // behaviour that is not switched on.
            if (layout.ButtonRowActive && layout.ButtonRowIndex < grid.Rows)
            {
                float y = (float)(layout.ButtonRowIndex * grid.CellH * scale);
                float h = (float)(grid.CellH * scale);
                capi.Render.Render2DTexturePremultipliedAlpha(whiteTexId, 0, y, screenW, h, 149f,
                    Premultiplied(0.55f, 0.45f, 0.95f, 0.10f));
            }

            if (avoiding)
            {
                for (int row = 0; row < grid.Rows; row++)
                {
                    for (int col = 0; col < grid.Cols; col++)
                    {
                        if (!layout.IsBlocked(col, row)) continue;
                        var cell = grid.Cell(col, row);
                        capi.Render.Render2DTexturePremultipliedAlpha(whiteTexId,
                            (float)(cell.X * scale), (float)(cell.Y * scale),
                            (float)(cell.W * scale), (float)(cell.H * scale), 151f,
                            Premultiplied(0.75f, 0.15f, 0.15f, 0.16f));
                    }
                }
            }

            if (hoverCol < 0 || hoverRow < 0) return;

            // Painted from the same rule the drop is judged by, so a refusing cell always looks
            // refusing. With nothing being dragged there is no window to judge, so it falls back to
            // the standing HUD tint.
            bool hoverBlocked = dragging != null
                ? layout.WhyCannotSnap(dragging, hoverCol, hoverRow) != null
                : avoiding && layout.IsBlocked(hoverCol, hoverRow);
            var hover = grid.Cell(hoverCol, hoverRow);
            capi.Render.Render2DTexturePremultipliedAlpha(whiteTexId,
                (float)(hover.X * scale), (float)(hover.Y * scale),
                (float)(hover.W * scale), (float)(hover.H * scale), 152f,
                hoverBlocked ? Premultiplied(0.85f, 0.2f, 0.2f, 0.30f)
                             : Premultiplied(0.35f, 0.85f, 0.45f, 0.25f));

            // While dragging, outline where the window will actually end up. On a fine lattice one
            // cell is far smaller than most windows, so highlighting the cell alone would say
            // almost nothing about the result.
            if (dragging != null && !hoverBlocked &&
                layout.TryLandingRect(dragging, hoverCol, hoverRow, out var landing))
            {
                capi.Render.RenderRectangle(
                    (float)(landing.X * scale), (float)(landing.Y * scale), 153f,
                    (float)(landing.W * scale), (float)(landing.H * scale),
                    LandingColor);
            }
        }

        static readonly int GridLineColor = ColorUtil.ToRgba(120, 210, 190, 150);
        static readonly int LandingColor = ColorUtil.ToRgba(245, 110, 240, 130);

        // ------------------------------------------------------------------ labels & hint

        /// <summary>Column/row index labels, generated once per grid shape and reused every frame.</summary>
        readonly Dictionary<string, LoadedTexture> labelCache = new Dictionary<string, LoadedTexture>();
        LoadedTexture hintTexture;

        LoadedTexture Label(string text)
        {
            if (labelCache.TryGetValue(text, out var tex) && tex != null) return tex;

            // Gold with a dark stroke rather than dimmed-down white. The labels are small enough
            // not to compete with the windows being placed, and the stroke is what keeps them
            // readable over a bright map or pale terrain — legibility from contrast, not from
            // cranking the alpha up until they shout.
            var font = CairoFont.WhiteSmallText()
                .WithColor(new double[] { 1.0, 0.82, 0.34, 0.95 })
                .WithStroke(new double[] { 0.0, 0.0, 0.0, 0.85 }, 1.5);
            tex = capi.Gui.TextTexture.GenTextTexture(text, font);
            labelCache[text] = tex;
            return tex;
        }

        void DrawLabels(float scale)
        {
            var grid = layout.Grid;

            // Column indices along the top edge, row indices down the left edge — enough to read a
            // coordinate off the screen without labelling all 400 cells.
            for (int col = 0; col < grid.Cols; col++)
            {
                var tex = Label(col.ToString());
                float x = (float)((col * grid.CellW) * scale) + 4;
                capi.Render.Render2DLoadedTexture(tex, x, 3, 154f);
            }
            for (int row = 0; row < grid.Rows; row++)
            {
                var tex = Label(row.ToString());
                float y = (float)((row * grid.CellH) * scale) + 3;
                capi.Render.Render2DLoadedTexture(tex, 4, y + 14, 154f);
            }
        }

        void DrawHint(float scale)
        {
            if (hintTexture == null)
            {
                var font = CairoFont.WhiteSmallText().WithColor(new double[] { 0.9, 0.95, 1.0, 0.75 });
                hintTexture = capi.Gui.TextTexture.GenTextTexture(
                    "Drag a window by its title bar and release it on a zone \u2014 its top-left corner snaps there. "
                    + "Red zones are blocked by a HUD.", font);
            }

            float x = (float)(capi.Render.FrameWidth - hintTexture.Width) / 2;
            float y = (float)capi.Render.FrameHeight - hintTexture.Height - 90f * scale;

            // Near-opaque: the hint sits over the map itself, and at lower alphas bright terrain
            // bleeds through enough to make the text genuinely hard to read.
            capi.Render.Render2DTexturePremultipliedAlpha(whiteTexId,
                x - 10, y - 5, hintTexture.Width + 20, hintTexture.Height + 10, 154f,
                Premultiplied(0.02f, 0.03f, 0.05f, 0.88f));
            capi.Render.Render2DLoadedTexture(hintTexture, x, y, 155f);
        }

        /// <summary>
        /// GUI textures are composited premultiplied, so the tint has to be premultiplied too or a
        /// translucent fill comes out far brighter than asked for.
        /// </summary>
        static Vec4f Premultiplied(float r, float g, float b, float a) => new Vec4f(r * a, g * a, b * a, a);

        void EnsureTexture()
        {
            if (whiteTexId != 0) return;

            // A 2x2 opaque white quad, tinted per draw. Cheaper and far less code than composing a
            // GUI element per cell, and it never touches the focus/tab machinery.
            var surface = new ImageSurface(Format.Argb32, 2, 2);
            var ctx = new Context(surface);
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Paint();
            whiteTexId = capi.Gui.LoadCairoTexture(surface, false);
            ctx.Dispose();
            surface.Dispose();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (whiteTexId != 0)
            {
                capi.Gui.DeleteTexture(whiteTexId);
                whiteTexId = 0;
            }
            foreach (var tex in labelCache.Values) tex?.Dispose();
            labelCache.Clear();
            hintTexture?.Dispose();
            hintTexture = null;
        }
    }
}
