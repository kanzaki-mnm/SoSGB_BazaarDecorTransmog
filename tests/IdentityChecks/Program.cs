using BazaarDecorTransmog;
using System.Text.Json;

int passed = 0;
void Check(string name, uint expected, IEnumerable<VisualIdentity.Part> rows, uint id, string model, string category = "OrnamentS")
{
    uint actual = VisualIdentity.Resolve(rows, id, category, model);
    if (actual != expected) throw new Exception($"{name}: expected {expected}, got {actual}");
    passed++;
    Console.WriteLine("PASS " + name);
}
var data = new[] { new VisualIdentity.Part(10, "OrnamentS", "a"), new VisualIdentity.Part(20, "OrnamentS", "b") };
Check("exact identity", 10, data, 10, "a");
Check("reused ID resolves by model", 20, data, 10, "b");
Check("removed ID resolves by model", 10, data, 99, "a");
Check("missing model cannot use ID alone", 0, data, 10, "missing");
Check("empty identity rejected", 0, data, 10, "");
Check("cross-category rejected", 0, new[] { new VisualIdentity.Part(10, "Shelf", "a") }, 10, "a");
var duplicates = new[] { new VisualIdentity.Part(10, "OrnamentS", "a"), new VisualIdentity.Part(20, "OrnamentS", "a") };
Check("ambiguous migration rejected", 0, duplicates, 99, "a");
Check("exact ID disambiguates duplicate models", 20, duplicates, 20, "a");
if (args.Length > 0)
{
    using var catalog = JsonDocument.Parse(File.ReadAllText(args[0]).TrimStart('\uFEFF'));
    var rows = catalog.RootElement.EnumerateArray().Select(p => new VisualIdentity.Part(
        p.GetProperty("id").GetUInt32(), p.GetProperty("category").GetString(), p.GetProperty("modelName").GetString())).ToArray();
    Check("real 1.5.0 default", 119074, rows, 119074, "fld_cst_032_02");
    Check("real 1.5.0 simulated ID migration", 119074, rows, 999999, "fld_cst_032_02");
    Check("real large-object identity", 119084, rows, 119084, "fld_cst_036_00", "OrnamentL");
    Check("real large-object simulated ID migration", 119084, rows, 999999, "fld_cst_036_00", "OrnamentL");
    Check("real shelf identity", 119051, rows, 119051, "fld_cst_019_02", "Shelf");
    Check("real shelf simulated ID migration", 119051, rows, 999999, "fld_cst_019_02", "Shelf");
}

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    passed++;
    Console.WriteLine("PASS " + name);
}
void Reject(PresetFile file, string name)
{
    bool rejected = false;
    try { PresetStorage.Validate(file); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, name);
}
var original = new VisualPreset { Name = "春のバザール", Slots = Enumerable.Range(0, 4).Select(i =>
    new VisualSlot { Index = i, ItemId = 119074, ModelName = "fld_cst_032_02" }).ToList() };
