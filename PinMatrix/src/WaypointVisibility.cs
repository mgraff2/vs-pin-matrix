using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// Per-world "hidden pins" overlay: waypoints the player has switched off stop being drawn on
    /// the world map and the minimap, while staying exactly where they are on the server.
    ///
    /// WHY IT WORKS THIS WAY. Vanilla's <see cref="Waypoint"/> has no visibility field of any kind
    /// (Position, Title, Text, Color, Icon, ShowInWorld, Pinned, OwningPlayerUid,
    /// OwningPlayerGroupId, Temporary, Guid — verified against 1.22.6 VSEssentials), so "hidden"
    /// can only ever be this mod's own concept. The two ways to implement it are:
    ///
    ///   1. delete the waypoint and re-add it later from a local copy, or
    ///   2. leave the waypoint alone and skip drawing it.
    ///
    /// This is (2), deliberately. (1) is destructive — losing this mod or its JSON would lose the
    /// waypoints for good — it costs one chat command per pin in both directions, and every restore
    /// mints a fresh Guid, breaking identity for the recycle bin, sharing and any other mod. Hiding
    /// 300 resin trees and showing them again next week has to be instant and free, and it has to be
    /// impossible to lose data by doing it.
    ///
    /// HOW (2) IS DONE. <see cref="WaypointMapLayer"/> draws map markers by iterating a private
    /// <c>List&lt;MapComponent&gt; wayPointComponents</c>, rebuilt from <c>ownWaypoints</c> in
    /// <c>RebuildMapComponents()</c>. Both the full map and the minimap render through that one
    /// list, so dropping a component removes the pin from the map, the minimap, its hover text and
    /// middle-click editing at once — with the waypoint itself untouched. <c>MapComponent.Dispose</c>
    /// is a no-op, so dropped components leak nothing.
    ///
    /// The list is rebuilt on map-open and on every server resync — including the resync that
    /// dragging or zooming the map triggers every 32 blocks of travel — so <see cref="Apply"/> has to
    /// re-filter after each rebuild and before the next draw. That is what
    /// <see cref="WaypointVisibilityRenderer"/> below is for; read its remarks before moving the call
    /// anywhere else. It costs nothing at all until something is actually hidden.
    ///
    /// THE REFLECTION IS THE RISK, AND IT FAILS SOFT. Both field names are private; they are
    /// identical in 1.22.0 through 1.22.6 (checked against every server package in tools/server-cache).
    /// If a future version renames them, <see cref="Available"/> goes false, the feature disables
    /// itself with one logged warning, and the rest of the mod carries on — pins already hidden come
    /// back into view, which is the right way to fail: visible and recoverable, never lost.
    /// </summary>
    public class WaypointVisibility
    {
        readonly ICoreClientAPI capi;
        readonly WaypointService svc;
        readonly string filePath;

        HashSet<string> hidden = new HashSet<string>();

        static FieldInfo componentsField;    // WaypointMapLayer.wayPointComponents
        static FieldInfo compWaypointField;  // WaypointMapComponent.waypoint
        static MethodInfo rebuildMethod;     // WaypointMapLayer.RebuildMapComponents()
        static bool resolved;
        static bool resolvedOk;

        public WaypointVisibility(ICoreClientAPI capi, WaypointService svc)
        {
            this.capi = capi;
            this.svc = svc;

            Resolve(capi);

            string folder = capi.GetOrCreateDataPath(Path.Combine("ModData", "pinmatrix"));
            filePath = Path.Combine(folder, "hidden-" + WorldTag() + ".json");
            Load();
        }

        /// <summary>Whether hiding actually works on this game version (see the class remarks).</summary>
        public bool Available => resolvedOk;

        public int HiddenCount => hidden.Count;

        public bool IsHidden(string key) => hidden.Count > 0 && hidden.Contains(key);

        public bool IsHidden(Waypoint wp) => hidden.Count > 0 && hidden.Contains(PinKey.KeyOf(wp));

        // ------------------------------------------------------------------ reflection

        static void Resolve(ICoreClientAPI capi)
        {
            if (resolved) return;
            resolved = true;

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                componentsField = typeof(WaypointMapLayer).GetField("wayPointComponents", flags);
                compWaypointField = typeof(WaypointMapComponent).GetField("waypoint", flags);
                rebuildMethod = typeof(WaypointMapLayer).GetMethod("RebuildMapComponents", flags, null, Type.EmptyTypes, null);

                resolvedOk = componentsField != null
                    && typeof(IList<MapComponent>).IsAssignableFrom(componentsField.FieldType)
                    && compWaypointField != null
                    && typeof(Waypoint).IsAssignableFrom(compWaypointField.FieldType);
            }
            catch (Exception e)
            {
                resolvedOk = false;
                capi.Logger.Warning("[pinmatrix] Could not inspect the waypoint map layer: {0}", e.Message);
            }

            if (!resolvedOk)
            {
                capi.Logger.Warning("[pinmatrix] This game version's waypoint map layer does not look the way hiding needs it to — the hide/show feature is disabled. Every waypoint stays visible; nothing else is affected.");
            }
        }

        // ------------------------------------------------------------------ applying

        /// <summary>
        /// Drops the hidden pins' map components, so the map and minimap skip them. Idempotent and
        /// cheap: it matches components to waypoints by reference, does nothing while nothing is
        /// hidden, and re-does its work after each of vanilla's own rebuilds.
        /// </summary>
        public void Apply()
        {
            if (!resolvedOk || hidden.Count == 0) return;

            try
            {
                var layer = svc.Layer;
                if (layer == null) return;

                var list = componentsField.GetValue(layer) as IList<MapComponent>;
                if (list == null || list.Count == 0) return;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!(list[i] is WaypointMapComponent wc)) continue;
                    if (!(compWaypointField.GetValue(wc) is Waypoint wp)) continue;
                    if (hidden.Contains(PinKey.KeyOf(wp))) list.RemoveAt(i);
                }
            }
            catch (Exception e)
            {
                resolvedOk = false;
                capi.Logger.Warning("[pinmatrix] Hiding waypoints failed and has been switched off for this session ({0}). All waypoints are visible again.", e.Message);
            }
        }

        /// <summary>
        /// Puts the components back after something is un-hidden. Vanilla rebuilds the whole list
        /// from its own waypoints, and <see cref="Apply"/> then removes whatever is still hidden.
        /// </summary>
        void Restore()
        {
            try
            {
                var layer = svc.Layer;
                if (layer != null && rebuildMethod != null)
                {
                    // No-op while no map is open — the list is rebuilt on map-open anyway
                    rebuildMethod.Invoke(layer, null);
                    Apply();
                    return;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not redraw the waypoint markers directly ({0}); asking the server for a resync instead.", e.Message);
            }

            // Fallback: the server's reply rebuilds the components for us a moment later
            svc.RequestResync();
        }

        // ------------------------------------------------------------------ mutating

        /// <summary>Hides or shows the given pins. Returns how many actually changed state.</summary>
        public int Set(IEnumerable<string> keys, bool hide)
        {
            if (!resolvedOk) return 0;

            int changed = 0;
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (hide ? hidden.Add(key) : hidden.Remove(key)) changed++;
            }

            if (changed == 0) return 0;

            Save();
            if (hide) Apply(); else Restore();
            return changed;
        }

        /// <summary>
        /// Forgets pins that no longer exist, so the file cannot grow forever. Only ever called with
        /// a complete waypoint list — the server sends all of them in one packet — because pruning
        /// against a partial one would silently un-hide everything.
        /// </summary>
        public void PruneTo(ICollection<string> liveKeys)
        {
            if (hidden.Count == 0 || liveKeys == null || liveKeys.Count == 0) return;

            int before = hidden.Count;
            hidden.RemoveWhere(k => !liveKeys.Contains(k));
            if (hidden.Count != before) Save();
        }

        // ------------------------------------------------------------------ persistence

        /// <summary>
        /// Hidden state is per world: the same waypoint key means nothing across savegames, and a
        /// single shared file would leak one world's hidden pins into another's.
        /// </summary>
        string WorldTag()
        {
            string id = null;
            try { id = capi.World?.SavegameIdentifier; } catch (Exception) { /* not connected yet */ }
            if (string.IsNullOrEmpty(id)) return "unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = id.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            string tag = new string(chars);
            return tag.Length > 64 ? tag.Substring(0, 64) : tag;
        }

        void Load()
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var keys = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(filePath));
                if (keys != null) hidden = new HashSet<string>(keys.Where(k => !string.IsNullOrEmpty(k)));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not read the hidden-waypoint file, starting with everything visible: {0}", e.Message);
                try { File.Copy(filePath, filePath + ".broken", true); } catch { /* best effort */ }
                hidden = new HashSet<string>();
            }
        }

        void Save()
        {
            try
            {
                if (hidden.Count == 0)
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    return;
                }
                File.WriteAllText(filePath, JsonConvert.SerializeObject(hidden.ToList(), Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Error("[pinmatrix] Could not save which waypoints are hidden: {0}", e.Message);
            }
        }
    }

    /// <summary>
    /// Re-hides the switched-off pins once per frame, immediately before the GUI is drawn.
    ///
    /// WHY THIS IS A RENDERER AND NOT A TICK. Vanilla rebuilds <c>wayPointComponents</c> from
    /// scratch — hidden pins included — inside <c>WaypointMapLayer.OnDataFromServer</c>, and the
    /// server re-sends every waypoint each time the map view crosses a 32-block chunk boundary
    /// (<c>GuiElementMap.EnsureMapFullyLoaded</c> → <c>viewChangedSync</c> →
    /// <c>OnViewChangedServer</c> → <c>ResendWaypoints</c>). So dragging or zooming the map fires a
    /// full rebuild every few pixels of travel.
    ///
    /// This used to be re-filtered from a 20ms game tick, which is a race against the *render
    /// frame*, not against wall-clock: at 60-144fps one to three frames could be drawn from the
    /// rebuilt list before the tick ran, and the hidden pins visibly flashed all the way through a
    /// drag. No tick interval fixes that — only running after the rebuild and before the draw does.
    ///
    /// Packet handling and the rebuild both happen in the game tick, ahead of any render stage, and
    /// GuiManager draws every dialog at <see cref="EnumRenderStage.Ortho"/> order 1.0 (both the
    /// world map and the minimap render the layer through that one list). Filtering at Ortho with a
    /// lower order is therefore the last hook before the pins would be drawn: no frame can render an
    /// unfiltered list, so the flash is zero rather than merely short.
    ///
    /// Costs nothing per frame while nothing is hidden — <see cref="WaypointVisibility.Apply"/>
    /// returns on its first line, and the supplier is null until the world is joined.
    /// </summary>
    public class WaypointVisibilityRenderer : IRenderer
    {
        readonly Func<WaypointVisibility> supplier;

        public WaypointVisibilityRenderer(Func<WaypointVisibility> supplier)
        {
            this.supplier = supplier;
        }

        /// <summary>Below GuiManager's 1.0, so this runs before any dialog draws.</summary>
        public double RenderOrder => 0.9;

        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            supplier()?.Apply();
        }

        public void Dispose() { }
    }
}
