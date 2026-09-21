using System.Text.Json;

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

internal static class PresetStorage
{
    internal const int UiSlotCount = 6;

    internal static void Validate(PresetFile data)
    {
        if (data == null || data.SchemaVersion < 1 || data.SchemaVersion > 9)
            throw new InvalidDataException("Unsupported schema or empty preset file.");
        ValidateContent(data.SchemaVersion, data.CurrentAppearance, data.Presets);
        // A damaged embedded recovery snapshot must not invalidate an otherwise
        // healthy primary state. It will simply be replaced on the next save.
        if (data.PreviousValidState != null)
            try { ValidateContent(data.SchemaVersion, data.PreviousValidState.CurrentAppearance, data.PreviousValidState.Presets); }
            catch { data.PreviousValidState = null; }
    }

    private static void ValidateContent(int schemaVersion, VisualPreset currentAppearance,
        List<VisualPreset> presets)
    {
        if (presets == null || presets.Count == 0)
            throw new InvalidDataException("Unsupported schema or empty preset file.");
        var uiSlots = new HashSet<int>();
        foreach (var preset in presets.Concat(currentAppearance == null
                     ? Enumerable.Empty<VisualPreset>() : new[] { currentAppearance }))
        {
            // The in-game slot is the identity. Names are display labels and may
            // be repeated across different slots, such as seasonal variants.
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 60 || preset.Slots == null)
                throw new InvalidDataException("Invalid preset name.");
            if (!ReferenceEquals(preset, currentAppearance) && preset.UiSlotIndex.HasValue &&
                (schemaVersion < 6 || preset.UiSlotIndex.Value < 1 ||
                 preset.UiSlotIndex.Value > (schemaVersion >= 7 ? UiSlotCount : 3) ||
                 !uiSlots.Add(preset.UiSlotIndex.Value)))
                throw new InvalidDataException("Invalid or duplicate in-game preset slot.");
            if (ReferenceEquals(preset, currentAppearance) && preset.UiSlotIndex.HasValue)
                throw new InvalidDataException("Current appearance cannot occupy a named preset slot.");
            var slots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in preset.Slots)
                if (slot == null || slot.Index < 0 || !slots.Add(slot.Category + ":" + slot.Index) ||
                    (slot.Category == "OrnamentS" ? slot.Index > 3 :
                     slot.Category == "Tent" ? schemaVersion < 2 || slot.Index != 0 :
                     slot.Category == "OrnamentL" ? schemaVersion < 3 || slot.Index > 2 :
                     slot.Category == "Shelf" ? schemaVersion < 4 || slot.Index > 2 : true) ||
                    (slot.IsReplacement && (slot.ItemId == 0 || string.IsNullOrWhiteSpace(slot.ModelName))) ||
                    (!slot.IsReplacement && !slot.IsActual && !slot.IsHidden) ||
                    (!slot.IsReplacement && schemaVersion < 5))
                    throw new InvalidDataException("Invalid slot identity.");
        }
    }

    internal static PresetFile Load(string path)
    {
        try
        {
            var primary = LoadFile(path);
            Plugin.Emit("AppearanceDataLoaded", new { source = "primary", path });
            return primary;
        }
        catch (Exception primaryError)
        {
            string backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            try
            {
                var recovered = LoadFile(backup);
                Plugin.Emit("AppearanceDataRecovered", new
                {
                    source = "external-backup",
                    path = backup,
                    primaryError = primaryError.Message
                });
                return recovered;
            }
            catch (Exception backupError)
            {
                throw new InvalidDataException(
                    $"Primary and backup data are unreadable. Primary: {primaryError.Message}; Backup: {backupError.Message}",
                    backupError);
            }
        }
    }

    private static PresetFile LoadFile(string path)
    {
        var data = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path));
        try { Validate(data); }
        catch when (data?.PreviousValidState != null)
        {
            var recovered = new PresetFile
            {
                SchemaVersion = Math.Clamp(data.SchemaVersion, 5, 9),
                CurrentAppearance = data.PreviousValidState.CurrentAppearance?.Copy(),
                Presets = data.PreviousValidState.Presets?.Select(p => p.Copy()).ToList()
            };
            Validate(recovered);
            Plugin.Emit("AppearanceDataRecovered", new { source = "embedded-previous-state", path });
            data = recovered;
        }
        data.SchemaVersion = 9;
        return data;
    }

    internal static void Save(string path, PresetFile data)
    {
        data.SchemaVersion = 9;
        PresetFile previous = null;
        if (File.Exists(path))
            try { previous = LoadFile(path); }
            catch { }
        data.PreviousValidState = previous == null ? null : new PresetSnapshot
        {
            CurrentAppearance = previous.CurrentAppearance?.Copy(),
            Presets = previous.Presets.Select(p => p.Copy()).ToList()
        };
        Validate(data);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        // Verify the complete serialized file before it can replace either the
        // primary data or the last known-good external backup.
        var verification = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(temporary));
        Validate(verification);
        if (previous != null) File.Copy(path, path + ".bak", true);
        File.Move(temporary, path, true);
    }
}
