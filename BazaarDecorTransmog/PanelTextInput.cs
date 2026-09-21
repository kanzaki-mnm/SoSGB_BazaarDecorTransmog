using UnityEngine;

namespace BazaarDecorTransmog;

// GUI.TextField relies on stripped GUIStateObjects/TextEditor methods in this game.
// Keep editing state in managed code and draw using the already working GUI.Button.
internal static class PanelTextInput
{
    private static string focused;
    private static bool selectAll;

    internal static void Blur() { focused = null; selectAll = false; }

    internal static string Draw(string id, Rect area, string value, int maxLength)
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && !area.Contains(e.mousePosition) && focused == id) Blur();
        if (PanelControls.Button(area, value + (focused == id ? " |" : "")))
        {
            focused = id;
            selectAll = false;
        }
        if (focused != id || e.type != EventType.KeyDown) return value;
        bool command = e.control || e.command;
        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape)
            Blur();
        else if (command && e.keyCode == KeyCode.A) selectAll = true;
        else if (command && e.keyCode == KeyCode.V)
        {
            // Clipboard retrieval is optional; its failure must not interrupt the panel.
            string pasted = "";
            Plugin.Guard("panel-paste", () => pasted = GUIUtility.systemCopyBuffer ?? "");
            if (pasted.Length > 0)
            {
                value = Append(selectAll ? "" : value, pasted, maxLength);
                selectAll = false;
            }
        }
        else if (e.keyCode == KeyCode.Backspace || e.keyCode == KeyCode.Delete)
        {
            if (selectAll) value = "";
            else if (value.Length > 0)
            {
                int remove = value.Length >= 2 && char.IsSurrogatePair(value, value.Length - 2) ? 2 : 1;
                value = value.Substring(0, value.Length - remove);
            }
            selectAll = false;
        }
        else if (!command && !char.IsControl(e.character))
        {
            value = Append(selectAll ? "" : value, e.character.ToString(), maxLength);
            selectAll = false;
        }
        e.Use();
        return value;
    }

    internal static string Paste(string value, int limit)
    {
        string text = null;
        Plugin.Guard("panel-paste", () => text = GUIUtility.systemCopyBuffer);
        return string.IsNullOrEmpty(text) ? value : Append("", text, limit);
    }

    private static string Append(string value, string input, int limit)
    {
        string next = value + new string(input.Where(c => !char.IsControl(c)).ToArray());
        if (next.Length <= limit) return next;
        int end = limit;
        if (end > 0 && char.IsHighSurrogate(next[end - 1])) end--;
        return next.Substring(0, end);
    }
}
