using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// Small floating "Pin Matrix Editor" button shown while the full world map dialog is open.
    /// Kept as its own HUD dialog (rather than injecting into the map composer) so the vanilla
    /// map layout, layer tabs and recompose logic stay untouched.
    /// </summary>
    public class HudPinMatrixMapButton : GuiDialog
    {
        readonly Action onClick;
        readonly PinMatrixConfig config;
        int rightMargin = DefaultRightMargin;
        int yOffset = DefaultYOffset;

        public const int DefaultYOffset = 120;
        public const int DefaultRightMargin = 100;

        public HudPinMatrixMapButton(ICoreClientAPI capi, PinMatrixConfig config, Action onClick) : base(capi)
        {
            this.config = config;
            this.onClick = onClick;
            Compose();
        }

        public int CurrentRightMargin => rightMargin;
        public int CurrentYOffset => yOffset;

        /// <summary>Back to the preferred top slot; called when the map (re)opens so hysteresis starts fresh.</summary>
        public void ResetOffset() => SetOffset(DefaultRightMargin, DefaultYOffset);

        /// <summary>Moves the button (used to dodge other map-screen panels: layer filters, other mods' dialogs).</summary>
        public void SetOffset(int fromRight, int fromTop)
        {
            if (fromRight == rightMargin && fromTop == yOffset) return;
            capi.Logger.Debug("[pinmatrix] mapbtn move: right={0} y={1} -> right={2} y={3}", rightMargin, yOffset, fromRight, fromTop);
            rightMargin = fromRight;
            yOffset = fromTop;
            var old = SingleComposer;
            Compose();
            if (old != null) capi.World.RegisterCallback(_ => old.Dispose(), 250);
        }

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;

        // The full world map draws at 0.11; without this the button renders behind it
        public override double DrawOrder => 0.2;

        // Not focusable, so opt in to keyboard events while shown (the P shortcut below).
        // Opted out entirely when the shortcut is off, so nothing of ours is in the key path at all.
        public override bool ShouldReceiveKeyboardEvents() => IsOpened() && config.MapButtonShortcutKey;

        public override void OnKeyDown(KeyEvent args)
        {
            base.OnKeyDown(args);
            if (args.Handled) return;
            if (!config.MapButtonShortcutKey) return;
            if (args.KeyCode != (int)GlKeys.P || args.CtrlPressed || args.AltPressed || args.ShiftPressed) return;

            // Only while the world map itself is the focused dialog — not chat or another input
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            if (mapDlg == null || !mapDlg.IsOpened() || !mapDlg.Focused) return;

            // ...and not while the player is typing into any text field (see TextInputHasFocus)
            if (TextInputHasFocus(capi)) return;

            args.Handled = true;
            onClick?.Invoke();
        }

        /// <summary>
        /// True when a text field anywhere in the open GUI has keyboard focus, i.e. the player is
        /// typing rather than using a shortcut.
        ///
        /// CRITICAL compat (Boat Autopilot, and any mod that puts an input on the map screen).
        /// Mods attach their map panels to the vanilla world map dialog as extra composers — Boat
        /// Autopilot's route planner adds "worldmap-layer-boatroutes" with route-name and filter
        /// text inputs. Vanilla's own protection against a shortcut eating those keystrokes is
        /// GuiElementEditableTextBase.OnKeyDown, which marks *every* key handled while the field
        /// has focus. That protection never reaches us: GuiManager dispatches OnKeyDown down
        /// OpenedGuis in descending DrawOrder, and this HUD sits at 0.2 versus the map dialog's
        /// 0.11 — so we see the keystroke first and would consume the "p" out of "Port Nowhere".
        /// Checking focus directly is the only ordering-independent guard.
        /// </summary>
        public static bool TextInputHasFocus(ICoreClientAPI capi)
        {
            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                foreach (var compo in gui.Composers.Values)
                {
                    if (compo == null || !compo.Enabled) continue;
                    if (compo.CurrentTabIndexElement is GuiElementEditableTextBase) return true;
                }
            }
            return false;
        }

        // Approximate outer width of the composed dialog (button 170 + bg padding 2*4),
        // used to convert the right-edge margin into an absolute left position.
        const double OuterWidthUnscaled = 178;

        double composedForFrameW;
        double composedForScale;

        /// <summary>Re-anchors to the right edge after a window resize or GUI-scale change (absolute positioning doesn't track it automatically).</summary>
        public void RecomposeIfScreenChanged()
        {
            if (capi.Render.FrameWidth == composedForFrameW && RuntimeEnv.GUIScale == composedForScale) return;
            var old = SingleComposer;
            Compose();
            if (old != null) capi.World.RegisterCallback(_ => old.Dispose(), 250);
        }

        void Compose()
        {
            composedForFrameW = capi.Render.FrameWidth;
            composedForScale = RuntimeEnv.GUIScale;

            // CRITICAL compat: the alignment must NOT be RightTop. Vanilla's coordinate overlay
            // re-stacks itself below the first other RightTop-aligned composer every 250ms
            // (HudElementCoordinates.Every250ms -> GetDialogBoundsInArea(RightTop), which matches
            // purely on Bounds.Alignment) — a RightTop button and the overlay end up chasing each
            // other around the corner forever, at any GUI scale. Absolute positioning keeps this
            // dialog invisible to that stacking system; the trade-off is re-anchoring manually on
            // resize/scale changes (RecomposeIfScreenChanged).
            double screenWUnscaled = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(screenWUnscaled - rightMargin - OuterWidthUnscaled, yOffset);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            SingleComposer = capi.Gui
                .CreateCompo("pinmatrix-mapbutton", dialogBounds)
                .AddShadedDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds)
                .AddSmallButton(config.MapButtonShortcutKey ? "Pin Matrix Editor (P)" : "Pin Matrix Editor", () => { onClick?.Invoke(); return true; }, ElementBounds.Fixed(0, 0, 170, 30))
                .EndChildElements()
                .Compose();
        }
    }
}
