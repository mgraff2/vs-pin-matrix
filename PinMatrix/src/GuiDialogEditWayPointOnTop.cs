using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// The vanilla waypoint editor, raised above the Pin Matrix dialog. Both draw at 0.2, and
    /// a same-order tie always resolves against a freshly spawned editor: the matrix table
    /// handles its row buttons on mouse-down, so right after the editor opens, the GuiManager
    /// re-focuses the click-handling dialog (the matrix) — and RequestFocus re-raises it to the
    /// front of its DrawOrder group, burying the editor (verified against 1.22.6 VintagestoryLib
    /// GuiManager.OnMouseDown/RequestFocus). A strictly higher DrawOrder puts the editor in its
    /// own group: always drawn above the matrix, immune to the matrix's re-raise, and — because
    /// ClientMain.RegisterDialog orders same-InputOrder dialogs by descending DrawOrder — it
    /// also receives mouse events first where the two windows overlap.
    /// </summary>
    public class GuiDialogEditWayPointOnTop : GuiDialogEditWayPoint
    {
        public GuiDialogEditWayPointOnTop(ICoreClientAPI capi, WaypointMapLayer layer, Waypoint waypoint, int index)
            : base(capi, layer, waypoint, index)
        {
        }

        public override double DrawOrder => 0.25;
    }
}
