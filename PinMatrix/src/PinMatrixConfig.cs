using System;

namespace PinMatrix
{
    public class PinMatrixConfig
    {
        /// <summary>Max entries kept in the recycle bin; oldest pruned beyond this.</summary>
        public int RecycleBinMaxEntries { get; set; } = 500;

        /// <summary>Redundant with the recycle bin; exports a full backup before every mutating bulk op.</summary>
        public bool AutoBackupBeforeBulkOps { get; set; } = false;

        /// <summary>How many auto-backup files to keep.</summary>
        public int BackupRetentionCount { get; set; } = 20;

        /// <summary>Per-command delay in ms during bulk operations. 0 = burst (fine for singleplayer/local).</summary>
        public int BulkOpDelayMs { get; set; } = 30;

        /// <summary>Warn on the confirmation page if a bulk pin would leave more than this many waypoints pinned.</summary>
        public int PinnedWarnThreshold { get; set; } = 20;

        /// <summary>Show the "Redraw map" utility button (invokes the vanilla client-side ".map redraw" command).</summary>
        public bool EnableMapRefresh { get; set; } = false;

        /// <summary>Table rows per page (5-18; the table area is sized to fit).</summary>
        public int RowsPerPage { get; set; } = 14;

        /// <summary>
        /// Fixed placement for the map-screen button, in unscaled pixels from the right/top edge.
        /// -1 (default) = automatic placement. Set both to pin the button and disable the
        /// automatic overlap-avoidance entirely — the escape hatch when another mod's map HUD
        /// occupies the same corner.
        /// </summary>
        public int MapButtonRightMargin { get; set; } = -1;
        public int MapButtonYOffset { get; set; } = -1;

        /// <summary>
        /// Whether plain "P" on the map screen opens the editor (the button's own shortcut; the
        /// rebindable Settings &gt; Controls hotkey is unaffected). It is already suppressed while
        /// any text field has focus, so it does not eat typing — set this to false only to free
        /// the key up completely.
        /// </summary>
        public bool MapButtonShortcutKey { get; set; } = true;

        public void Clamp()
        {
            RecycleBinMaxEntries = Math.Max(10, RecycleBinMaxEntries);
            BackupRetentionCount = Math.Max(1, BackupRetentionCount);
            BulkOpDelayMs = Math.Min(2000, Math.Max(0, BulkOpDelayMs));
            PinnedWarnThreshold = Math.Max(1, PinnedWarnThreshold);
            if (RowsPerPage <= 0) RowsPerPage = 18;
            RowsPerPage = Math.Min(18, Math.Max(5, RowsPerPage));
            if (MapButtonRightMargin >= 0) MapButtonRightMargin = Math.Min(4000, MapButtonRightMargin);
            if (MapButtonYOffset >= 0) MapButtonYOffset = Math.Min(4000, MapButtonYOffset);
        }
    }
}
