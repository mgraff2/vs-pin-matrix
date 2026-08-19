using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace PinMatrix
{
    /// <summary>One end-to-end translocator hop, in absolute world coordinates.</summary>
    public class TlPath
    {
        public Vec3d From;
        public Vec3d To;
        /// <summary>True while this hop is inside the "recently used" window and draws highlighted.</summary>
        public bool Recent;
    }

    /// <summary>
    /// Records translocator hops the player has actually made, and marks both ends.
    ///
    /// The design decision that makes this cheap: **the waypoint title is the storage**. Each end
    /// gets a waypoint named after the coordinates of the other end, so the path is fully described
    /// by the two waypoints and nothing else has to be persisted at all.
    ///
    /// That has a consequence worth stating plainly, because it is the whole reason to do it this
    /// way: waypoints live server-side, per player. So paths recorded on one machine appear on
    /// every other machine you log in from, with no files, no sync folder and no import step — and
    /// a player without this mod still sees a waypoint that says in plain text where the pad goes.
    ///
    /// Only hops you have travelled are recorded. Nothing is drawn for a translocator you merely
    /// walked past, which is the entire point of the feature.
    /// </summary>
    public class TranslocatorPaths
    {
        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;
        readonly WaypointService svc;

        /// <summary>An add or edit we have sent but not yet seen come back from the server.</summary>
        class InFlight
        {
            public Vec3d Pos;
            public long At;
        }

        Vec3d lastPos;

        /// <summary>
        /// Ends with a command in flight, so the next tick does not send a second one before the
        /// server has answered.
        ///
        /// Time-limited, and that matters: an earlier version kept these for the whole session,
        /// which quietly made a hop un-markable for the rest of the session once it had been walked
        /// — delete the marker, walk it again, and nothing happened, with no message explaining why.
        /// A few seconds is all the round-trip needs.
        /// </summary>
        readonly List<InFlight> pendingEnds = new List<InFlight>();

        const long InFlightMs = 5000;

        /// <summary>
        /// A jump we have seen but not yet acted on, and how many ticks we have waited since.
        ///
        /// Arrival is sampled a few ticks after the jump rather than on the jump tick itself. A
        /// translocator does not simply move you: it pulls you toward the pad centre and the client
        /// interpolates toward the new position, so the very first post-jump sample can be
        /// somewhere between the two pads or short of the destination. Waiting for it to settle is
        /// the difference between a marker on the pad and a marker in a field.
        /// </summary>
        Vec3d pendingFrom;
        int settleTicks;

        /// <summary>~250ms at the 20ms tick this runs on.</summary>
        const int SettleTicks = 12;

        /// <summary>
        /// When each path was last travelled, keyed the same way the render list is, in client
        /// elapsed milliseconds.
        ///
        /// Kept in memory rather than written into the waypoint: highlighting a recent hop is a
        /// property of *this session's* view of the map, and baking it into the waypoint colour
        /// would mean a server round-trip to set it and another twenty minutes later to put it
        /// back — churning a player's waypoint list for something purely cosmetic.
        /// </summary>
        readonly Dictionary<string, long> lastUsed = new Dictionary<string, long>();

        public int RecordedThisSession { get; private set; }

        public TranslocatorPaths(ICoreClientAPI capi, PinMatrixConfig config, WaypointService svc)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
        }

        // ------------------------------------------------------------------ titles

        /// <summary>
        /// ASCII on purpose. These titles travel through /waypoint addati as a chat command and get
        /// read back by a regex; a decorative arrow buys nothing and adds an encoding question.
        /// </summary>
        public const string TitleMarker = "TL -> ";

        static readonly Regex TitleRx = new Regex(
            @"^TL -> \s*(?<x>-?\d+)\s*,\s*(?<y>-?\d+)\s*,\s*(?<z>-?\d+)\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Coordinates are written spawn-relative on X/Z and absolute on Y — the same form the
        /// vanilla coordinate HUD shows, so the title reads as the number the player already has on
        /// screen rather than one they have to convert.
        /// </summary>
        public string TitleFor(Vec3d otherEnd) =>
            TitleMarker
            + $"{svc.RelX(otherEnd.X):0}, {otherEnd.Y:0}, {svc.RelZ(otherEnd.Z):0}";

        /// <summary>Reads a title back into the absolute position it points at.</summary>
        public bool TryParseTitle(string title, out Vec3d target)
        {
            target = null;
            if (string.IsNullOrEmpty(title)) return false;

            var m = TitleRx.Match(title.Trim());
            if (!m.Success) return false;

            double rx = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            double rz = double.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture);
            target = new Vec3d(svc.AbsX(rx), y, svc.AbsZ(rz));
            return true;
        }

        /// <summary>
        /// Every recorded hop, rebuilt purely from waypoint titles. Both ends of a hop describe the
        /// same line, so pairs are normalised and de-duplicated — and a hop whose far end has been
        /// deleted still draws, because one titled waypoint is a complete description of it.
        /// </summary>
        public List<TlPath> CurrentPaths()
        {
            var paths = new List<TlPath>();
            var seen = new HashSet<string>();

            foreach (var wp in svc.Own)
            {
                if (wp?.Position == null) continue;
                if (!TryParseTitle(wp.Title, out var target)) continue;

                var a = new Vec3d(wp.Position.X, wp.Position.Y, wp.Position.Z);

                // Refuse to draw nonsense regardless of how it got recorded. Guarding only at
                // record time was not enough: a bad marker from an earlier build, or an endpoint
                // that slipped past the recording check, still drew a line to the corner of the
                // world. Validating here means the renderer cannot be made to lie by bad data.
                if (!PlausiblePosition(a) || !PlausiblePosition(target))
                {
                    capi.Logger.Debug("[pinmatrix] skipping implausible TL path: {0} -> {1},{2},{3}",
                        wp.Title, target.X, target.Y, target.Z);
                    continue;
                }

                string key = PairKey(a, target);
                if (!seen.Add(key)) continue;
                paths.Add(new TlPath { From = a, To = target, Recent = IsRecent(key) });
            }
            return paths;
        }

        bool IsRecent(string key)
        {
            double minutes = config.TranslocatorRecentMinutes;
            if (minutes <= 0) return false;
            if (!lastUsed.TryGetValue(key, out long at)) return false;
            return capi.World.ElapsedMilliseconds - at <= (long)(minutes * 60000);
        }

        /// <summary>Marks a hop as just travelled, so it draws in the highlight colour for a while.</summary>
        void TouchRecent(Vec3d a, Vec3d b) => lastUsed[PairKey(a, b)] = capi.World.ElapsedMilliseconds;

        /// <summary>Order-independent key, so A→B and B→A collapse to one line.</summary>
        static string PairKey(Vec3d a, Vec3d b)
        {
            string ka = $"{a.X:0},{a.Y:0},{a.Z:0}";
            string kb = $"{b.X:0},{b.Y:0},{b.Z:0}";
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        // ------------------------------------------------------------------ adoption

        /// <summary>One existing waypoint that could be converted into a Pin Matrix path marker.</summary>
        public class Adoptable
        {
            public string Key;
            public int Index;
            public string OldTitle;
            public string NewTitle;
            public Vec3d MarkerPos;
            public Vec3d Target;
        }

        /// <summary>Any three signed integers in a row, which is how most tools write a coordinate.</summary>
        static readonly Regex LooseCoordsRx = new Regex(
            @"(?<x>-?\d+)\D+(?<y>-?\d+)\D+(?<z>-?\d+)", RegexOptions.Compiled);

        /// <summary>
        /// Existing waypoints that look like another tool's translocator markers, with the title we
        /// would give them.
        ///
        /// Deliberately a *proposal*, not an action. The rule is lenient — a translocator-ish word
        /// in the title, or our icon, plus three numbers that read as a coordinate — and lenient
        /// rules misfire. So this only ever describes what it would do; the caller shows it and asks.
        /// Reading someone's waypoint list wrongly and rewriting it silently is not a trade worth
        /// making for saving one click.
        /// </summary>
        public List<Adoptable> FindAdoptable()
        {
            var found = new List<Adoptable>();
            string icon = WpCommands.SafeIcon(config.TranslocatorMarkerIcon);
            var own = svc.Own;

            for (int i = 0; i < own.Count; i++)
            {
                var wp = own[i];
                if (wp?.Position == null || wp.Title == null) continue;

                // Already ours and already correct in shape — nothing to adopt.
                if (wp.Title.StartsWith(TitleMarker, StringComparison.Ordinal)) continue;

                string lower = wp.Title.ToLowerInvariant();
                bool looksTl = lower.Contains("translocator") || lower.Contains("tl ") || lower.StartsWith("tl")
                            || string.Equals(wp.Icon, icon, StringComparison.OrdinalIgnoreCase);
                if (!looksTl) continue;

                var m = LooseCoordsRx.Match(wp.Title);
                if (!m.Success) continue;

                double rx = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
                double ry = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
                double rz = double.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture);

                var target = new Vec3d(svc.AbsX(rx), ry, svc.AbsZ(rz));
                if (!PlausiblePosition(target)) continue;   // numbers that were not a coordinate

                found.Add(new Adoptable
                {
                    Key = PinKey.KeyOf(wp),
                    Index = i,
                    OldTitle = wp.Title,
                    NewTitle = TitleMarker + $"{rx:0}, {ry:0}, {rz:0}",
                    MarkerPos = new Vec3d(wp.Position.X, wp.Position.Y, wp.Position.Z),
                    Target = target,
                });
            }
            return found;
        }

        /// <summary>The commands that carry out an adoption, for the batch engine to run.</summary>
        public List<string> AdoptCommands(List<Adoptable> items)
        {
            var cmds = new List<string>();
            string icon = WpCommands.SafeIcon(config.TranslocatorMarkerIcon);
            int color = TraderMarkers.ParseHex(config.TranslocatorMarkerColor);

            // Descending index: /waypoint modify is index-based, and although modify does not shift
            // the list the way remove does, keeping the whole codebase on one ordering rule is how
            // this mod avoids the index bugs that plague waypoint editing.
            items.Sort((a, b) => b.Index.CompareTo(a.Index));

            foreach (var item in items)
            {
                int index = svc.ResolveIndex(item.Key);
                if (index < 0) continue;
                cmds.Add(WpCommands.Modify(index, color, icon, config.TranslocatorMarkerPinned, item.NewTitle));
            }
            return cmds;
        }

        public string DescribeAdoptable(Adoptable a) =>
            $"\"{a.OldTitle}\"  at {Rel(a.MarkerPos.X, a.MarkerPos.Y, a.MarkerPos.Z)}"
            + $"  ->  \"{a.NewTitle}\"";

        // ------------------------------------------------------------------ detection

        /// <summary>
        /// Called on the fast tick. Watches for the player jumping further than a step, then checks
        /// what they landed on.
        ///
        /// Arrival is the end we test for a translocator block, not departure: after the hop the
        /// origin chunk may already have been unloaded client-side, so a departure-side test would
        /// fail exactly when the feature is meant to fire. Landing on a translocator after a jump
        /// of tens of blocks is a strong enough signal on its own.
        /// </summary>
        public void Tick()
        {
            if (!config.TranslocatorPathsEnabled) { lastPos = null; pendingFrom = null; return; }

            var entity = capi.World?.Player?.Entity;
            if (entity == null) return;

            // Pos, not ServerPos: the API marks ServerPos obsolete in favour of Pos. Interpolation
            // lag around a teleport is handled by the settle delay below instead, which is the more
            // robust fix anyway — it also covers the pad's pull-in animation, which no choice of
            // position field would have.
            if (entity.Pos == null) return;
            var now = entity.Pos.XYZ;

            // Ignore everything until the world has actually placed us. Early in a join the
            // position can still be origin-ish, and treating that as a departure point is how a
            // path ends up pointing at the north-west corner of the map.
            if (!PlausiblePosition(now)) { lastPos = null; pendingFrom = null; return; }

            if (lastPos == null) { lastPos = now.Clone(); return; }

            // A jump is being settled: wait, then sample where we actually ended up.
            if (pendingFrom != null)
            {
                if (++settleTicks < SettleTicks) return;

                var landedAt = now.Clone();
                var origin = pendingFrom;
                pendingFrom = null;
                settleTicks = 0;
                lastPos.Set(now.X, now.Y, now.Z);

                if (PlausiblePosition(landedAt) && NearTranslocator(landedAt)) RecordHop(origin, landedAt);
                return;
            }

            double minJump = Math.Max(8, config.TranslocatorMinJump);
            if (lastPos.SquareDistanceTo(now) < minJump * minJump)
            {
                lastPos.Set(now.X, now.Y, now.Z);
                return;
            }

            pendingFrom = lastPos.Clone();
            settleTicks = 0;
        }

        /// <summary>
        /// Rejects positions that cannot be a real player location. Spawn is near the middle of a
        /// Vintage Story map, so anything at single- or double-digit absolute coordinates is the
        /// world not being ready rather than somewhere the player stood.
        /// </summary>
        bool PlausiblePosition(Vec3d pos)
        {
            if (pos == null) return false;
            var accessor = capi.World?.BlockAccessor;
            if (accessor == null) return false;

            if (pos.X < 0 || pos.Z < 0 || pos.Y < 0) return false;
            if (pos.X > accessor.MapSizeX || pos.Z > accessor.MapSizeZ || pos.Y > accessor.MapSizeY) return false;

            // Either axis near zero, not both. The previous version required both, so a position
            // like (0, y, 4000) sailed through and drew a line to the western edge of the world.
            // On a normally generated map spawn sits near the middle, so a genuine player position
            // is nowhere near the origin corner; the check is skipped on maps small enough for the
            // corner to be somewhere you could actually stand.
            const int EdgeMargin = 32;
            bool largeMap = accessor.MapSizeX > 4096 && accessor.MapSizeZ > 4096;
            if (largeMap && (pos.X < EdgeMargin || pos.Z < EdgeMargin)) return false;

            return true;
        }

        /// <summary>Any translocator block within a couple of blocks of this position.</summary>
        bool NearTranslocator(Vec3d pos)
        {
            var accessor = capi.World?.BlockAccessor;
            if (accessor == null) return false;

            int cx = (int)Math.Floor(pos.X), cy = (int)Math.Floor(pos.Y), cz = (int)Math.Floor(pos.Z);
            const int R = 3;

            var probe = new BlockPos(0, 0, 0, 0);
            for (int dx = -R; dx <= R; dx++)
            {
                for (int dy = -R; dy <= R; dy++)
                {
                    for (int dz = -R; dz <= R; dz++)
                    {
                        probe.Set(cx + dx, cy + dy, cz + dz);
                        var block = accessor.GetBlock(probe);
                        var path = block?.Code?.Path;
                        if (path != null && path.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        void RecordHop(Vec3d from, Vec3d to)
        {
            // Always freshen the recency clock, even for a hop already on the map — "recently used"
            // is about this trip, not about whether the path was new to us.
            TouchRecent(from, to);

            var a = MarkEnd(from, to);
            var b = MarkEnd(to, from);

            // Always say something. The silent no-op was the worst part of the previous behaviour:
            // walk a hop, see nothing happen, and have no way to tell whether it was already
            // recorded, blocked, or broken.
            if (a == EndResult.InFlight && b == EndResult.InFlight) return;

            if (a == EndResult.Created || b == EndResult.Created) RecordedThisSession++;

            string what =
                (a == EndResult.Created || b == EndResult.Created) ? "recorded" :
                (a == EndResult.Corrected || b == EndResult.Corrected) ? "corrected" :
                "confirmed";

            capi.ShowChatMessage(WpCommands.ChatSafe(
                $"[Pin Matrix] Translocator path {what}: "
                + $"{svc.RelX(from.X):0}, {from.Y:0}, {svc.RelZ(from.Z):0}"
                + $"  to  {svc.RelX(to.X):0}, {to.Y:0}, {svc.RelZ(to.Z):0}"));
        }

        /// <summary>What happened to one end of a hop.</summary>
        public enum EndResult { InFlight, Unchanged, Corrected, Created }

        /// <summary>
        /// Marks one end with the other end's coordinates, or repairs it if it is already marked
        /// with the wrong destination.
        ///
        /// Ours is identified by the **title prefix**, never by the icon. The icon is cosmetic and
        /// defaults to a common vanilla one, so a player may well have their own spiral-marked pin
        /// near a pad — matching on icon would have let us suppress our marker because of their
        /// waypoint, and worse, rewrite the title of a waypoint they made themselves. Title prefix
        /// is the only signal that actually means "this is one of ours".
        /// </summary>
        EndResult MarkEnd(Vec3d here, Vec3d other)
        {
            double r = Math.Max(1, config.TranslocatorDedupeRadius);
            double r2 = r * r;

            PruneInFlight();
            foreach (var p in pendingEnds)
            {
                if (p.Pos.SquareDistanceTo(here) <= r2) return EndResult.InFlight;
            }

            string icon = WpCommands.SafeIcon(config.TranslocatorMarkerIcon);
            string wantTitle = TitleFor(other);
            int color = TraderMarkers.ParseHex(config.TranslocatorMarkerColor);

            foreach (var wp in svc.Own)
            {
                if (wp?.Position == null) continue;
                if (wp.Title == null || !wp.Title.StartsWith(TitleMarker, StringComparison.Ordinal)) continue;
                if (wp.Position.SquareDistanceTo(here) > r2) continue;

                if (string.Equals(wp.Title.Trim(), wantTitle, StringComparison.Ordinal)) return EndResult.Unchanged;

                // One of ours pointing at the wrong place — the stale half of a bad recording.
                // Repairing beats leaving it, and beats making the player hunt it down and delete it.
                int index = svc.ResolveIndex(PinKey.KeyOf(wp));
                if (index < 0) return EndResult.Unchanged;

                pendingEnds.Add(new InFlight { Pos = here.Clone(), At = capi.World.ElapsedMilliseconds });
                capi.SendChatMessage(WpCommands.Modify(index, color, icon, config.TranslocatorMarkerPinned, wantTitle));
                return EndResult.Corrected;
            }

            pendingEnds.Add(new InFlight { Pos = here.Clone(), At = capi.World.ElapsedMilliseconds });
            capi.SendChatMessage(WpCommands.Add(
                icon, here.X, here.Y, here.Z,
                config.TranslocatorMarkerPinned, color, wantTitle));
            return EndResult.Created;
        }

        void PruneInFlight()
        {
            long now = capi.World.ElapsedMilliseconds;
            pendingEnds.RemoveAll(p => now - p.At > InFlightMs);
        }

        public void ClearPending()
        {
            pendingEnds.Clear();
            pendingFrom = null;
            settleTicks = 0;
        }

        /// <summary>
        /// Every path as the renderer sees it: the title, where the waypoint actually is, and where
        /// the title says the far end is — all in the spawn-relative form the coordinate readout
        /// uses. If a line goes somewhere the label does not, this is the command that says which
        /// of the two is wrong.
        /// </summary>
        public string Explain()
        {
            var sb = new System.Text.StringBuilder();
            var paths = CurrentPaths();
            sb.AppendLine($"[Pin Matrix] spawn {SpawnDesc()}, {paths.Count} translocator path(s):");

            foreach (var wp in svc.Own)
            {
                if (wp?.Position == null) continue;
                if (!TryParseTitle(wp.Title, out var target)) continue;
                sb.AppendLine(
                    $"  \"{wp.Title}\"  marker at {Rel(wp.Position.X, wp.Position.Y, wp.Position.Z)}"
                    + $"  ->  line end {Rel(target.X, target.Y, target.Z)}"
                    + $"  [abs {target.X:0}/{target.Z:0}]");
            }
            if (paths.Count == 0) sb.AppendLine("  none — no waypoint title matched \"" + TitleMarker + "x, y, z\"");
            return sb.ToString().TrimEnd();
        }

        string SpawnDesc()
        {
            var sp = svc.SpawnPos;
            return sp == null ? "(unknown)" : $"{sp.X:0}, {sp.Y:0}, {sp.Z:0}";
        }

        string Rel(double x, double y, double z) => $"{svc.RelX(x):0}, {y:0}, {svc.RelZ(z):0}";
    }
}
