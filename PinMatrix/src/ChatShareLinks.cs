using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace PinMatrix
{
    /// <summary>
    /// Receiving side of waypoint sharing. The server escapes angle brackets in player chat, so a
    /// sender can never deliver a clickable VTML link directly — instead, when a share line (see
    /// WpCommands.ShareLine) arrives in chat, this prints an extra client-side line whose
    /// command:// link re-runs the embedded /waypoint addati. Clicking it shows the vanilla
    /// "run this command?" confirmation. Players without Pin Matrix just see the plain share line.
    /// </summary>
    public class ChatShareLinks
    {
        // Anchored to the marker + separator; the command's shape is validated strictly so a
        // crafted chat line can only ever link the same kind of command we would build ourselves.
        static readonly Regex ShareRx = new Regex(
            @"\[Pin Matrix\][^|]*\|\s*add:\s*(/waypoint addati \S+ =?-?[0-9.]+ =?-?[0-9.]+ =?-?[0-9.]+ (?:true|false) #[0-9a-fA-F]{6} [^<>""]+?)\s*$",
            RegexOptions.Compiled);

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

            var m = ShareRx.Match(message);
            if (!m.Success) return;
            string cmd = m.Groups[1].Value;

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
