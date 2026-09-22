namespace BazaarDecorTransmog;

public sealed class VisualSlot
{
    public int Index { get; set; }
    public uint ItemId { get; set; }
    public string Category { get; set; } = "OrnamentS";
    public string ModelName { get; set; } = "";
    // Replacement is the original transmog behavior. Actual and Hidden intentionally
    // carry no item identity: they are display choices, not catalogue entries.
    public string Mode { get; set; } = "Replacement";
    public bool IsReplacement => string.IsNullOrEmpty(Mode) || Mode == "Replacement";
    public bool IsActual => Mode == "Actual";
    public bool IsHidden => Mode == "Hidden";
}

public sealed class VisualPreset
{
    public string Name { get; set; } = "Default";
    // Null is a legacy/debug-panel preset. The in-game menu owns slots 1-6.
    public int? UiSlotIndex { get; set; }
    public List<VisualSlot> Slots { get; set; } = new();
    public VisualPreset Copy() => new() { Name = Name, UiSlotIndex = UiSlotIndex, Slots = Slots.Select(s => new VisualSlot
        { Index = s.Index, ItemId = s.ItemId, Category = s.Category, ModelName = s.ModelName, Mode = s.Mode }).ToList() };
}

public sealed class PresetFile
{
    public int SchemaVersion { get; set; } = 9;
    public VisualPreset CurrentAppearance { get; set; }
    public List<VisualPreset> Presets { get; set; } = new();
    public PresetSnapshot PreviousValidState { get; set; }
}

public sealed class PresetSnapshot
{
    public VisualPreset CurrentAppearance { get; set; }
    public List<VisualPreset> Presets { get; set; } = new();
}
