using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// Drops a waypoint on every trader the player walks past, coloured by trade specialisation.
    ///
    /// This is the one kind of auto-marker that costs nothing to support, because what it produces
    /// is *ordinary waypoints*. They sort, filter, recolour, hide, bulk-rename, land in the recycle
    /// bin and export like anything else in the matrix — no parallel marker system, no second thing
    /// to maintain, and nothing left behind if the feature is switched off later.
    ///
    /// Off by default. Writing waypoints onto someone's map without being asked is not a default.
    ///
    /// WHERE A TRADER IS. <c>Entity.Pos</c> on the client is the *rendered* position, and the
    /// interpolation behaviour every trader carries smooths it towards each server update from
    /// wherever it last was — which, for an entity the client has only just received, is the world
    /// origin. For the first frames after a trader's chunk loads, <c>Pos</c> is therefore the real
    /// position scaled by some fraction (measured in the field: 0.62 and 0.93 of a trader at
    /// 512330, 138, 514078 gave marks at 318042, 86, 319127 and 478562, 129, 480194 — hundreds of
    /// thousands of blocks from anything). A 250ms tick catches that window often enough, and a mark
    /// that far out can never match the real waypoint, so the same trader got a fresh bogus pin every
    /// time its chunk reloaded. There is no second field to prefer: in 1.22 <c>ServerPos</c> and
    /// <c>SidedPos</c> are obsolete aliases of <c>Pos</c>. So the scan trusts a position only once
    /// the same entity has reported it on two consecutive scans (<see cref="SettledPosition"/>): a
    /// mid-smoothing sample moves by a large fraction of the whole coordinate between ticks, while a
    /// real trader ambles a block or two.
    ///
    /// WHEN TO SCAN. Not before the player's waypoints have arrived. The client is only sent them in
    /// reply to a map view-change packet (see <see cref="WaypointService.RequestResync"/>), so the
    /// first ticks of a session see an empty list, and against an empty list every loaded trader
    /// looks unmarked. The scan asks for a resync itself and waits for either the list or a short
    /// grace period — the grace is for a player who genuinely has no waypoints yet, for whom an
    /// early scan cannot duplicate anything.
    /// </summary>
    public class TraderMarkers
    {
        /// <summary>
        /// Trade specialisations and their colours, defaulted to the palette used by Laimfo's
        /// Waypointer (Waypointer.TraderTypes.RoleColors) so players running both mods, or moving
        /// between them, keep reading the same colour as the same kind of trader.
        /// </summary>
        public static readonly Dictionary<string, string> DefaultRoleColors = new Dictionary<string, string>
        {
            { "agriculture",    "#9FAB3A" },
            { "artisan",        "#14A4DD" },
            { "buildmaterials", "#C8772E" },
            { "clothing",       "#92479B" },
            { "commodities",    "#F15A4A" },
            { "furniture",      "#5C1D02" },
            { "luxuries",       "#FDBB3A" },
            { "survivalgoods",  "#47B749" },
            { "treasurehunter", "#F6EA5E" },
        };

        public static readonly Dictionary<string, string> RoleTitles = new Dictionary<string, string>
        {
            { "agriculture",    "Agriculture" },
            { "artisan",        "Artisan" },
            { "buildmaterials", "Building Materials" },
            { "clothing",       "Clothing" },
            { "commodities",    "Commodities" },
            { "furniture",      "Furniture" },
            { "luxuries",       "Luxuries" },
            { "survivalgoods",  "Survival Goods" },
            { "treasurehunter", "Treasure Hunter" },
        };

        /// <summary>Waypointer's fallback for a trader whose role we do not recognise.</summary>
        public const string DefaultColor = "#D9D4CE";
        public const string DefaultRole = "trader";

        /// <summary>Ordered role list for the settings screen, so the rows never shuffle.</summary>
        public static readonly string[] Roles =
        {
            "agriculture", "artisan", "buildmaterials", "clothing", "commodities",
            "furniture", "luxuries", "survivalgoods", "treasurehunter",
        };

        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;
        readonly WaypointService svc;

        /// <summary>
        /// Positions we have already sent an add for this session. The server round-trip takes a
        /// moment, so without this the next tick would see no waypoint yet and add a duplicate.
        /// </summary>
        readonly List<Vec3d> pending = new List<Vec3d>();

        public int MarkedThisSession { get; private set; }

        /// <summary>
        /// How far an entity may move between two scans and still count as settled. A trader
        /// ambles; anything beyond a few blocks in 250ms is interpolation, not movement.
        /// </summary>
        const double SettleDistance = 4;

        /// <summary>Where each trader was on the previous scan, by entity id, and this scan's samples.</summary>
        Dictionary<long, Vec3d> lastSeen = new Dictionary<long, Vec3d>();
        Dictionary<long, Vec3d> seenNow = new Dictionary<long, Vec3d>();

        /// <summary>How long to wait for the first waypoint sync before scanning anyway.</summary>
        const long SyncGraceMs = 5000;

        long resyncAskedAt = -1;

        public TraderMarkers(ICoreClientAPI capi, PinMatrixConfig config, WaypointService svc)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
        }

        /// <summary>
        /// Waypointer's own role parse, matched deliberately: split the entity code on '-' and take
        /// the first segment that names a known specialisation. Matching its behaviour means both
        /// mods agree on what a given trader *is*, not just on the colour it gets painted.
        /// </summary>
        public static string RoleOf(string entityCodePath)
        {
            if (string.IsNullOrEmpty(entityCodePath)) return null;
            if (!entityCodePath.StartsWith("trader-", StringComparison.Ordinal)) return null;

            foreach (var part in entityCodePath.Split('-'))
            {
                if (DefaultRoleColors.ContainsKey(part)) return part;
            }
            return DefaultRole;
        }

        public string ColorHexFor(string role)
        {
            if (role != null && config.TraderMarkerColors != null &&
                config.TraderMarkerColors.TryGetValue(role, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
            if (role != null && DefaultRoleColors.TryGetValue(role, out var def)) return def;
            return DefaultColor;
        }

        public static string TitleFor(string role) =>
            role != null && RoleTitles.TryGetValue(role, out var t) ? t : "Trader";

        /// <summary>
        /// The trade specialisation a title names, or null if it names none — the inverse of
        /// <see cref="TitleFor"/>, so "Trader: Survival Goods" reads back as "survivalgoods".
        ///
        /// This exists for same-spot cleanup. Traders genuinely stand together in camps, but never
        /// two of the same kind, so two pins naming *different* specialisations are two different
        /// traders and must never be collapsed into each other however close they are. A pin whose
        /// specialisation cannot be read returns null and stays eligible to group, which is what
        /// lets a hand-placed pin and another tool's marker join the trader they both point at.
        /// </summary>
        public static string RoleFromTitle(string title, string prefix)
        {
            string t = (title ?? "").Trim();
            if (!string.IsNullOrEmpty(prefix) && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(prefix.Length).Trim();
            }
            if (t.Length == 0) return null;

            foreach (var pair in RoleTitles)
            {
                if (string.Equals(pair.Value, t, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            }
            return null;
        }

        /// <summary>Parses "#rrggbb" into the packed int the waypoint commands expect.</summary>
        public static int ParseHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return 0xD9D4CE;
            hex = hex.Trim().TrimStart('#');
            return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
                ? v & 0xFFFFFF
                : 0xD9D4CE;
        }

        /// <summary>Called from the watcher tick. Cheap and returns immediately while switched off.</summary>
        public void Scan()
        {
            if (!config.TraderMarkersEnabled) return;
            if (capi.World?.Player == null || svc.Layer == null) return;
            if (!WaypointsKnown()) return;

            var entities = capi.World.LoadedEntities;
            if (entities == null) return;

            double radius = Math.Max(1, config.TraderMarkerDedupeRadius);
            var existing = svc.Own;

            // 0 = whatever the client has loaded. Anything else is a real "walk up to it" range,
            // which is a different question from the dedupe radius above and deserves its own knob:
            // entity streaming usually loads a trader well before you can see them.
            double maxDist = config.TraderMarkerMaxDistance;
            var playerPos = capi.World.Player.Entity?.Pos?.XYZ;

            foreach (var pair in entities)
            {
                var entity = pair.Value;
                if (entity == null || !entity.Alive) continue;

                string role = RoleOf(entity.Code?.Path);
                if (role == null) continue;

                var pos = SettledPosition(entity.EntityId, entity.Pos);
                if (pos == null) continue;

                if (maxDist > 0 && playerPos != null &&
                    playerPos.SquareDistanceTo(pos) > maxDist * maxDist) continue;

                if (AlreadyMarked(existing, pos, radius)) continue;

                pending.Add(pos.Clone());
                string title = config.TraderMarkerTitlePrefix + TitleFor(role);
                capi.SendChatMessage(WpCommands.Add(
                    WpCommands.SafeIcon(config.TraderMarkerIcon),
                    pos.X, pos.Y, pos.Z,
                    config.TraderMarkerPinned,
                    ParseHex(ColorHexFor(role)),
                    title));

                MarkedThisSession++;
                // Spawn-relative, like the coordinate HUD, the table and the translocator lines —
                // an absolute position reads as nonsense next to the numbers on screen.
                capi.ShowChatMessage(WpCommands.ChatSafe(
                    $"[Pin Matrix] Marked {title} at {svc.RelX(pos.X):0}, {pos.Y:0}, {svc.RelZ(pos.Z):0}"));
            }

            // This scan's samples become the next scan's baseline; traders that have gone are
            // dropped with the old dictionary's contents.
            (lastSeen, seenNow) = (seenNow, lastSeen);
            seenNow.Clear();
        }

        /// <summary>
        /// The entity's position if it has held still since the previous scan, else null. The first
        /// sighting of any trader is always null — that is the tick the smoothing lies on. See the
        /// class remarks for why <c>Pos</c> cannot be trusted on its own.
        /// </summary>
        Vec3d SettledPosition(long entityId, EntityPos p)
        {
            if (p == null) return null;
            var pos = new Vec3d(p.X, p.Y, p.Z);
            seenNow[entityId] = pos;

            if (!lastSeen.TryGetValue(entityId, out var before)) return null;
            if (before.SquareDistanceTo(pos) > SettleDistance * SettleDistance) return null;
            return pos;
        }

        /// <summary>
        /// True once the client holds the player's waypoints, or once it has asked for them and
        /// waited long enough to conclude there are none. Until then a scan compares against an
        /// empty list and would re-mark every trader in sight on every login.
        /// </summary>
        bool WaypointsKnown()
        {
            if (svc.SyncedCount > 0) return true;

            long now = capi.World.ElapsedMilliseconds;
            if (resyncAskedAt < 0)
            {
                resyncAskedAt = now;
                svc.RequestResync();
                return false;
            }
            return now - resyncAskedAt >= SyncGraceMs;
        }

        /// <summary>
        /// True when this trader already has a marker. Deliberately positional rather than by entity
        /// id: ids do not survive a reload, and a trader wanders a few blocks around its cart, so
        /// "is there already a trader waypoint about here" is the question that actually matters.
        /// </summary>
        bool AlreadyMarked(List<Waypoint> existing, Vec3d pos, double radius)
        {
            double r2 = radius * radius;

            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].SquareDistanceTo(pos) <= r2) return true;
            }

            string icon = WpCommands.SafeIcon(config.TraderMarkerIcon);
            foreach (var wp in existing)
            {
                if (wp?.Position == null) continue;
                // Only our own kind of marker counts, so a player's hand-placed "Home" next to a
                // trader does not silently suppress the trader marker.
                if (!string.Equals(wp.Icon, icon, StringComparison.OrdinalIgnoreCase)) continue;
                if (wp.Position.SquareDistanceTo(pos) <= r2) return true;
            }
            return false;
        }

        /// <summary>Forgets the in-flight adds, e.g. after the player deletes markers and wants them back.</summary>
        public void ClearPending() => pending.Clear();
    }
}
