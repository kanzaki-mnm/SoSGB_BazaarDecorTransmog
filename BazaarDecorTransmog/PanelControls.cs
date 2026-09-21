using UnityEngine;

namespace BazaarDecorTransmog;

internal static class PanelControls
{
    // Avoid native IMGUI hot-control/state bookkeeping. Each action is dispatched
    // once on MouseDown, directly against the same rectangle used for drawing.
    internal static bool Button(Rect area, string label)
    {
        GUI.Box(area, label);
        var e = Event.current;
        if (!GUI.enabled || e.type != EventType.MouseDown || e.button != 0 || !area.Contains(e.mousePosition)) return false;
        e.Use();
        Plugin.Emit("PanelClick", new { control = label });
        return true;
    }
}
