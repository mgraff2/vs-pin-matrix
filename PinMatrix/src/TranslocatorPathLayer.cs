using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// Draws a straight line on the world map between the two ends of every translocator hop the
    /// player has travelled.
    ///
    /// The only thing this owns is the drawing. What paths exist comes from
    /// <see cref="TranslocatorPaths.CurrentPaths"/>, which reconstructs them from waypoint titles —
    /// so there is no second copy of the data to keep in step, and deleting a pad's waypoint in the
    /// matrix removes its line with no further bookkeeping.
    ///
    /// This is the mod's first and only registration into another system's list
    /// (WorldMapManager.RegisterMapLayer). It is unconditional and nameless — there is no
    /// "if some mod is present" branch here, and the layer simply draws nothing when the feature is
    /// off or no hop has been recorded.
    /// </summary>
    public class TranslocatorPathLayer : MapLayer
    {
        /// <summary>Set by the mod system once its services exist; null until then, and drawn as empty.</summary>
        public static TranslocatorPaths Service;
        public static PinMatrixConfig Config;

        readonly TranslocatorPathComponent component;

        public TranslocatorPathLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            if (api is ICoreClientAPI capi) component = new TranslocatorPathComponent(capi);
        }

        public override string Title => "Translocator paths";

        /// <summary>Its own tab group, so toggling it never disturbs vanilla's waypoint layer.</summary>
        public override string LayerGroupCode => "pinmatrixtl";

        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        /// <summary>Lines are point-to-point, not chunk data — nothing here needs a chunk loaded.</summary>
        public override bool RequireChunkLoaded => false;

        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active) return;
            if (Config == null || !Config.TranslocatorPathsEnabled) return;
            component?.Render(mapElem, dt);
        }

        public override void Dispose()
        {
            component?.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Moves this layer to draw *before* vanilla's waypoint layer, so pad icons sit on top of
        /// the lines instead of under them.
        ///
        /// GuiElementMap renders layers in plain list order — later means on top — and the list is
        /// built from the registry in insertion order. Vanilla registers its layers in Start(); we
        /// register in StartClientSide, which necessarily runs later, so without this our lines
        /// would always paint over every marker on the map. ZIndex exists on MapLayer but the map
        /// element does not consult it.
        /// </summary>
        public static void OrderBehindWaypoints(ICoreClientAPI capi)
        {
            var layers = capi.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers;
            if (layers == null) return;

            int ours = layers.FindIndex(l => l is TranslocatorPathLayer);
            int waypoints = layers.FindIndex(l => l is WaypointMapLayer);
            if (ours < 0 || waypoints < 0 || ours < waypoints) return;

            var layer = layers[ours];
            layers.RemoveAt(ours);
            layers.Insert(layers.FindIndex(l => l is WaypointMapLayer), layer);
        }
    }

    /// <summary>
    /// The actual line drawing.
    ///
    /// A rotated quad per segment through the engine's GUI shader is the only way to put a diagonal
    /// line on the map: the 2D render helpers draw axis-aligned rectangles only, and a Cairo-drawn
    /// texture would have to be regenerated every frame because the map pans and zooms continuously.
    /// </summary>
    public class TranslocatorPathComponent : MapComponent
    {
        MeshRef quad;
        readonly Matrixf mat = new Matrixf();
        readonly Vec4f lineColor = new Vec4f();
        readonly Vec4f endColor = new Vec4f();

        /// <summary>Half-size of the little square drawn at each pad, in pixels.</summary>
        const float EndMarkerPx = 7f;

        public TranslocatorPathComponent(ICoreClientAPI capi) : base(capi) { }

        public override void Render(GuiElementMap map, float dt)
        {
            var service = TranslocatorPathLayer.Service;
            var config = TranslocatorPathLayer.Config;
            if (service == null || config == null) return;

            var paths = service.CurrentPaths();
            if (paths.Count == 0) return;

            if (quad == null) quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

            var shader = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
            shader.Uniform("extraGlow", 0);
            shader.Uniform("applyColor", 0);
            shader.Uniform("noTexture", 1f);
            shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

            // Without this the lines spill outside the map frame and paint over the dialog border.
            // Wrapped in try/finally because an unbalanced scissor stack does not fail locally — it
            // silently misrenders every GUI drawn afterwards, which is a miserable thing to debug.
            capi.Render.PushScissor(map.Bounds, true);
            try
            {
                var viewA = new Vec2f();
                var viewB = new Vec2f();
                var worldA = new Vec3d();
                var worldB = new Vec3d();

                float half = (float)Math.Max(0.5, config.TranslocatorLineThickness) / 2f;

                foreach (var path in paths)
                {
                    worldA.Set(path.From.X, path.From.Y, path.From.Z);
                    worldB.Set(path.To.X, path.To.Y, path.To.Z);
                    map.TranslateWorldPosToViewPos(worldA, ref viewA);
                    map.TranslateWorldPosToViewPos(worldB, ref viewB);

                    float ax = (float)(map.Bounds.renderX + viewA.X);
                    float ay = (float)(map.Bounds.renderY + viewA.Y);
                    float bx = (float)(map.Bounds.renderX + viewB.X);
                    float by = (float)(map.Bounds.renderY + viewB.Y);

                    // Both ends off the same edge means the whole segment is off-screen. Testing each
                    // axis separately still draws a line that crosses the view with both ends outside.
                    if (BothBeyond(ax, bx, map.Bounds.renderX, map.Bounds.renderX + map.Bounds.InnerWidth)) continue;
                    if (BothBeyond(ay, by, map.Bounds.renderY, map.Bounds.renderY + map.Bounds.InnerHeight)) continue;

                    float dx = bx - ax, dy = by - ay;
                    float length = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (length < 0.001f) continue;

                    int rgb = TraderMarkers.ParseHex(path.Recent
                        ? config.TranslocatorRecentColor
                        : config.TranslocatorMarkerColor);
                    float r = ((rgb >> 16) & 0xFF) / 255f;
                    float g = ((rgb >> 8) & 0xFF) / 255f;
                    float b = (rgb & 0xFF) / 255f;

                    // A recent hop is drawn more opaque as well as in a different hue, so it reads as
                    // "this one" even where the two colours land on similar terrain.
                    lineColor.Set(r, g, b, path.Recent ? 0.95f : 0.55f);
                    endColor.Set(r, g, b, 1f);

                    DrawQuad(shader, (ax + bx) / 2f, (ay + by) / 2f, (float)Math.Atan2(dy, dx), length / 2f, half, lineColor);
                    DrawQuad(shader, ax, ay, 0f, EndMarkerPx / 2f, EndMarkerPx / 2f, endColor);
                    DrawQuad(shader, bx, by, 0f, EndMarkerPx / 2f, EndMarkerPx / 2f, endColor);
                }
            }
            finally
            {
                capi.Render.PopScissor();
            }
        }

        void DrawQuad(IShaderProgram shader, float cx, float cy, float angle, float halfW, float halfH, Vec4f color)
        {
            mat.Set(capi.Render.CurrentModelviewMatrix).Translate(cx, cy, 60f);
            if (angle != 0f) mat.RotateZ(angle);
            mat.Scale(halfW, halfH, 0f);
            shader.Uniform("rgbaIn", color);
            shader.UniformMatrix("modelViewMatrix", mat.Values);
            capi.Render.RenderMesh(quad);
        }

        /// <summary>True when both coordinates are outside the same edge of the view.</summary>
        static bool BothBeyond(double a, double b, double lo, double hi)
        {
            const double slack = 8.0;
            if (a < lo - slack && b < lo - slack) return true;
            if (a > hi + slack && b > hi + slack) return true;
            return false;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (quad != null)
            {
                quad.Dispose();
                quad = null;
            }
        }
    }
}
