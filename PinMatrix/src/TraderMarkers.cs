using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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

                var pos = entity.Pos?.XYZ;
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
                capi.ShowChatMessage(WpCommands.ChatSafe($"[Pin Matrix] Marked {title} at {pos.X:0}, {pos.Y:0}, {pos.Z:0}"));
            }
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
