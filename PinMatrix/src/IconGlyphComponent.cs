using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace PinMatrix
{
    /// <summary>
    /// Makes the waypoint icon SVGs paintable. Shared, because two unrelated places need it: the
    /// editor's dropdowns and the map screen's icon buttons, and either can be the first to draw one.
    ///
    /// <see cref="Vintagestory.GameContent.WaypointMapLayer"/> indexes
    /// <c>textures/icons/worldmap/</c> with <c>loadAsset: false</c> and registers a custom GUI icon
    /// per asset whose renderer draws the <c>IAsset</c> *object* — so until something loads that
    /// object's data, painting the icon throws ArgumentNullException("svgAsset"). Vanilla loads them
    /// lazily, when the world map first builds its waypoint icon textures, so a session that never
    /// opened the map has most of the set still unloaded. <c>GetMany</c> hands back the *cached*
    /// asset instances and fills them in place, which is what populates the very objects those
    /// renderers closed over.
    /// </summary>
    public static class WaypointIconAssets
    {
        static bool loaded;

        /// <summary>Cheap to call repeatedly; does its work once per session.</summary>
        public static void EnsureLoaded(ICoreClientAPI capi)
        {
            if (loaded || capi == null) return;
            loaded = true;
            try
            {
                capi.Assets.GetMany("textures/icons/worldmap/", null, true);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not preload the waypoint icon assets: {0}", e.Message);
            }
        }
    }

    /// <summary>
    /// VTML component that paints a waypoint icon inline with the text:
    /// <c>&lt;pmicon code="resin"/&gt;</c>. The icon counterpart of
    /// <see cref="ColorSwatchComponent"/>, and it exists for the same reason: dropdown entry labels
    /// are run through <see cref="VtmlUtil"/> before being drawn, so a custom tag is the only way to
    /// get anything but text into a list menu.
    ///
    /// It is what let the icon *filter* stop being a 36-cell grid occupying two rows of the matrix
    /// screen and become one dropdown beside the colour one — the entries still show the icon, so
    /// nothing was traded away for the space.
    ///
    /// DRAWING AN ICON CAN THROW, and here that would happen inside a list menu's compose. Vanilla
    /// registers worldmap icons whose renderer draws an <see cref="IAsset"/> object that may not
    /// have been loaded yet, so a bad code raises ArgumentNullException mid-paint. Callers are
    /// expected to emit this tag only for codes that have already been probed (see
    /// GuiDialogPinMatrix.IconDrawable), and the catch here is the second line of defence: a missing
    /// glyph leaves a gap, never an exception out of a GUI compose.
    /// </summary>
    public class IconGlyphComponent : RichTextComponentBase
    {
        /// <summary>Namespaced: <see cref="VtmlUtil.TagConverters"/> is a process-wide table shared with every other mod.</summary>
        public const string TagName = "pmicon";

        static readonly double[] White = { 1, 1, 1, 1 };

        readonly string code;
        readonly double size, ascent;

        public IconGlyphComponent(ICoreClientAPI capi, string code, CairoFont font) : base(capi)
        {
            this.code = code ?? "";
            double em = GuiElement.scaled(font.UnscaledFontsize);
            size = em * 1.15;
            ascent = font.GetFontExtents().Ascent;
            PaddingRight = 3;
            BoundsPerLine = new[] { new LineRectangled(0, 0, size, size) { Ascent = ascent } };
        }

        /// <summary>Idempotent for the same reason <see cref="ColorSwatchComponent.EnsureTagRegistered"/> is.</summary>
        public static void EnsureTagRegistered()
        {
            if (VtmlUtil.TagConverters.ContainsKey(TagName)) return;
            VtmlUtil.TagConverters[TagName] = (capi, token, fontStack, didClickLink) =>
            {
                token.Attributes.TryGetValue("code", out string code);
                return new IconGlyphComponent(capi, code, fontStack.Peek());
            };
        }

        public override void ComposeElements(Context ctx, ImageSurface surface)
        {
            var b = BoundsPerLine[0];
            double y = b.Y + ascent - size;

            ctx.Save();
            try
            {
                api.Gui.Icons.DrawIcon(ctx, "wp" + code.UcFirst(), b.X, y, size, size, White);
            }
            catch (Exception)
            {
                // Deliberately silent: this runs per entry per compose of a dropdown that may hold
                // dozens, so a warning here would be a log flood, and the code itself is already in
                // the label text beside the gap.
            }
            finally { ctx.Restore(); }
        }

        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
        {
            var section = GetCurrentFlowPathSection(flowPath, lineY);
            offsetX += GuiElement.scaled(PaddingLeft);
            bool wrap = section != null && offsetX + size > section.X2;

            var b = BoundsPerLine[0];
            b.X = wrap ? 0 : offsetX;
            b.Y = lineY + (wrap ? currentLineHeight : 0);
            nextOffsetX = b.X + size + GuiElement.scaled(PaddingRight);

            return wrap ? EnumCalcBoundsResult.Nextline : EnumCalcBoundsResult.Continue;
        }
    }
}
