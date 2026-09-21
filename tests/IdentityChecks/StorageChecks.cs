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
            PresetStorage.Save(path, recovered);
            Check(File.ReadAllText(path + ".bak") == backup &&
                PresetStorage.Load(path).PreviousValidState.Presets.Count == 6,
                "saving embedded recovery preserves good external backup and recovered snapshot");

            var healthy = State();
            healthy.PreviousValidState = new PresetSnapshot { Presets = new() };
            File.WriteAllText(path, JsonSerializer.Serialize(healthy));
            Check(PresetStorage.Load(path).PreviousValidState == null, "invalid embedded snapshot does not discard healthy primary");
            File.WriteAllText(path, "{broken");
            Check(PresetStorage.Load(path).CurrentAppearance.Slots[0].IsHidden, "unparseable primary uses external backup");
            Check(events.Any(e => e.Contains("external-backup")), "external recovery is reported");
            PresetStorage.Save(path, State());
            Check(File.ReadAllText(path + ".bak") == backup, "saving external recovery never backs up corrupt primary");
            File.WriteAllText(path, "{broken");
            File.WriteAllText(path + ".bak", "{also broken");
            Throws<InvalidDataException>(() => PresetStorage.Load(path), "both corrupt files fail visibly");
            Check(File.ReadAllText(path) == "{broken" && File.ReadAllText(path + ".bak") == "{also broken",
                "failed recovery preserves evidence");
            Throws<InvalidDataException>(() => PresetStorage.Save(path, State()), "unrecoverable existing data blocks save");
            File.Delete(path + ".bak");
            File.WriteAllText(path, JsonSerializer.Serialize(new PresetFile { SchemaVersion = 10, Presets = State().Presets }));
            Throws<NotSupportedException>(() => PresetStorage.Load(path), "future schema without recovery data rejected on load");
            File.WriteAllText(path + ".bak", backup);
            foreach (string future in new[] {
                JsonSerializer.Serialize(new PresetFile { SchemaVersion = 10, Presets = State().Presets,
                    PreviousValidState = new PresetSnapshot { Presets = State().Presets } }),
                "{\"SchemaVersion\":10,\"Presets\":\"new-shape\"}" })
            {
                File.WriteAllText(path, future);
                Throws<NotSupportedException>(() => PresetStorage.Load(path), "future data cannot recover older snapshot or backup");
                Throws<NotSupportedException>(() => PresetStorage.Save(path, State()), "future target cannot be overwritten");
                Check(File.ReadAllText(path) == future && File.ReadAllText(path + ".bak") == backup,
                    "future data and backup remain byte-identical");
            }
            File.WriteAllText(path, valid);
            string futureBackup = "{\"SchemaVersion\":10,\"Presets\":\"future\"}";
            File.WriteAllText(path + ".bak", futureBackup);
            Throws<NotSupportedException>(() => PresetStorage.Save(path, State()), "future backup cannot be rotated away");
            Check(File.ReadAllText(path) == valid && File.ReadAllText(path + ".bak") == futureBackup,
                "future backup refusal preserves both files");
            File.WriteAllText(path + ".bak", backup);
            var futureInput = State(); futureInput.SchemaVersion = 10;
            Throws<InvalidDataException>(() => PresetStorage.Save(path, futureInput), "future input rejected before normalization");
            Check(futureInput.SchemaVersion == 10 && File.ReadAllText(path) == valid, "failed save does not mutate input schema");
            using (var lockedBackup = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<IOException>(() => PresetStorage.Save(path, State()), "locked backup rejects atomic commit");
            Check(File.ReadAllText(path) == valid && File.ReadAllText(path + ".bak") == backup,
                "backup replacement failure preserves primary and backup");
            var failedInput = State(); failedInput.SchemaVersion = 7;
            // A real sharing violation exercises the commit failure, after staging succeeds.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<IOException>(() => PresetStorage.Save(path, failedInput), "locked primary rejects atomic commit");
            Check(File.ReadAllText(path) == valid && File.ReadAllText(path + ".bak") == backup &&
                failedInput.SchemaVersion == 7 && failedInput.PreviousValidState == null && !File.Exists(path + ".tmp"),
                "commit failure preserves primary backup caller metadata and cleans staging");
            File.Delete(path);
            Check(PresetStorage.Load(path).Presets.Count == 6, "missing primary recovers backup");
            PresetStorage.Save(path, State());
            Check(File.ReadAllText(path + ".bak") == backup, "creating primary after recovery retains backup");
            PresetStorage.Report = (_, _) => throw new Exception("logger unavailable");
            Check(PresetStorage.Load(path).Presets.Count == 6, "diagnostic failure does not invalidate healthy data");

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
            var restored = new List<int>();
            AggregateException failures = null;
            try
            {
                Recovery.Run(() => { restored.Add(1); throw new IOException("renderer lost"); },
                    () => restored.Add(2),
                    () => Recovery.Run(() => { restored.Add(3); throw new IOException("mesh lost"); },
                        () => restored.Add(4)), () => restored.Add(5));
            }
            catch (AggregateException ex) { failures = ex; }
            Check(restored.SequenceEqual(new[] { 1, 2, 3, 4, 5 }) && failures?.Flatten().InnerExceptions.Count == 2,
                "independent and nested restoration continues after faults and reports both failures");
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
