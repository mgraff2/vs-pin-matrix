using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace PinMatrix
{
    /// <summary>
    /// Receiving side of waypoint sharing. The server escapes angle brackets in player chat, so a
    /// sender can never deliver a clickable VTML link directly — instead, when a share line (see
    /// WpCommands.ShareLine) arrives in chat, this prints an extra client-side line whose
    /// command:// link runs /waypoint addati for the clicking player. Clicking shows the vanilla
    /// "run this command?" confirmation. Players without Pin Matrix just see the plain share line.
    /// </summary>
    public class ChatShareLinks
    {
        // Current form (1.1.1): [Pin Matrix] Title (x, y, z) — name and coordinates only.
        // Coordinates are plain command-parser form: X/Z spawn-relative, Y absolute. The line
        // carries no icon/color/pinned data, so the link adds with a neutral default look —
        // the deliberate price of a chat line with zero machine noise.
        static readonly Regex ShareRx = new Regex(
            @"\[Pin Matrix\] (?<title>[^|<>""]+?) \((?<x>-?[0-9.]+), (?<y>-?[0-9.]+), (?<z>-?[0-9.]+)\)\s*$",
            RegexOptions.Compiled);

        // Interim test-build form: "... (x, y, z) | icon #hex [pinned]" tail.
        static readonly Regex TailRx = new Regex(
            @"\[Pin Matrix\] (?<title>[^|<>""]+?) \((?<x>-?[0-9.]+), (?<y>-?[0-9.]+), (?<z>-?[0-9.]+)\) \| (?<icon>[0-9A-Za-z_-]+) (?<color>#[0-9a-fA-F]{6})(?<pinned> pinned)?\s*$",
            RegexOptions.Compiled);

        // Legacy form (1.1.0): "... | add: /waypoint addati ..." with the command inline.
        static readonly Regex LegacyRx = new Regex(
            @"\[Pin Matrix\][^|]*\|\s*add:\s*(/waypoint addati \S+ =?-?[0-9.]+ =?-?[0-9.]+ =?-?[0-9.]+ (?:true|false) #[0-9a-fA-F]{6} [^<>""]+?)\s*$",
            RegexOptions.Compiled);

        const string DefaultIcon = "circle";
        const string DefaultColor = "#ff8800";

        readonly ICoreClientAPI capi;
        bool injecting;

        public ChatShareLinks(ICoreClientAPI capi)
        {
            this.capi = capi;
            capi.Event.ChatMessage += OnChatMessage;
        }

        void OnChatMessage(int groupId, string message, EnumChatType chattype, string data)
        {
            if (injecting || message == null) return;
            if (chattype != EnumChatType.OthersMessage && chattype != EnumChatType.OwnMessage) return;
            if (message.Contains("command://")) return;   // never re-process an already-linkified line

            string cmd = null;
            var tm = TailRx.Match(message);
            var m = tm.Success ? tm : ShareRx.Match(message);
            if (m.Success)
            {
                string title = WpCommands.ShareSafeTitle(m.Groups["title"].Value);
                string icon = tm.Success ? tm.Groups["icon"].Value : DefaultIcon;
                string color = tm.Success ? tm.Groups["color"].Value : DefaultColor;
                string pinned = tm.Success && tm.Groups["pinned"].Success ? "true" : "false";
                cmd = $"/waypoint addati {icon} {m.Groups["x"].Value} {m.Groups["y"].Value} {m.Groups["z"].Value} {pinned} {color} {title}";
            }
            else
            {
                var lm = LegacyRx.Match(message);
                if (lm.Success) cmd = lm.Groups[1].Value;
            }
            if (cmd == null) return;

            injecting = true;
            try
            {
                capi.ShowChatMessage($"<a href=\"command://{cmd}\">[Pin Matrix] Click here to add this waypoint to your map</a>");
            }
            finally
            {
                injecting = false;
            }
        }

        public void Dispose()
        {
            capi.Event.ChatMessage -= OnChatMessage;
        }
    }
}
