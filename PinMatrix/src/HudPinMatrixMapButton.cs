using System;
using Vintagestory.API.Client;
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
        int rightMargin = DefaultRightMargin;
        int yOffset = DefaultYOffset;

        public const int DefaultYOffset = 120;
        public const int DefaultRightMargin = 100;

        public HudPinMatrixMapButton(ICoreClientAPI capi, Action onClick) : base(capi)
        {
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

        // Not focusable, so opt in to keyboard events while shown (the P shortcut below)
        public override bool ShouldReceiveKeyboardEvents() => IsOpened();

        public override void OnKeyDown(KeyEvent args)
        {
            base.OnKeyDown(args);
            if (args.Handled) return;
            if (args.KeyCode != (int)GlKeys.P || args.CtrlPressed || args.AltPressed || args.ShiftPressed) return;

            // Only while the world map itself is the focused dialog — not chat or another input
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            if (mapDlg == null || !mapDlg.IsOpened() || !mapDlg.Focused) return;

            args.Handled = true;
            onClick?.Invoke();
        }

        void Compose()
        {
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightTop)
                .WithFixedAlignmentOffset(-rightMargin, yOffset);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            SingleComposer = capi.Gui
                .CreateCompo("pinmatrix-mapbutton", dialogBounds)
                .AddShadedDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds)
                .AddSmallButton("Pin Matrix Editor (P)", () => { onClick?.Invoke(); return true; }, ElementBounds.Fixed(0, 0, 170, 30))
                .EndChildElements()
                .Compose();
        }
    }
}
