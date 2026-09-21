using System.Text.Json;

namespace BazaarDecorTransmog;

internal static class PresetStorage
{
    // The host owns logging so storage can also run without Unity/BepInEx.
    internal static Action<string, object> Report { get; set; }
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
        => LoadRecoverable(path, out _);

    private static void ReportSafely(string kind, object data)
    {
        // Diagnostics must never turn a successful read/commit into a failure.
        try { Report?.Invoke(kind, data); } catch { }
    }

    private static void RejectFutureSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(nameof(PresetFile.SchemaVersion), out var schema) &&
            schema.ValueKind == JsonValueKind.Number && schema.TryGetInt64(out long version) && version > 9)
            throw new NotSupportedException("This appearance data was written by a newer Mod version.");
    }

    private static PresetFile LoadRecoverable(string path, out bool healthyPrimary)
    {
        healthyPrimary = false;
        try
        {
            var primary = LoadFile(path, out healthyPrimary);
            ReportSafely("AppearanceDataLoaded", new { source = "primary", path });
            return primary;
        }
        catch (Exception primaryError) when (primaryError is not NotSupportedException)
        {
            string backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            try
            {
                var recovered = LoadFile(backup, out _);
                ReportSafely("AppearanceDataRecovered", new
                {
                    source = "external-backup",
                    path = backup,
                    primaryError = primaryError.Message
                });
                return recovered;
            }
            catch (Exception backupError) when (backupError is not NotSupportedException)
            {
                throw new InvalidDataException(
                    $"Primary and backup data are unreadable. Primary: {primaryError.Message}; Backup: {backupError.Message}",
                    backupError);
            }
        }
    }

    private static PresetFile LoadFile(string path, out bool healthy)
    {
        healthy = false;
        string json = File.ReadAllText(path);
        // Inspect the version before deserializing fields whose shape may have changed.
        // A future schema is not corruption and must never fall back to older data.
        RejectFutureSchema(json);
        var data = JsonSerializer.Deserialize<PresetFile>(json);
        try { Validate(data); }
        catch (InvalidDataException) when (data?.PreviousValidState != null && data.SchemaVersion >= 1)
        {
            var recovered = new PresetFile
            {
                SchemaVersion = data.SchemaVersion,
                CurrentAppearance = data.PreviousValidState.CurrentAppearance?.Copy(),
                Presets = data.PreviousValidState.Presets?.Select(p => p.Copy()).ToList()
            };
            Validate(recovered);
            ReportSafely("AppearanceDataRecovered", new { source = "embedded-previous-state", path });
            recovered.SchemaVersion = 9;
            return recovered;
        }
        healthy = true;
        data.SchemaVersion = 9;
        return data;
    }

    internal static void Save(string path, PresetFile data)
    {
        // Validate a detached candidate so failed saves do not advance in-memory
        // schema/snapshot state. The caller commits its new state only on success.
        var candidate = JsonSerializer.Deserialize<PresetFile>(JsonSerializer.Serialize(data));
        Validate(candidate);
        candidate.SchemaVersion = 9;
        PresetFile previous = null;
        bool healthyPrimary = false;
        bool primaryExists = File.Exists(path);
        if (primaryExists || File.Exists(path + ".bak"))
            previous = LoadRecoverable(path, out healthyPrimary);
        if (healthyPrimary && File.Exists(path + ".bak"))
        {
            // Rotating a backup is also a write: preserve a newer version there
            // even when the primary happens to contain older, readable data.
            try { RejectFutureSchema(File.ReadAllText(path + ".bak")); }
            catch (JsonException) { } // A malformed old backup may be replaced.
        }
        candidate.PreviousValidState = previous == null ? null : new PresetSnapshot
        {
            CurrentAppearance = previous.CurrentAppearance?.Copy(),
            Presets = previous.Presets.Select(p => p.Copy()).ToList()
        };
        Validate(candidate);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(candidate, new JsonSerializerOptions { WriteIndented = true }));
            // Verify the complete serialized file before it can replace either the
            // primary data or the last known-good external backup.
            var verification = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(temporary));
            Validate(verification);
            // Replacement and backup creation are a single filesystem operation.
            // Never rotate damaged primary bytes over a known-good recovery file.
            if (primaryExists) File.Replace(temporary, path, healthyPrimary ? path + ".bak" : null);
            else File.Move(temporary, path);
            data.SchemaVersion = candidate.SchemaVersion;
            data.PreviousValidState = candidate.PreviousValidState;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
