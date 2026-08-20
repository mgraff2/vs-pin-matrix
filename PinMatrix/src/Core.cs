using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>One table row: a live waypoint plus its current index in the layer's own-waypoint list.</summary>
    public class PinRow
    {
        public Waypoint Wp;
        public int Index;
        public string Key;
        public double Dist;
    }

    public static class PinKey
    {
        /// <summary>Stable identity for a waypoint: Guid when the server assigned one, else position+title.</summary>
        public static string KeyOf(Waypoint wp)
        {
            if (!string.IsNullOrEmpty(wp.Guid)) return wp.Guid;
            return FormattableString.Invariant($"@{wp.Position.X:0.##}|{wp.Position.Y:0.##}|{wp.Position.Z:0.##}|{wp.Title}");
        }
    }

    /// <summary>Builds the exact chat commands the vanilla waypoint dialogs use (verified against 1.22.6 VSEssentials).</summary>
    public static class WpCommands
    {
        public static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

        public static string ColorHex(int argb) => "#" + (argb & 0xFFFFFF).ToString("x6");

        /// <summary>
        /// Makes arbitrary text safe to drop into a VTML string.
        ///
        /// Every rich-text label this mod builds mixes markup we wrote with text we did not — pin
        /// titles, set names, search terms. A title containing a stray angle bracket would otherwise
        /// be parsed as a tag and swallow the rest of the label.
        /// </summary>
        public static string VtmlEscape(string s) =>
            (s ?? "").Replace("<", "&lt;").Replace(">", "&gt;");

        public static string SafeTitle(string title)
        {
            title = (title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return title.Length == 0 ? "Waypoint" : title;
        }

        public static string SafeIcon(string icon)
        {
            icon = (icon ?? "").Trim();
            if (icon.Length == 0 || icon.Contains(' ')) return "circle";
            return icon;
        }

        /// <summary>/waypoint addati [icon] =x =y =z [pinned] [color] [title] — '=' means absolute world coords.</summary>
        public static string Add(string icon, double x, double y, double z, bool pinned, int color, string title)
            => $"/waypoint addati {SafeIcon(icon)} ={F(x)} ={F(y)} ={F(z)} {(pinned ? "true" : "false")} {ColorHex(color)} {SafeTitle(title)}";

        /// <summary>/waypoint modify [index] [color] [icon] [pinned] [title] — index-based, position cannot change.</summary>
        public static string Modify(int index, int color, string icon, bool pinned, string title)
            => $"/waypoint modify {index} {ColorHex(color)} {SafeIcon(icon)} {(pinned ? "true" : "false")} {SafeTitle(title)}";

        /// <summary>/waypoint remove [index] — index-based; bulk removals must run in descending index order.</summary>
        public static string Remove(int index) => $"/waypoint remove {index}";

        // ---- sharing ----

        /// <summary>
        /// Escapes text bound for capi.ShowChatMessage.
        ///
        /// Chat is rendered as VTML, so a bare '&lt;' opens a tag. A message containing "&lt;-&gt;" produced
        /// "Found closing tag &lt;font&gt; ... but &lt;-&gt; should be closed first" and corrupted the chat
        /// renderer for every later message, which presents to the player as the mod having broken.
        /// Anything we build from coordinates, titles or user input goes through here.
        /// </summary>
        public static string ChatSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>Marker prefix that lets receiving Pin Matrix clients recognize a share line in chat.</summary>
        public const string ShareMarker = "[Pin Matrix]";

        /// <summary>
        /// Title safe to travel through chat and to sit inside a VTML href attribute on the
        /// receiving side: the server escapes angle brackets in player chat (which would corrupt
        /// the embedded command), and quotes would terminate the href attribute.
        /// </summary>
        public static string ShareSafeTitle(string title)
        {
            var sb = new StringBuilder();
            foreach (char ch in SafeTitle(title))
            {
                if (ch != '<' && ch != '>' && ch != '"' && ch != '&' && ch != '|') sb.Append(ch);
            }
            string t = sb.ToString().Trim();
            return t.Length == 0 ? "Waypoint" : t;
        }

        /// <summary>
        /// The add command another player runs to get a copy of this waypoint. Uses plain
        /// (unprefixed) coordinates — X/Z spawn-relative, Y absolute — the same convention as the
        /// coordinate HUD and the share line's readable part. Safe because the command parser
        /// resolves plain numbers against (spawnX, 0, spawnZ), which is identical for every
        /// player on the server (PopFlexiblePos, verified against 1.22.6 vsapi).
        /// </summary>
        public static string ShareCommand(Waypoint wp, double relX, double relZ)
            => $"/waypoint addati {SafeIcon(wp.Icon)} {F(relX)} {F(wp.Position.Y)} {F(relZ)} {(wp.Pinned ? "true" : "false")} {ColorHex(wp.Color)} {ShareSafeTitle(wp.Title)}";

        /// <summary>
        /// One chat line: readable name + coords, then a compact "| icon #hex [pinned]" tail.
        /// The tail is the only channel for the pin's look — the server strips VTML from player
        /// chat and does not relay hidden data, so anything the clickable link should reproduce
        /// must ride in the visible text. Format is load-bearing — ChatShareLinks parses it.
        /// </summary>
        public static string ShareLine(Waypoint wp, string relCoords)
            => $"{ShareMarker} {ShareSafeTitle(wp.Title)} ({relCoords}) | {SafeIcon(wp.Icon)} {ColorHex(wp.Color)}{(wp.Pinned ? " pinned" : "")}";
    }

    /// <summary>Read access to the client's waypoint layer plus spawn-relative coordinate conversion.</summary>
    public class WaypointService
    {
        readonly ICoreClientAPI capi;

        public WaypointService(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public WorldMapManager MapManager => capi.ModLoader.GetModSystem<WorldMapManager>();

        public WaypointMapLayer Layer
        {
            get
            {
                var mm = MapManager;
                if (mm?.MapLayers == null) return null;
                foreach (var layer in mm.MapLayers)
                {
                    if (layer is WaypointMapLayer wml) return wml;
                }
                return null;
            }
        }

        /// <summary>
        /// The player's own waypoints, in the exact order of the server's command index space.
        /// CRITICAL: the client's synced list can also contain group-shared waypoints owned by other
        /// players, but /waypoint modify|remove indices count own waypoints only (verified against
        /// 1.22.6 WaypointMapLayer.ResendWaypoints vs OnCmdWayPointRemove) — so filter by owner UID.
        /// </summary>
        public List<Waypoint> Own
        {
            get
            {
                var layer = Layer;
                if (layer?.ownWaypoints == null) return new List<Waypoint>();
                string uid = capi.World.Player?.PlayerUID;
                return layer.ownWaypoints.Where(wp => wp.OwningPlayerUid == uid).ToList();
            }
        }

        /// <summary>How many waypoints the client has been sent at all, owned by anyone (see <see cref="Own"/>).</summary>
        public int SyncedCount => Layer?.ownWaypoints?.Count ?? 0;

        /// <summary>
        /// Asks the server to re-send this player's waypoints.
        ///
        /// The client is only ever sent waypoints in reply to a map view-change packet
        /// (`WorldMapManager.OnViewChangedServer` → `WaypointMapLayer.ResendWaypoints`), which
        /// vanilla sends only from the world map dialog. So a session that never opened the map —
        /// entirely possible once the hotkey is bound — has an empty `ownWaypoints` and the matrix
        /// would show "no waypoints" while the map layer itself has never been told otherwise.
        /// This sends the same packet vanilla does; the view rectangle is ignored by the waypoint
        /// layer, so it is kept to the player's own chunk to stay cheap for other map layers.
        /// </summary>
        public void RequestResync()
        {
            try
            {
                var channel = capi.Network.GetChannel("worldmap");
                if (channel == null) return;

                var pos = capi.World.Player?.Entity?.Pos;
                if (pos == null) return;

                int cx = (int)(pos.X / GlobalConstants.ChunkSize);
                int cz = (int)(pos.Z / GlobalConstants.ChunkSize);
                channel.SendPacket(new OnViewChangedPacket { X1 = cx, Z1 = cz, X2 = cx, Z2 = cz });
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not request a waypoint resync: {0}", e.Message);
            }
        }

        public Vec3d SpawnPos => capi.World.DefaultSpawnPosition.XYZ;

        // The table, editors and exports show X/Z relative to world spawn (what the coordinate HUD shows); Y stays absolute.
        public double RelX(double absX) => absX - SpawnPos.X;
        public double RelZ(double absZ) => absZ - SpawnPos.Z;
        public double AbsX(double relX) => relX + SpawnPos.X;
        public double AbsZ(double relZ) => relZ + SpawnPos.Z;

        public double ClampY(double y) => GameMath.Clamp(y, 0, capi.World.BlockAccessor.MapSizeY);
        public double ClampAbsX(double x) => GameMath.Clamp(x, 0, capi.World.BlockAccessor.MapSizeX);
        public double ClampAbsZ(double z) => GameMath.Clamp(z, 0, capi.World.BlockAccessor.MapSizeZ);

        /// <summary>Change-detection fingerprint of the own-waypoint list.</summary>
        public string Signature()
        {
            var sb = new StringBuilder();
            foreach (var wp in Own)
            {
                sb.Append(PinKey.KeyOf(wp)).Append('|').Append(wp.Title).Append('|').Append(wp.Color)
                  .Append('|').Append(wp.Icon).Append('|').Append(wp.Pinned ? '1' : '0').Append(';');
            }
            return sb.ToString();
        }

        public int ResolveIndex(string key)
        {
            var own = Own;
            for (int i = 0; i < own.Count; i++)
            {
                if (PinKey.KeyOf(own[i]) == key) return i;
            }
            return -1;
        }

        public string[] IconCodes()
        {
            var layer = Layer;
            if (layer?.WaypointIcons == null || layer.WaypointIcons.Count == 0) return new[] { "circle" };
            return layer.WaypointIcons.Keys.ToArray();
        }

        public int[] PaletteColors()
        {
            var layer = Layer;
            if (layer?.WaypointColors == null || layer.WaypointColors.Count == 0) return new[] { unchecked((int)0xFFFFFFFF) };
            return layer.WaypointColors.ToArray();
        }
    }

    /// <summary>
    /// Sends a prepared list of chat commands with optional per-command throttling.
    /// While Busy, the dialog suppresses list polling so mid-batch resyncs can't shift captured indices (spec §4).
    /// </summary>
    public class BatchEngine
    {
        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;

        Queue<string> queue;
        Action onDone;
        int total;

        public bool Busy { get; private set; }
        public Action<int, int> OnProgress;

        public BatchEngine(ICoreClientAPI capi, PinMatrixConfig config)
        {
            this.capi = capi;
            this.config = config;
        }

        public void Run(IEnumerable<string> commands, Action onDone)
        {
            if (Busy) return;
            queue = new Queue<string>(commands);
            this.onDone = onDone;
            total = queue.Count;
            Busy = true;

            if (total == 0) { Finish(); return; }

            if (config.BulkOpDelayMs <= 0)
            {
                while (queue.Count > 0) capi.SendChatMessage(queue.Dequeue());
                ScheduleFinish();
            }
            else
            {
                SendNext();
            }
        }

        void SendNext()
        {
            if (queue.Count == 0) { ScheduleFinish(); return; }
            capi.SendChatMessage(queue.Dequeue());
            OnProgress?.Invoke(total - queue.Count, total);
            capi.World.RegisterCallback(_ => SendNext(), Math.Max(1, config.BulkOpDelayMs));
        }

        // Give the server a moment to process and resync before the dialog re-reads the layer.
        void ScheduleFinish() => capi.World.RegisterCallback(_ => Finish(), 700);

        void Finish()
        {
            Busy = false;
            var cb = onDone;
            onDone = null;
            cb?.Invoke();
        }
    }

    /// <summary>Single-level undo snapshot for the last mutating bulk op (not deletes — the bin covers those).</summary>
    public class UndoState
    {
        public string Label;
        public List<UndoEntry> Entries = new List<UndoEntry>();
    }

    public class UndoEntry
    {
        public string Key;
        public string Title;
        public int Color;
        public string Icon;
        public bool Pinned;
    }
}
