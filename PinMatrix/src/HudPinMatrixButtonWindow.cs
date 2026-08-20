using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>Which map-screen button. Also the order they appear in.</summary>
    /// <summary>
    /// What a map-screen button window can hold.
    ///
    /// Everything from <see cref="PmButton.Options"/> onwards is a <em>layout-mode tool</em>: it only
    /// means anything while the zone grid is on screen, and it lives in the floating tools window
    /// rather than in the permanent button stack. See <see cref="PmButtons.LayoutOnly"/>.
    /// </summary>
    public enum PmButton { Editor, Zones, Options, RescanHuds, ResetLayout, Rescue }

    public static class PmButtons
    {
        /// <summary>
        /// True for the tools that exist only while the zones are showing.
        ///
        /// Keeping this one predicate rather than a list per screen is what stops the tools window
        /// and the thing that fills it disagreeing about what "temporary" means.
        /// </summary>
        public static bool LayoutOnly(PmButton b) => b >= PmButton.Options;
    }

    /// <summary>
    /// A map-screen button window: either the whole set in one window, or a single button on its own.
    ///
    /// One class covers both because the only differences are which buttons it holds and whether
    /// they stack down or across. That keeps the three placement modes from each needing their own
    /// window type:
    ///
    ///   Fixed cell — one window, buttons in a column, snapped to a grid cell
    ///   Tab row    — one window, buttons in a row, stretched across the row it is dropped on
    ///   Floating   — one window per button, each placed wherever you like
    ///
    /// Every one of them is an ordinary window with its own name, so dragging, snapping, saved
    /// positions and the zone grid all work on them with no special case anywhere.
    ///
    /// While layout mode is on, a window grows a title bar so it can be dragged; outside layout mode
    /// the title bar is gone and it is just buttons again. Same dialog, same name, same position
    /// either way — only the chrome changes, so nothing jumps when you leave layout mode.
    /// </summary>
    public class HudPinMatrixButtonWindow : GuiDialog
    {
        readonly PinMatrixConfig config;
        readonly List<PmButton> contents;
        readonly bool horizontal;
        readonly string dialogName;
        readonly Func<PmButton, Action> actionFor;
        readonly Func<bool> zonesVisible;
        readonly Func<bool> layoutMode;

        /// <summary>
        /// When set, the window is this wide and its buttons divide it evenly instead of each
        /// taking a fixed width. That is what makes the tab row span the screen.
        /// </summary>
        double stretchWidth;

        /// <summary>
        /// Whether the zone grid may capture and snap this window when it is dragged.
        ///
        /// False for the floating tools window, and that is the whole point of it: the tools appear
        /// *because* you are arranging windows, so they are the one thing that must never become
        /// another window to arrange. It stays where you drop it, on or off the lattice.
        /// <see cref="HudLayoutOverlay"/> reads this when collecting drag candidates.
        /// </summary>
        public bool Snappable { get; set; } = true;

        /// <summary>
        /// What the drag handle says. "Move" for the button bar, which is all it does; the floating
        /// tools window names itself instead, because it is a window you go looking for rather than
        /// a handle attached to something you can already see.
        /// </summary>
        public string TitleBarText { get; set; } = "Move";

        /// <summary>
        /// What the title bar's X does. Null leaves it dead — GuiElementDialogTitleBar draws the
        /// button whether or not anything is listening (it ends in a plain <c>OnClose?.Invoke()</c>),
        /// so a window with a bar and no handler shows a close button that silently does nothing.
        ///
        /// These bars only exist while the zones are up, so the only honest meaning for closing one
        /// is "I have finished arranging" — which is why every one of them hides the zones.
        /// </summary>
        public Action OnTitleBarClose;

        double posX, posY;
        double composedForFrameW;
        double composedForScale;
        bool composedWithTitleBar;
        int composedButtonCount;

        const double ButtonW = 170;
        const double ButtonH = 30;
        const double Gap = 4;
        const double OuterWidthUnscaled = 178;

        public HudPinMatrixButtonWindow(ICoreClientAPI capi, PinMatrixConfig config, string dialogName,
                                        IEnumerable<PmButton> contents, bool horizontal,
                                        Func<PmButton, Action> actionFor,
                                        Func<bool> zonesVisible, Func<bool> layoutMode) : base(capi)
        {
            this.config = config;
            this.dialogName = dialogName;
            this.contents = new List<PmButton>(contents);
            this.horizontal = horizontal;
            this.actionFor = actionFor;
            this.zonesVisible = zonesVisible;
            this.layoutMode = layoutMode;

            ResetToDefaultPosition();
            Compose();
        }

        /// <summary>Stable name: the key for vanilla's saved positions and for our zone assignments.</summary>
        public string DialogName => dialogName;

        /// <summary>The position we wrote purely to unlock the title bar, if we wrote one.</summary>
        Vec2i seeded;

        /// <summary>
        /// Invoked when we clear a seeded position, so the removal reaches clientsettings.json.
        ///
        /// It has to: SetDialogPosition only touches an in-memory dictionary, and the layout manager
        /// flushes that dictionary whole whenever it stores positions for other windows — which
        /// happens the moment the zones are shown. So a seed can reach disk, and a seed read back
        /// from disk next session is indistinguishable from a position the player chose. Flushing
        /// the removal is what stops one appearing as a placement later.
        /// </summary>
        public Action PositionsChanged;

        /// <summary>
        /// The position the player themselves put this window at, if any.
        ///
        /// Not the same question as "does vanilla have a stored position", which is what the
        /// placement rules used to ask: since layout mode seeds one to make the title bar draggable
        /// (see <see cref="SyncTitleBarMovable"/>), a stored position no longer implies a deliberate
        /// one. Asking the wrong question would freeze the buttons wherever they happened to be the
        /// first time the grid was shown, and make the configured Cell stop taking effect.
        /// </summary>
        public bool TryGetPlayerPosition(out int x, out int y)
        {
            x = y = 0;
            var stored = capi.Gui.GetDialogPosition(dialogName);
            if (stored == null) return false;
            if (seeded != null && stored.X == seeded.X && stored.Y == seeded.Y) return false;
            x = stored.X;
            y = stored.Y;
            return true;
        }

        public bool PlayerPlaced => TryGetPlayerPosition(out _, out _);

        /// <summary>
        /// Makes the "Move" title bar actually draggable — and takes it back when the bar goes away.
        ///
        /// A title bar is only movable when vanilla has a stored position under its dialog name:
        /// GuiElementDialogTitleBar reads GetDialogPosition once, on compose, and stays "Fixed" if
        /// it finds nothing. Other mods' windows get that from LayoutManager.MakeOpenDialogsMovable,
        /// which skips ours twice over — they are HUDs, and they are in OwnDialogs — so without this
        /// our own buttons grow a drag handle that does nothing whatsoever, until something else
        /// happens to store a position for them. That is exactly why some of the floating buttons
        /// were draggable and others were not.
        ///
        /// Seeding the position the window already occupies moves nothing. Dropping it again when
        /// the bar goes away is safe here in a way it is not for other mods' windows: we re-place
        /// ourselves from posX/posY on every compose and never rely on vanilla restoring us, so
        /// clearing it cannot make us jump. A position the player actually dragged us to is left
        /// alone — that one is theirs, and it is what makes the drag survive a restart.
        /// </summary>
        void SyncTitleBarMovable(bool wantMovable)
        {
            var stored = capi.Gui.GetDialogPosition(dialogName);

            if (wantMovable)
            {
                if (stored != null) return;
                seeded = new Vec2i((int)Math.Round(posX), (int)Math.Round(posY));
                capi.Gui.SetDialogPosition(dialogName, seeded);
                return;
            }

            if (seeded != null && stored != null && stored.X == seeded.X && stored.Y == seeded.Y)
            {
                capi.Gui.SetDialogPosition(dialogName, null);
                PositionsChanged?.Invoke();
            }
            seeded = null;
        }

        public bool Holds(PmButton b) => contents.Contains(b);

        /// <summary>The rectangle this window currently occupies, in unscaled units.</summary>
        public URect OuterRect
        {
            get
            {
                int count = Math.Max(1, VisibleButtons().Count);
                double w = (horizontal ? (stretchWidth > 0 ? stretchWidth : count * (ButtonW + Gap)) : ButtonW) + 8;
                double h = (horizontal ? ButtonH : count * (ButtonH + Gap)) + 8
                         + ((layoutMode != null && layoutMode()) ? 30 : 0);
                return new URect(posX, posY, w, h);
            }
        }

        /// <summary>True when this window has nothing to show and should not be composed or opened.</summary>
        public bool IsEmpty => VisibleButtons().Count == 0;

        /// <summary>Stretches the window to a width, dividing it evenly between its buttons.</summary>
        public void SetStretchWidth(double unscaledWidth)
        {
            if (Math.Abs(stretchWidth - unscaledWidth) < 0.5) return;
            stretchWidth = unscaledWidth;
            Recompose();
        }

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;

        // Above the world map (0.11) and above the zone overlay (0.15). These are the only fixed
        // furniture on the map screen, so the grid must never paint over them.
        public override double DrawOrder => 0.97;

        // The title bar does its own dragging, so mouse events have to reach us while it exists.
        public override bool ShouldReceiveMouseEvents() => IsOpened();

        // ------------------------------------------------------------------ placement

        /// <summary>The right-edge position used on a fresh install and after a layout reset.</summary>
        public void ResetToDefaultPosition()
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;
            double screenW = capi.Render.FrameWidth / scale;

            // Single-button windows stack down the edge so a fresh install does not pile all three
            // on top of each other.
            int slot = contents.Count == 1 ? (int)contents[0] : 0;
            posX = screenW - 100 - OuterWidthUnscaled;
            posY = 120 + slot * (ButtonH + Gap + 8);
            ClampOnScreen(ref posX, ref posY);
        }

        /// <summary>
        /// Points vanilla's stored position at where <em>we</em> are about to put this window.
        ///
        /// THIS IS WHAT MAKES THE TAB ROW FOLLOW A DROP. A movable title bar reads GetDialogPosition
        /// on every compose and restores the window to it (the same rule that makes other mods'
        /// windows come back where you left them). Dragging the bar writes the dragged position into
        /// that store — so when we then placed the bar on its new strip, the very recompose that move
        /// triggered handed the title bar the *dragged* position and it went straight back. The row
        /// assignment had updated correctly all along; the window just could not stay where we put
        /// it. Hiding the zones removed the title bar, nothing restored anything, and it finally
        /// jumped to the strip — which is exactly the "it only snaps after I hide zones" symptom.
        ///
        /// Writing our position in as the *seed* rather than as a plain stored position matters:
        /// TryGetPlayerPosition compares the two, so a seeded value is still correctly not mistaken
        /// for somewhere the player chose.
        /// </summary>
        public void PinPositionForTitleBar(double unscaledX, double unscaledY)
        {
            ClampOnScreen(ref unscaledX, ref unscaledY);
            var pos = new Vec2i((int)Math.Round(unscaledX), (int)Math.Round(unscaledY));

            var stored = capi.Gui.GetDialogPosition(dialogName);
            if (stored != null && stored.X == pos.X && stored.Y == pos.Y &&
                seeded != null && seeded.X == pos.X && seeded.Y == pos.Y) return;

            seeded = pos;
            capi.Gui.SetDialogPosition(dialogName, pos);
            PositionsChanged?.Invoke();
        }

        public void SetPosition(double unscaledX, double unscaledY)
        {
            ClampOnScreen(ref unscaledX, ref unscaledY);
            if (Math.Abs(posX - unscaledX) < 0.5 && Math.Abs(posY - unscaledY) < 0.5) return;
            posX = unscaledX;
            posY = unscaledY;
            Recompose();
        }

        /// <summary>
        /// Keeps the window reachable whatever it was asked to do.
        ///
        /// Without this a horizontal button strip placed near the right edge simply runs off the
        /// screen and the mod looks like it failed to load — which is exactly what happened. A
        /// window the player cannot see is indistinguishable from a window that is not there, so
        /// nothing is allowed to put one out of reach.
        /// </summary>
        void ClampOnScreen(ref double x, ref double y)
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) scale = 1;

            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            int count = Math.Max(1, VisibleButtons().Count);
            double w = (horizontal ? (stretchWidth > 0 ? stretchWidth : count * (ButtonW + Gap)) : ButtonW) + 8;
            double h = (horizontal ? ButtonH : count * (ButtonH + Gap)) + 8
                     + ((layoutMode != null && layoutMode()) ? 30 : 0);

            x = Math.Max(0, Math.Min(x, screenW - w));
            y = Math.Max(0, Math.Min(y, screenH - h));
        }

        /// <summary>Re-anchors after a resize or scale change, and re-chromes when layout mode flips.</summary>
        public void RefreshIfNeeded()
        {
            bool wantTitleBar = layoutMode != null && layoutMode();
            if (capi.Render.FrameWidth == composedForFrameW
                && RuntimeEnv.GUIScale == composedForScale
                && wantTitleBar == composedWithTitleBar
                && VisibleButtons().Count == composedButtonCount)
            {
                return;
            }
            Recompose();
        }

        void Recompose()
        {
            // When the window is about to be emptied, Compose disposes the old composer itself via
            // ClearComposers — so do not also queue a deferred dispose for it, or the same composer
            // gets disposed twice.
            var old = IsEmpty ? null : SingleComposer;
            Compose();
            if (old != null) capi.World.RegisterCallback(_ => old.Dispose(), 250);
        }

        /// <summary>
        /// Layout Options only exists while the zones are showing — it configures what you can see.
        /// The Zones button itself only exists while window layout is switched on (editor > Map
        /// options): the feature is opt-in, and a button for a feature you never asked for is
        /// clutter on every map open. Options cannot outlive Zones — zones can only be pinned while
        /// layout is on — so the one gate covers both.
        /// </summary>
        List<PmButton> VisibleButtons()
        {
            var list = new List<PmButton>();
            bool zonesOn = zonesVisible != null && zonesVisible();
            foreach (var b in contents)
            {
                if (b == PmButton.Zones && !config.LayoutEnabled) continue;
                if (PmButtons.LayoutOnly(b) && !zonesOn) continue;
                list.Add(b);
            }
            return list;
        }

        string Label(PmButton b)
        {
            switch (b)
            {
                case PmButton.Editor:
                    return config.MapButtonShortcutKey ? "Pin Matrix Editor (P)" : "Pin Matrix Editor";
                case PmButton.Zones:
                    string t = (zonesVisible != null && zonesVisible()) ? "Hide Zones" : "Layout Zones";
                    return config.MapLayoutShortcutKey ? t + " (Z)" : t;
                case PmButton.RescanHuds:
                    return "Rescan HUDs";
                case PmButton.ResetLayout:
                    return "Reset layout";
                case PmButton.Rescue:
                    return "Rescue off-screen";
                default:
                    return "Layout Options";
            }
        }

        void Compose()
        {
            composedForFrameW = capi.Render.FrameWidth;
            composedForScale = RuntimeEnv.GUIScale;
            composedWithTitleBar = layoutMode != null && layoutMode();

            var visible = VisibleButtons();
            composedButtonCount = visible.Count;

            // Before the composer is built: the title bar reads its movable state while composing,
            // so a position seeded afterwards would not take effect until the compose after that.
            SyncTitleBarMovable(composedWithTitleBar && visible.Count > 0);

            // A window with nothing in it cannot be composed at all: the background sizes itself to
            // its children, and GuiElementDialogBackground throws outright when there are none.
            // This is reachable in normal use — the Options-only window empties the moment the zones
            // are hidden — so it is a guard, not a formality.
            //
            // ClearComposers, never "SingleComposer = null": the DlgComposers indexer dereferences
            // the value it is handed, so assigning null throws a NullReferenceException of its own.
            // The first version of this guard did exactly that and simply moved the crash.
            if (visible.Count == 0)
            {
                ClearComposers();
                return;
            }

            // CRITICAL compat: the alignment must NOT be RightTop. Vanilla's coordinate overlay
            // re-stacks itself below the first other RightTop-aligned composer every 250ms
            // (HudElementCoordinates.Every250ms -> GetDialogBoundsInArea(RightTop), matching purely
            // on Bounds.Alignment) - a RightTop window and the overlay chase each other around the
            // corner forever, at any GUI scale. Absolute positioning keeps this dialog invisible to
            // that stacking system; the trade-off is re-anchoring manually (RefreshIfNeeded).
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(posX, posY);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui.CreateCompo(dialogName, dialogBounds)
                .AddShadedDialogBG(bgBounds, false);

            // A title bar is what makes a window draggable in this game, so it appears exactly when
            // dragging is the point and gets out of the way the rest of the time.
            if (composedWithTitleBar) composer.AddDialogTitleBar(TitleBarText, OnTitleBarClose);

            double top = composedWithTitleBar ? 30 : 0;
            double buttonW = ButtonW;
            if (horizontal && stretchWidth > 0)
            {
                buttonW = Math.Max(60, (stretchWidth - 8 - Gap * (visible.Count - 1)) / visible.Count);
            }

            composer.BeginChildElements(bgBounds);

            for (int i = 0; i < visible.Count; i++)
            {
                var b = visible[i];
                var action = actionFor?.Invoke(b);
                var bounds = horizontal
                    ? ElementBounds.Fixed(i * (buttonW + Gap), top, buttonW, ButtonH)
                    : ElementBounds.Fixed(0, top + i * (ButtonH + Gap), buttonW, ButtonH);
                composer.AddSmallButton(Label(b), () => { action?.Invoke(); return true; }, bounds);
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        // ------------------------------------------------------------------ shortcuts

        // Not focusable, so opt in to keyboard events while shown. A window only listens for the
        // shortcuts of the buttons it actually holds, so two windows can never both claim a key.
        // Z follows its button: with layout switched off the button is hidden, and a shortcut for
        // an invisible button would summon a mystery grid.
        public override bool ShouldReceiveKeyboardEvents() =>
            IsOpened() && ((Holds(PmButton.Editor) && config.MapButtonShortcutKey)
                        || (Holds(PmButton.Zones) && config.MapLayoutShortcutKey && config.LayoutEnabled));

        public override void OnKeyDown(KeyEvent args)
        {
            base.OnKeyDown(args);
            if (args.Handled) return;
            if (args.CtrlPressed || args.AltPressed || args.ShiftPressed) return;

            PmButton? target = null;
            if (Holds(PmButton.Editor) && config.MapButtonShortcutKey && args.KeyCode == (int)GlKeys.P) target = PmButton.Editor;
            else if (Holds(PmButton.Zones) && config.MapLayoutShortcutKey && config.LayoutEnabled && args.KeyCode == (int)GlKeys.Z) target = PmButton.Zones;
            if (target == null) return;

            // Only while the world map itself is the focused dialog — not chat or another input
            var mapDlg = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
            if (mapDlg == null || !mapDlg.IsOpened() || !mapDlg.Focused) return;

            // ...and not while the player is typing into any text field (see TextInputHasFocus)
            if (TextInputHasFocus(capi)) return;

            args.Handled = true;
            actionFor?.Invoke(target.Value)?.Invoke();
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
        /// OpenedGuis in descending DrawOrder, and these windows sit at 0.97 versus the map dialog's
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
    }
}
