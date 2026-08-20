using System;

namespace PinMatrix
{
    /// <summary>
    /// A rectangle in *unscaled* GUI units, i.e. pixels divided by RuntimeEnv.GUIScale.
    ///
    /// Everything the layout system stores or compares is unscaled on purpose. ElementBounds.fixedX
    /// / fixedY are unscaled too (absX = fixedX * GUIScale), so working in these units means a
    /// GUI-scale change is a pure re-derivation rather than a repair.
    /// </summary>
    public struct URect
    {
        public double X, Y, W, H;

        public URect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }

        public double Right => X + W;
        public double Bottom => Y + H;
        public double Area => Math.Max(0, W) * Math.Max(0, H);

        /// <summary>Area of the intersection with <paramref name="o"/>; 0 when they don't overlap.</summary>
        public double OverlapArea(URect o)
        {
            double w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
            double h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
            return (w <= 0 || h <= 0) ? 0 : w * h;
        }

        public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

        /// <summary>True when the two rects are the same to within <paramref name="tol"/> on every edge.</summary>
        public bool Approximately(URect o, double tol) =>
            Math.Abs(X - o.X) <= tol && Math.Abs(Y - o.Y) <= tol &&
            Math.Abs(W - o.W) <= tol && Math.Abs(H - o.H) <= tol;

        public override string ToString() =>
            $"{X:0},{Y:0} {W:0}x{H:0}";
    }

    /// <summary>
    /// The snap grid: the screen divided into Cols x Rows cells.
    ///
    /// Cells are addressed by (col,row) and only resolved to a rectangle at the moment of use.
    /// That is the whole point — assignments are persisted as cell indices, never as coordinates,
    /// so a resolution change, a monitor swap or a GUI-scale change re-derives correct geometry
    /// instead of stranding windows at coordinates that meant something on different hardware.
    /// </summary>
    public class ZoneGrid
    {
        // Free choice up to 50 each way — 3x3 or 50x50, whatever the player picks, split evenly.
        // A fine lattice is a positioning grid rather than a set of drop targets, and the overlay
        // draws it as lines rather than one quad per cell (102 lines at the maximum, not 2500 quads),
        // so the count costs nothing to render. The blocked-cell set is sparse for the same reason.
        public const int MaxCols = 50;
        public const int MaxRows = 50;

        public readonly int Cols, Rows;
        /// <summary>Screen size in unscaled units.</summary>
        public readonly double ScreenW, ScreenH;
        readonly double pad;

        public ZoneGrid(int cols, int rows, double screenWUnscaled, double screenHUnscaled, double padding)
        {
            Cols = Clamp(cols, 1, MaxCols);
            Rows = Clamp(rows, 1, MaxRows);
            ScreenW = Math.Max(1, screenWUnscaled);
            ScreenH = Math.Max(1, screenHUnscaled);
            pad = Math.Max(0, padding);
        }

        public double CellW => ScreenW / Cols;
        public double CellH => ScreenH / Rows;
        public int CellCount => Cols * Rows;

        /// <summary>Flat index for a cell, used as the key of the blocked-cell set.</summary>
        public int Index(int col, int row) => row * Cols + col;

        /// <summary>The usable rectangle of a (possibly multi-cell) zone, inset by the configured padding.</summary>
        public URect Cell(int col, int row, int colSpan = 1, int rowSpan = 1)
        {
            col = Clamp(col, 0, Cols - 1);
            row = Clamp(row, 0, Rows - 1);
            colSpan = Clamp(colSpan, 1, Cols - col);
            rowSpan = Clamp(rowSpan, 1, Rows - row);

            return new URect(
                col * CellW + pad,
                row * CellH + pad,
                colSpan * CellW - 2 * pad,
                rowSpan * CellH - 2 * pad);
        }

        /// <summary>Which cell contains the given unscaled point. False when off-grid.</summary>
        public bool CellAt(double ux, double uy, out int col, out int row)
        {
            col = (int)(ux / CellW);
            row = (int)(uy / CellH);
            if (col < 0 || row < 0 || col >= Cols || row >= Rows) { col = row = -1; return false; }
            return true;
        }

        public bool SameShape(ZoneGrid o) =>
            o != null && o.Cols == Cols && o.Rows == Rows &&
            Math.Abs(o.ScreenW - ScreenW) < 0.5 && Math.Abs(o.ScreenH - ScreenH) < 0.5;

        static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
