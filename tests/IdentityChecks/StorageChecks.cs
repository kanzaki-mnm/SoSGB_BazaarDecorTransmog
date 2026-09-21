using BazaarDecorTransmog;
using System.Text.Json;

internal static class StorageChecks
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        void Throws<T>(Action action, string name) where T : Exception
        {
            bool thrown = false;
            try { action(); } catch (T) { thrown = true; }
            Check(thrown, name);
        }
        VisualPreset Appearance(string mode = "Hidden") => new()
        {
            Name = "Current", Slots = new() { new VisualSlot { Category = "Tent", Mode = mode } }
        };
        PresetFile State() => new()
        {
            CurrentAppearance = Appearance(),
            Presets = Enumerable.Range(1, 6).Select(i => new VisualPreset
                { Name = "同じ名前", UiSlotIndex = i }).ToList()
        };
        string directory = Path.Combine(Path.GetTempPath(), "BDT-StorageChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "BazaarDecorTransmog.cfg");
        var events = new List<string>();
        PresetStorage.Report = (kind, payload) => events.Add(kind + ":" + JsonSerializer.Serialize(payload));
        try
        {
            PresetStorage.Save(path, State());
            var loaded = PresetStorage.Load(path);
            Check(loaded.SchemaVersion == 9 && loaded.Presets.Select(p => p.UiSlotIndex).SequenceEqual(
                Enumerable.Range(1, 6).Select(i => (int?)i)) && loaded.Presets.All(p => p.Name == "同じ名前"),
                "six same-name presets retain distinct slot identities");
            Check(loaded.CurrentAppearance.Slots.Single().IsHidden && loaded.CurrentAppearance.UiSlotIndex == null,
                "current appearance persists independently of named slots");
            Check(events.Any(e => e.StartsWith("AppearanceDataLoaded:")), "host receives load diagnostics");
            var copy = loaded.CurrentAppearance.Copy();
            copy.Slots[0].Mode = "Actual";
            Check(loaded.CurrentAppearance.Slots[0].IsHidden, "current appearance copy is independent");
            loaded.CurrentAppearance = copy;
            PresetStorage.Save(path, loaded);
            loaded = PresetStorage.Load(path);
            Check(loaded.CurrentAppearance.Slots[0].IsActual && loaded.PreviousValidState.CurrentAppearance.Slots[0].IsHidden &&
                loaded.PreviousValidState.Presets.Count == 6, "previous normal state includes current appearance and presets");
            string valid = File.ReadAllText(path);
            string backup = File.ReadAllText(path + ".bak");
            var invalid = State(); invalid.Presets[5].UiSlotIndex = 1;
            Throws<InvalidDataException>(() => PresetStorage.Save(path, invalid), "duplicate UI slot save rejected");
            Check(File.ReadAllText(path) == valid && File.ReadAllText(path + ".bak") == backup,
                "validation failure preserves primary and backup bytes");
            // A directory at the staging path deterministically prevents file creation on Windows and Unix.
            Directory.CreateDirectory(path + ".tmp");
            try
            {
                Throws<UnauthorizedAccessException>(() => PresetStorage.Save(path, State()), "staging write failure propagates");
                Check(File.ReadAllText(path) == valid && File.ReadAllText(path + ".bak") == backup,
                    "staging write failure preserves primary and backup bytes");
            }
            finally { Directory.Delete(path + ".tmp"); }

            loaded.Presets[0].Name = "";
            File.WriteAllText(path, JsonSerializer.Serialize(loaded));
            string damaged = File.ReadAllText(path);
            var recovered = PresetStorage.Load(path);
            Check(recovered.CurrentAppearance.Slots[0].IsHidden && recovered.Presets.Count == 6 &&
                File.ReadAllText(path) == damaged, "embedded previous state recovers invalid content without rewriting file");
            Check(events.Any(e => e.Contains("embedded-previous-state")), "embedded recovery is reported");

            var healthy = State();
            healthy.PreviousValidState = new PresetSnapshot { Presets = new() };
            File.WriteAllText(path, JsonSerializer.Serialize(healthy));
            Check(PresetStorage.Load(path).PreviousValidState == null, "invalid embedded snapshot does not discard healthy primary");
            File.WriteAllText(path, "{broken");
            Check(PresetStorage.Load(path).CurrentAppearance.Slots[0].IsHidden, "unparseable primary uses external backup");
            Check(events.Any(e => e.Contains("external-backup")), "external recovery is reported");
            File.WriteAllText(path + ".bak", "{also broken");
            Throws<InvalidDataException>(() => PresetStorage.Load(path), "both corrupt files fail visibly");
            Check(File.ReadAllText(path) == "{broken" && File.ReadAllText(path + ".bak") == "{also broken",
                "failed recovery preserves evidence");
            File.Delete(path + ".bak");
            File.WriteAllText(path, JsonSerializer.Serialize(new PresetFile { SchemaVersion = 10, Presets = State().Presets }));
            Throws<InvalidDataException>(() => PresetStorage.Load(path), "future schema without recovery data rejected on load");

            for (int schema = 1; schema <= 9; schema++)
            {
                var legacy = new PresetFile { SchemaVersion = schema, Presets = new() { new VisualPreset { Name = "Legacy" } } };
                string json = JsonSerializer.Serialize(legacy);
                File.WriteAllText(path, json);
                Check(PresetStorage.Load(path).SchemaVersion == 9 && File.ReadAllText(path) == json,
                    $"schema {schema} accepted without rewriting source");
            }
            File.WriteAllText(path, "{\"SchemaVersion\":9,\"ObsoleteSetting\":true,\"Presets\":[{\"Name\":\"Legacy\",\"OldField\":42,\"Slots\":[]}]}");
            Check(PresetStorage.Load(path).Presets[0].Name == "Legacy", "unknown old properties are ignored");
            var currentOnly = new PresetFile { CurrentAppearance = Appearance(), Presets = new() { new VisualPreset() } };
            PresetStorage.Save(path, currentOnly);
            Check(PresetStorage.Load(path).CurrentAppearance.Slots[0].IsHidden &&
                PresetStorage.Load(path).Presets.All(p => p.UiSlotIndex == null),
                "current appearance persists with no UI presets saved");
            var badCurrent = State(); badCurrent.CurrentAppearance.UiSlotIndex = 1;
            Throws<InvalidDataException>(() => PresetStorage.Validate(badCurrent), "current appearance cannot own UI slot");
            foreach (int index in new[] { 0, 7 })
            {
                var badSlot = State(); badSlot.Presets[0].UiSlotIndex = index;
                Throws<InvalidDataException>(() => PresetStorage.Validate(badSlot), $"UI slot {index} rejected");
            }
        }
        finally
        {
            PresetStorage.Report = null;
            // Only files generated inside this unique test directory are removed.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        return passed;
    }
}
