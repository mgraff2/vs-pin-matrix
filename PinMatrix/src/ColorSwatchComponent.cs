using System.Globalization;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace PinMatrix
{
    /// <summary>
    /// VTML component that paints a solid colour chip inline with the text:
    /// <c>&lt;pmswatch color="#rrggbb"/&gt;</c>.
    ///
    /// Dropdown entry labels are run through <see cref="VtmlUtil"/> before being drawn, so a
    /// custom tag is the only way to get a real colour into a list menu. The built-in tags can
    /// only tint *text*, which would mean relying on the player's font having a block glyph —
    /// a drawn rectangle always works.
    /// </summary>
    public class ColorSwatchComponent : RichTextComponentBase
    {
        /// <summary>Namespaced: <see cref="VtmlUtil.TagConverters"/> is a process-wide table shared with every other mod.</summary>
        public const string TagName = "pmswatch";

        readonly double[] rgb;
        readonly double w, h, ascent;

        public ColorSwatchComponent(ICoreClientAPI capi, double[] rgb, CairoFont font) : base(capi)
        {
            this.rgb = rgb;
            double em = GuiElement.scaled(font.UnscaledFontsize);
            w = em * 1.9;
            h = em * 0.85;
            var fe = font.GetFontExtents();
            ascent = fe.Ascent;
            PaddingRight = 3;
            BoundsPerLine = new[] { new LineRectangled(0, 0, w, h) { Ascent = ascent } };
        }

        /// <summary>
        /// Idempotent, and deliberately keyed off the table rather than a one-shot flag:
        /// <c>ClientMain.Dispose()</c> calls <c>VtmlUtil.TagConverters.Clear()</c> when you leave a
        /// world, but static state in this assembly outlives that — so a "have I registered?" bool
        /// would report yes forever and leave the tag missing on every world after the first.
        /// Cheap enough to re-assert wherever the tag is about to be used.
        /// </summary>
        public static void EnsureTagRegistered()
        {
            if (VtmlUtil.TagConverters.ContainsKey(TagName)) return;
            VtmlUtil.TagConverters[TagName] = (capi, token, fontStack, didClickLink) =>
            {
                token.Attributes.TryGetValue("color", out string hex);
                return new ColorSwatchComponent(capi, ParseHex(hex), fontStack.Peek());
            };
        }

        /// <summary>"#rrggbb" (as produced by WpCommands.ColorHex) to Cairo RGB; white on anything unparseable.</summary>
        public static double[] ParseHex(string hex)
        {
            hex = (hex ?? "").TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            {
                return new double[] { 1, 1, 1 };
            }
            return new[] { ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0 };
        }

        public override void ComposeElements(Context ctx, ImageSurface surface)
        {
            var b = BoundsPerLine[0];
            // bottom edge just above the baseline, so the chip reads as a glyph on the text line
            double y = b.Y + ascent - h - GuiElement.scaled(1);

            GuiElement.RoundRectangle(ctx, b.X, y, w, h, 1.0);
            ctx.SetSourceRGBA(rgb[0], rgb[1], rgb[2], 1.0);
            ctx.FillPreserve();
            // outline keeps near-black and near-white chips distinguishable from the menu background
            ctx.SetSourceRGBA(0, 0, 0, 0.6);
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.Stroke();
        }

        public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
        {
            var section = GetCurrentFlowPathSection(flowPath, lineY);
            offsetX += GuiElement.scaled(PaddingLeft);
            bool wrap = section != null && offsetX + w > section.X2;

            var b = BoundsPerLine[0];
            b.X = wrap ? 0 : offsetX;
            b.Y = lineY + (wrap ? currentLineHeight : 0);
            nextOffsetX = b.X + w + GuiElement.scaled(PaddingRight);

            return wrap ? EnumCalcBoundsResult.Nextline : EnumCalcBoundsResult.Continue;
        }
    }
}
