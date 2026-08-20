using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// Drops a waypoint on a Herty cup the moment the player places one or collects from one.
    ///
    /// WHY THIS IS TRIGGERED BY INTERACTION AND NOT BY A SCAN. The obvious design — sweep the
    /// loaded chunks for cup blocks and mark them — cannot tell your cups from anyone else's, and
    /// on a server it would carpet the map with other people's taps. There is no way to fix that
    /// from a client: the client's only block signal is <c>IClientEventAPI.BlockChanged</c>, which
    /// carries a position and the old block and *no player at all*, and nothing in the cup's own
    /// synced data records who placed it (BEHertyCup stores spiles, pot size, rates — no owner).
    ///
    /// Triggering on the player's own interaction sidesteps the question rather than answering it.
    /// Placing a cup and collecting from one are both things *this client did*, so a marker made
    /// from either is yours by construction — no ownership field, no heuristic about who did what,
    /// and no server-side support needed. It also picks up cups you did not place but have started
    /// using, which is the honest definition of "a cup I care about".
    ///
    /// NAMED CROSS-MOD BEHAVIOUR, BUT NO MOD CHECK. This is the first thing in Pin Matrix that
    /// knows another mod's content by name, and the invariant in CLAUDE.md still holds: there is no
    /// <c>api.ModLoader.IsModEnabled("hertycups")</c> branch here. The condition that actually
    /// matters is whether a Herty cup block exists in the world, so that is what is tested. Without
    /// Herty Cups installed no such block can ever appear, every hook falls through on its first
    /// comparison, and the feature is inert without knowing or caring why.
    ///
    /// Off by default, like the trader and translocator markers, and for the same reason: writing
    /// waypoints onto someone's map without being asked is not a default.
    /// </summary>
    public class HertyCupMarkers
    {
        /// <summary>Mod domain and block-path prefix of a Herty cup, i.e. "hertycups:hertycup-north".</summary>
        public const string CupDomain = "hertycups";
        public const string CupPath = "hertycup";

        /// <summary>
        /// How long after a right-click a cup may appear and still be counted as that click's doing.
        ///
        /// The click and the block are two different events: the client sends the placement, the
        /// server decides, and the block arrives back on a later frame. A second and a half covers
        /// a bad connection without being long enough to catch an unrelated cup — for a false
        /// positive, another player would have to place a cup within two blocks of what you were
        /// pointing at, inside the same second and a half.
        /// </summary>
        const long PlaceWindowMs = 1500;

        /// <summary>How far from the aimed-at block a newly-appeared cup may be and still be ours.</summary>
        const double PlaceRadius = 2.0;

        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;
        readonly WaypointService svc;

        /// <summary>
        /// Positions we have already sent an add for this session. The server round-trip takes a
        /// moment, so without this a second interaction with the same cup would add a duplicate.
        /// </summary>
        readonly List<Vec3d> pending = new List<Vec3d>();

        BlockPos aimedAt;
        long aimedAtMs;

        public int MarkedThisSession { get; private set; }

        public HertyCupMarkers(ICoreClientAPI capi, PinMatrixConfig config, WaypointService svc)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
        }

        public void Hook()
        {
            capi.Input.InWorldAction += OnInWorldAction;
            capi.Event.BlockChanged += OnBlockChanged;
        }

        public void Unhook()
        {
            capi.Input.InWorldAction -= OnInWorldAction;
            capi.Event.BlockChanged -= OnBlockChanged;
        }

        /// <summary>Forgets the in-flight adds, e.g. after the player deletes markers and wants them back.</summary>
        public void ClearPending() => pending.Clear();

        // ------------------------------------------------------------------ the two triggers

        /// <summary>
        /// Every in-world right-click, which is both triggers at once.
        ///
        /// Pointing at a cup is a collection (or a spile change, or a look inside — all of them mean
        /// this cup matters to you), and is marked immediately. Pointing at anything else *might* be
        /// a placement, so the aim is remembered and <see cref="OnBlockChanged"/> decides once the
        /// world says whether a cup actually appeared.
        ///
        /// <c>handled</c> is never touched. This is an observer: swallowing a right-click here would
        /// stop the player collecting the resin they just clicked for.
        /// </summary>
        void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            // BOTH enum values, and the first one is the one that matters. Decompiled 1.22.7:
            // SystemMouseInWorldInteractions raises EnumEntityAction.InWorldRightMouseDown for a
            // right-click on a block, while EntityControls routes its own actions - including plain
            // RightMouseDown - through the same event (SystemPlayerControl sets
            // Controls.OnAction = TriggerInWorldAction). Testing only the plain one, which is the
            // obvious guess, means this never fires at all. Accepting both is harmless: two events
            // for one click set the same aim twice, and a mark is deduped either way.
            if (!on) return;
            if (action != EnumEntityAction.InWorldRightMouseDown &&
                action != EnumEntityAction.RightMouseDown) return;
            if (!config.HertyCupMarkersEnabled) return;

            var sel = capi.World?.Player?.CurrentBlockSelection;
            if (sel?.Position == null) return;

            if (IsCup(capi.World.BlockAccessor.GetBlock(sel.Position)))
            {
                Mark(sel.Position);
                return;
            }

            // No test on what is in hand, deliberately. The confirmation is the cup itself
            // appearing, which is a far stronger signal than an item code — and it does not need
            // this mod to know what a Herty cup is *held* as, only what it is once placed.
            aimedAt = sel.Position.Copy();
            aimedAtMs = capi.World.ElapsedMilliseconds;
        }

        /// <summary>A block changed: if it is the cup the player was just placing, mark it.</summary>
        void OnBlockChanged(BlockPos pos, Block oldBlock)
        {
            if (!config.HertyCupMarkersEnabled || aimedAt == null || pos == null) return;

            if (capi.World.ElapsedMilliseconds - aimedAtMs > PlaceWindowMs)
            {
                aimedAt = null;
                return;
            }

            if (pos.DistanceTo(aimedAt) > PlaceRadius) return;
            if (IsCup(oldBlock)) return;
            if (!IsCup(capi.World.BlockAccessor.GetBlock(pos))) return;

            aimedAt = null;
            Mark(pos);
        }

        // ------------------------------------------------------------------ marking

        public static bool IsCup(Block block)
        {
            var code = block?.Code;
            return code != null
                && string.Equals(code.Domain, CupDomain, StringComparison.Ordinal)
                && code.Path.StartsWith(CupPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// The wood the cup is tapping, read straight out of the block codes rather than from Herty
        /// Cups' own types — this mod does not reference that assembly and must not start.
        /// <c>BlockHertyCup.LogFacing</c> is <c>BlockFacing.FromCode(Variant["side"])</c>, which is
        /// public block data, so the tapped log is one step that way and its own <c>wood</c> variant
        /// names the tree. Null when anything in that chain is missing, and the title simply goes
        /// without — a marker that says less is much better than one that guesses.
        /// </summary>
        string WoodAt(BlockPos cupPos)
        {
            var cup = capi.World.BlockAccessor.GetBlock(cupPos);
            string side = cup?.Variant?["side"];
            if (side == null) return null;

            var facing = BlockFacing.FromCode(side);
            if (facing == null) return null;

            var log = capi.World.BlockAccessor.GetBlock(cupPos.AddCopy(facing));
            return log?.Variant?["wood"];
        }

        void Mark(BlockPos pos)
        {
            if (svc.Layer == null) return;

            // Block centre, so the marker sits on the cup rather than on its lower north-west corner.
            var at = new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5);
            if (AlreadyMarked(at)) return;

            pending.Add(at);

            // With no wood to name, the prefix's trailing separator is dropped rather than left
            // dangling — "Herty cup:" is a marker that looks like it failed halfway.
            string wood = WoodAt(pos);
            string title = (wood == null
                ? (config.HertyCupMarkerTitlePrefix ?? "").TrimEnd(' ', ':', '-', ',')
                : config.HertyCupMarkerTitlePrefix + Capitalize(wood)).Trim();
            if (title.Length == 0) title = "Herty cup";

            capi.SendChatMessage(WpCommands.Add(
                WpCommands.SafeIcon(config.HertyCupMarkerIcon),
                at.X, at.Y, at.Z,
                config.HertyCupMarkerPinned,
                TraderMarkers.ParseHex(config.HertyCupMarkerColor),
                title));

            MarkedThisSession++;
            capi.ShowChatMessage(WpCommands.ChatSafe(
                $"[Pin Matrix] Marked {title} at {at.X:0}, {at.Y:0}, {at.Z:0}"));
        }

        /// <summary>
        /// True when this cup already has a marker.
        ///
        /// Positional and tight, unlike the trader test: a cup does not move, and two cups on
        /// neighbouring faces of the same trunk are two real cups a block apart that both deserve a
        /// pin. Only our own icon counts, so a hand-placed pin beside a cup cannot silently
        /// suppress it.
        /// </summary>
        bool AlreadyMarked(Vec3d at)
        {
            double r = Math.Max(0, config.HertyCupDedupeRadius);
            double r2 = r * r;

            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].SquareDistanceTo(at) <= r2) return true;
            }

            string icon = WpCommands.SafeIcon(config.HertyCupMarkerIcon);
            foreach (var wp in svc.Own)
            {
                if (wp?.Position == null) continue;
                if (!string.Equals(wp.Icon, icon, StringComparison.OrdinalIgnoreCase)) continue;
                if (wp.Position.SquareDistanceTo(at) <= r2) return true;
            }
            return false;
        }

        static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