var draft = original.Copy();
draft.Slots[0].ItemId = 119080;
Assert(original.Slots[0].ItemId == 119074, "draft does not mutate saved identity");
var store = new PresetFile { Presets = new() { original, new VisualPreset { Name = "Vanilla" } } };
string directory = Path.Combine(Path.GetTempPath(), "BDT-PresetChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "presets.json");
try
{
    PresetStorage.Save(path, store);
    var loaded = PresetStorage.Load(path);
    Assert(loaded.Presets[0].Name == "春のバザール" && loaded.Presets[0].Slots.Count == 4 && loaded.Presets[1].Slots.Count == 0,
        "four slots, Unicode names and vanilla preset round trip");
    string first = File.ReadAllText(path);
    loaded.Presets[0].Slots.RemoveAt(0);
    PresetStorage.Save(path, loaded);
    Assert(File.ReadAllText(path + ".bak") == first, "previous valid file backed up");
    Assert(PresetStorage.Load(path).Presets[0].Slots.Count == 3, "overwrite and reload");
    var invalid = new PresetFile { Presets = new() { new VisualPreset { Name = "" } } };
    string valid = File.ReadAllText(path);
    bool saveRejected = false;
    try { PresetStorage.Save(path, invalid); } catch (InvalidDataException) { saveRejected = true; }
    Assert(saveRejected, "invalid save throws");
    Assert(File.ReadAllText(path) == valid, "invalid save preserves existing file");
    var mixed = original.Copy();
    mixed.Slots.Add(new VisualSlot { Category = "Tent", Index = 0, ItemId = 119025, ModelName = "fld_cst_009_02" });
    PresetStorage.Save(path, new PresetFile { Presets = new() { mixed } });
    var mixedLoaded = PresetStorage.Load(path);
    Assert(mixedLoaded.Presets[0].Slots.Count == 5 && mixedLoaded.Presets[0].Slots[4].Category == "Tent", "tent and ornament index zero coexist and round trip");
    var duplicateTent = mixed.Copy(); duplicateTent.Slots.Add(duplicateTent.Slots[4]);
    Reject(new PresetFile { Presets = new() { duplicateTent } }, "duplicate tent rejected");
    var wrongTent = mixed.Copy(); wrongTent.Slots[4].Index = 1;
    Reject(new PresetFile { Presets = new() { wrongTent } }, "tent index one rejected");
    Reject(new PresetFile { SchemaVersion = 1, Presets = new() { mixed } }, "tent forbidden in legacy schema");
    var expanded = mixed.Copy();
    expanded.Slots.AddRange(Enumerable.Range(0, 3).Select(i => new VisualSlot
        { Category = "OrnamentL", Index = i, ItemId = (uint)(119084 + i), ModelName = "fld_cst_036_0" + i }));
    PresetStorage.Save(path, new PresetFile { Presets = new() { expanded } });
    var expandedLoaded = PresetStorage.Load(path);
    Assert(expandedLoaded.Presets[0].Slots.Count == 8 && expandedLoaded.Presets[0].Slots.Count(s => s.Category == "OrnamentL") == 3,
        "three large objects coexist with small objects and tent");
    var duplicateLarge = expanded.Copy(); duplicateLarge.Slots.Add(duplicateLarge.Slots[5]);
    Reject(new PresetFile { Presets = new() { duplicateLarge } }, "duplicate large-object slot rejected");
    var wrongLarge = expanded.Copy(); wrongLarge.Slots[5].Index = 3;
    Reject(new PresetFile { Presets = new() { wrongLarge } }, "large-object index three rejected");
    Reject(new PresetFile { SchemaVersion = 2, Presets = new() { expanded } }, "large objects forbidden in schema two");
    var withShelves = expanded.Copy();
    withShelves.Slots.AddRange(Enumerable.Range(0, 3).Select(i => new VisualSlot
        { Category = "Shelf", Index = i, ItemId = (uint)(119049 + i), ModelName = "fld_cst_019_0" + i }));
    PresetStorage.Save(path, new PresetFile { Presets = new() { withShelves } });
    var shelfLoaded = PresetStorage.Load(path);
    Assert(shelfLoaded.Presets[0].Slots.Count == 11 && shelfLoaded.Presets[0].Slots.Count(s => s.Category == "Shelf") == 3,
        "three shelves coexist with ornaments and tent");
    var duplicateShelf = withShelves.Copy(); duplicateShelf.Slots.Add(duplicateShelf.Slots[8]);
    Reject(new PresetFile { Presets = new() { duplicateShelf } }, "duplicate shelf slot rejected");
    var wrongShelf = withShelves.Copy(); wrongShelf.Slots[8].Index = 3;
    Reject(new PresetFile { Presets = new() { wrongShelf } }, "shelf index three rejected");
    Reject(new PresetFile { SchemaVersion = 3, Presets = new() { withShelves } }, "shelves forbidden in schema three");
    File.WriteAllText(path, JsonSerializer.Serialize(new PresetFile { SchemaVersion = 1, Presets = new() { original } }));
    string legacy = File.ReadAllText(path);
    var migrated = PresetStorage.Load(path);
    Assert(migrated.SchemaVersion == 9 && migrated.Presets[0].Slots.Count == 4 && File.ReadAllText(path) == legacy, "legacy preset loads without modifying file");
    PresetStorage.Save(path, migrated);
    Assert(File.ReadAllText(path + ".bak") == legacy, "migration preserves legacy backup");
    File.WriteAllText(path, "{broken");
    Assert(PresetStorage.Load(path).Presets[0].Slots.Count == 4 && File.ReadAllText(path) == "{broken",
        "malformed primary recovers external backup without modifying primary");
    File.Delete(path + ".bak");
    bool rejected = false;
    try { PresetStorage.Load(path); } catch (JsonException) { rejected = true; }
    Assert(rejected && File.ReadAllText(path) == "{broken", "malformed file rejected without modification");
    Reject(new PresetFile { SchemaVersion = 10, Presets = new() { original } }, "future schema rejected by validator");
    PresetStorage.Validate(new PresetFile { Presets = new() { original, original.Copy() } });
    Assert(true, "duplicate names allowed");
    var duplicateSlot = original.Copy(); duplicateSlot.Slots.Add(duplicateSlot.Slots[0]);
    Reject(new PresetFile { Presets = new() { duplicateSlot } }, "duplicate slot rejected");
    var wrongCategory = original.Copy(); wrongCategory.Slots[0].Category = "Unknown";
    Reject(new PresetFile { Presets = new() { wrongCategory } }, "unsupported category rejected");
    var wrongIndex = original.Copy(); wrongIndex.Slots[0].Index = 4;
    Reject(new PresetFile { Presets = new() { wrongIndex } }, "out of range slot rejected");
    var missingIdentity = original.Copy(); missingIdentity.Slots[0].ModelName = "";
    Reject(new PresetFile { Presets = new() { missingIdentity } }, "missing model identity rejected");
    var displayModes = new VisualPreset { Name = "Display modes", Slots = new()
    {
        new VisualSlot { Category = "Tent", Index = 0, Mode = "Actual" },
        new VisualSlot { Category = "Shelf", Index = 1, Mode = "Hidden" }
    }};
    PresetStorage.Save(path, new PresetFile { Presets = new() { displayModes } });
    var displayModesLoaded = PresetStorage.Load(path);
    Assert(displayModesLoaded.Presets[0].Slots[0].IsActual && displayModesLoaded.Presets[0].Slots[1].IsHidden,
        "actual and hidden display modes round trip");
    var invalidMode = displayModes.Copy(); invalidMode.Slots[0].Mode = "Mystery";
    Reject(new PresetFile { Presets = new() { invalidMode } }, "unknown display mode rejected");
}
finally
{
    foreach (string file in new[] { path, path + ".bak", path + ".tmp" }) if (File.Exists(file)) File.Delete(file);
    Directory.Delete(directory, false);
}
passed += StorageChecks.Run();
Console.WriteLine($"Total: {passed} checks passed.");
