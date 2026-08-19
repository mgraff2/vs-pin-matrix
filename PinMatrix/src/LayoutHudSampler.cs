using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace PinMatrix
{
    /// <summary>
    /// Watches HUD rectangles so the grid can refuse to place a window on top of one.
    ///
    /// Sample-and-hold, deliberately. HUD rects are not stationary: vanilla's coordinate readout
    /// rewrites its text every 250ms, so it resizes as the player walks (digit counts roll over,
    /// "North" becomes "Northeast"), and it re-stacks its own Y below whatever else is RightTop-
    /// aligned. Recomputing blocked cells straight off a live scan would make cells flicker
    /// between available and blocked while the player is merely walking — the same oscillation
    /// the map button's hysteresis exists to avoid (see PositionMapButton).
    ///
    /// So: sample continuously and cheaply, but only <see cref="Commit"/> the result at moments
    /// that matter (opening the overlay, a resolution/scale change, an explicit rescan). A rect
    /// only counts once it has held still for <see cref="RequiredStableTicks"/> samples, which
    /// also disqualifies the transient HUDs by construction — the cursor-followers (drop-item,
    /// mouse tools) and the centre-screen toasts (ingame error, discovery) never settle.
    ///
    /// Nothing here knows a mod name. HUD-ness is <see cref="EnumDialogType.HUD"/>, a base-class
    /// property of every mod's HUD, and the rect comes from the composer every mod already has.
    /// An earlier draft filtered on Bounds.Alignment being a corner constant; that was wrong,
    /// because a HUD may legitimately be absolutely positioned with EnumDialogArea.None — our own
    /// map button is exactly that, and any mod making the same choice would have gone undetected.
    /// </summary>
    public class LayoutHudSampler
    {
        class Entry
        {
            public URect Rect;
            public int StableTicks;
            public bool SeenThisPass;
        }

        /// <summary>~2s at the 250ms layout tick.</summary>
        public int RequiredStableTicks = 8;

        /// <summary>Unscaled units a rect may drift and still count as "held still".</summary>
        const double MoveTolerance = 2.0;

        /// <summary>A composer covering more than this share of the screen is a backdrop, not a HUD.</summary>
        const double MaxScreenShare = 0.5;

        /// <summary>
        /// Minimum unscaled size, in both directions, before a composer counts as something you
        /// could collide with.
        ///
        /// This is not tidiness — it is the fix for phantom blocked cells. Vanilla keeps several
        /// HUDs permanently open that render nothing at all: HudIngameError and HudIngameDiscovery
        /// both override TryClose() to return false, and each parks a 600x5 composer at screen
        /// centre that only draws when a message fires. They are open, they are perfectly stable,
        /// and so they sail straight through the dwell filter and veto cells with nothing visible
        /// in them. A sliver a few units tall is not an obstacle; requiring real extent in both
        /// directions rejects that whole class without naming anything.
        /// </summary>
        const double MinObstacleSize = 16.0;

        readonly Dictionary<string, Entry> seen = new Dictionary<string, Entry>();
        readonly List<HudRect> committed = new List<HudRect>();

        /// <summary>A committed obstacle, with the composer it came from so diagnostics can name it.</summary>
        public class HudRect
        {
            public string Key;
            public URect Rect;
        }

        /// <summary>The rects that were stable as of the last <see cref="Commit"/>.</summary>
        public IReadOnlyList<HudRect> Rects => committed;

        public int StableCount
        {
            get
            {
                int n = 0;
                foreach (var e in seen.Values) if (e.StableTicks >= RequiredStableTicks) n++;
                return n;
            }
        }

        public void Clear()
        {
            seen.Clear();
            committed.Clear();
        }

        /// <summary>One observation pass. Cheap enough for the 250ms tick.</summary>
        public void Sample(ICoreClientAPI capi, ICollection<GuiDialog> ours)
        {
            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0) return;

            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;
            double screenArea = screenW * screenH;

            foreach (var e in seen.Values) e.SeenThisPass = false;

            foreach (var gui in capi.Gui.OpenedGuis)
            {
                if (gui == null || !gui.IsOpened()) continue;
                if (gui.DialogType != EnumDialogType.HUD) continue;
                if (ours != null && ours.Contains(gui)) continue;

                foreach (var pair in gui.Composers)
                {
                    var compo = pair.Value;
                    var b = compo?.Bounds;
                    if (compo == null || !compo.Enabled || b == null) continue;

                    var rect = new URect(b.absX / scale, b.absY / scale,
                                         b.OuterWidth / scale, b.OuterHeight / scale);
                    if (rect.W < MinObstacleSize || rect.H < MinObstacleSize) continue;
                    if (rect.Area > MaxScreenShare * screenArea) continue;

                    string key = (compo.DialogName ?? gui.GetType().Name) + "/" + pair.Key;
                    if (!seen.TryGetValue(key, out var entry))
                    {
                        seen[key] = new Entry { Rect = rect, StableTicks = 1, SeenThisPass = true };
                        continue;
                    }

                    // Held still? Only then does it age towards counting.
                    entry.StableTicks = rect.Approximately(entry.Rect, MoveTolerance) ? entry.StableTicks + 1 : 1;
                    entry.Rect = rect;
                    entry.SeenThisPass = true;
                }
            }

            // A HUD that closed stops being an obstacle immediately; no point ageing a ghost.
            List<string> gone = null;
            foreach (var pair in seen)
            {
                if (!pair.Value.SeenThisPass) (gone ?? (gone = new List<string>())).Add(pair.Key);
            }
            if (gone != null) foreach (var k in gone) seen.Remove(k);
        }

        /// <summary>Freezes the currently-stable rects as the obstacle set the grid will use.</summary>
        public void Commit()
        {
            committed.Clear();
            foreach (var pair in seen)
            {
                if (pair.Value.StableTicks >= RequiredStableTicks)
                {
                    committed.Add(new HudRect { Key = pair.Key, Rect = pair.Value.Rect });
                }
            }
        }

        /// <summary>
        /// Fraction of a cell covered by committed HUD rects, clamped to 1. Overlapping HUDs are
        /// double-counted, which can only ever push a cell over the threshold sooner — acceptable,
        /// since the answer in that case is "a HUD is here" either way.
        /// </summary>
        public double CoverageOf(URect cell)
        {
            if (cell.Area <= 0) return 0;
            double covered = 0;
            for (int i = 0; i < committed.Count; i++) covered += committed[i].Rect.OverlapArea(cell);
            double ratio = covered / cell.Area;
            return ratio > 1 ? 1 : ratio;
        }
    }
}
