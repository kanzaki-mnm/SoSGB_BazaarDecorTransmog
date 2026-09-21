using BepInEx;
using BokuMono;
using BokuMono.Data;
using UnityEngine;

namespace BazaarDecorTransmog;

internal static class Prototype
{
    internal static bool Enabled { get; private set; } = true;
    internal static bool EditorPreview { get; private set; }
    private static readonly SlotRuntime[] slots = Enumerable.Range(0, 4).Select(i => new SlotRuntime(i))
        .Append(new SlotRuntime(0, BazaarCustomItemData.PartsCategory.Tent))
        .Concat(Enumerable.Range(0, 3).Select(i => new SlotRuntime(i, BazaarCustomItemData.PartsCategory.OrnamentL)))
        .Concat(Enumerable.Range(0, 3).Select(i => new SlotRuntime(i, BazaarCustomItemData.PartsCategory.Shelf))).ToArray();
    private static readonly string[] SmallSlotNames = { "0: Left outside", "1: Left inside", "2: Right outside", "3: Right inside" };
    private static readonly string[] LargeSlotNames = { "0: Left", "1: Right outside", "2: Right inside" };
    private static readonly string[] ShelfSlotNames = { "0: Left", "1: Center", "2: Right" };
    private static PresetFile saved;
    private static VisualPreset draft;
    private static VisualPreset appearanceEntrySnapshot;
    private static BazaarMyShop shop;
    private static int selectedSlot;
    private static string filePath, nameText, message = "", search = "";
    private static bool dirty, storageBlocked, wasEditing, collapsed = true;
    private static readonly List<Choice> choices = new();
    private sealed class Choice
    {
        internal uint Id;
        internal string Name, Model, Category;
    }

    internal static void Configure()
    {
        filePath = Path.Combine(Paths.ConfigPath, "BazaarDecorTransmog.cfg");
        saved = new PresetFile { Presets = new() { new VisualPreset { Name = "Default" } } };
        try
        {
            if (File.Exists(filePath)) saved = PresetStorage.Load(filePath);
            else PresetStorage.Save(filePath, saved);
        }
        catch (Exception ex)
        {
            // Never silently overwrite unreadable or newer-schema data.
            storageBlocked = true;
            saved = new PresetFile { Presets = new() { new VisualPreset { Name = "Recovery" } } };
            message = "Preset file error: file preserved; saving blocked. See log.";
            Plugin.Warn("PresetFileError", new { filePath, error = ex.Message });
        }
        LoadDraft(saved.CurrentAppearance ??
            saved.Presets[0]);
    }

    private static void LoadDraft(VisualPreset value)
    {
        PanelTextInput.Blur();
        draft = value.Copy();
        nameText = value.Name;
        dirty = false;
        Rebind();
    }

    private static bool Matches(VisualSlot value, int runtime) =>
        value.Index == slots[runtime].Index && value.Category == slots[runtime].PartCategory.ToString();

    private static int RuntimeIndex(BazaarCustomItemData.PartsCategory category, int index) => category switch
    {
        BazaarCustomItemData.PartsCategory.OrnamentS => index,
        BazaarCustomItemData.PartsCategory.Tent => 4,
        BazaarCustomItemData.PartsCategory.OrnamentL => 5 + index,
        BazaarCustomItemData.PartsCategory.Shelf => 8 + index,
        _ => -1
    };

    internal static VisualSlot TentBinding => draft?.Slots.FirstOrDefault(s =>
        s.Category == BazaarCustomItemData.PartsCategory.Tent.ToString() && s.Index == 0);

    internal static VisualSlot ShelfBinding(int index) => draft?.Slots.FirstOrDefault(s =>
        s.Category == BazaarCustomItemData.PartsCategory.Shelf.ToString() && s.Index == index);

    // A shelf remains a gameplay surface while the bazaar is open. Hiding it leaves the
    // stock and price prompts behind with no visible place to interact, so that category
    // deliberately has no Hidden mode.
    internal static bool AllowsHidden(BazaarCustomItemData.PartsCategory category) =>
        category != BazaarCustomItemData.PartsCategory.Shelf;

    internal static uint AppearanceId(BazaarCustomItemData.PartsCategory category, int index)
    {
        int runtime = RuntimeIndex(category, index);
        if (runtime < 0 || runtime >= slots.Length || draft == null) return 0;
        var binding = draft.Slots.FirstOrDefault(s => Matches(s, runtime));
        if (binding == null || binding.IsActual || binding.IsHidden && !AllowsHidden(category))
            return slots[runtime].ActualVisualId();
        return SlotRuntime.ResolveVisual(binding, category);
    }

    internal static uint ActualAppearanceId(BazaarCustomItemData.PartsCategory category, int index)
    {
        int runtime = RuntimeIndex(category, index);
        return runtime >= 0 && runtime < slots.Length ? slots[runtime].ActualVisualId() : 0;
    }

    internal static bool UsesHiddenAppearance(BazaarCustomItemData.PartsCategory category, int index)
    {
        int runtime = RuntimeIndex(category, index);
        if (runtime < 0 || runtime >= slots.Length || draft == null || !AllowsHidden(category)) return false;
        return draft.Slots.FirstOrDefault(s => Matches(s, runtime))?.IsHidden == true;
    }

    // The native list uses this while drawing the small circular-arrow marker.  Keep
    // this separate from AppearanceId: the latter intentionally resolves Actual to
    // the currently equipped item's id, whereas the UI also needs to know *why* it
    // resolved that way.
    internal static bool UsesActualAppearance(BazaarCustomItemData.PartsCategory category, int index)
    {
        int runtime = RuntimeIndex(category, index);
        if (runtime < 0 || runtime >= slots.Length || draft == null) return false;
        var binding = draft.Slots.FirstOrDefault(s => Matches(s, runtime));
        // Actual is stored compactly: the ordinary case has no slot record at all.
        // Treat that omitted record exactly like an explicit Actual record for UI.
        return binding == null || binding.IsActual || binding.IsHidden && !AllowsHidden(category);
    }

    private static void SetDisplayMode(string mode)
    {
        var runtime = slots[selectedSlot];
        if (mode == "Hidden" && !AllowsHidden(runtime.PartCategory))
        {
            message = "Shelves cannot be hidden because they remain gameplay surfaces.";
            return;
        }
        draft.Slots.RemoveAll(s => Matches(s, selectedSlot));
        if (mode != "Actual")
            draft.Slots.Add(new VisualSlot { Index = runtime.Index, Category = runtime.PartCategory.ToString(), Mode = mode });
        // An omitted entry is deliberately equivalent to Actual. It keeps old, all-vanilla
        // presets compact while the explicit Hidden record persists the new choice.
        dirty = true;
        runtime.Bind(draft.Slots.FirstOrDefault(s => Matches(s, selectedSlot)));
        message = mode == "Hidden" ? "Draft changed: this slot will be hidden." : "Draft changed: this slot uses its actual appearance.";
    }

    private static void Rebind()
    {
        for (int i = 0; i < slots.Length; i++) slots[i].Bind(draft.Slots.FirstOrDefault(s => Matches(s, i)));
        ClosedTentProbe.RestoreAll("preset or mode rebound");
        ClosedShelfProbe.RestoreAll("preset or mode rebound");
    }

    internal static void BeginNativePreview(BazaarCustomPageCategory focusedCategory)
    {
        EditorPreview = true;
        int nativeFocusedSlot = RuntimeIndex(
            BazaarCustomItemData.ToPartsCategory(focusedCategory),
            BazaarCustomItemData.ToPartsCategoryIndex(focusedCategory));
        for (int i = 0; i < slots.Length; i++)
            slots[i].BeginEditorFocus(i == nativeFocusedSlot);
        Rebind();
    }

    private static PresetFile CopySavedFile() => new()
    {
        SchemaVersion = 9,
        CurrentAppearance = saved.CurrentAppearance?.Copy(),
        Presets = saved.Presets.Select(p => p.Copy()).ToList()
    };

    internal static void BeginAppearanceSession() => appearanceEntrySnapshot = draft?.Copy();

    internal static bool HasAppearanceSessionChanges() =>
        appearanceEntrySnapshot != null && !SameAppearance(appearanceEntrySnapshot, draft);

    private static bool SameAppearance(VisualPreset left, VisualPreset right)
    {
        if (left?.Slots == null || right?.Slots == null || left.Slots.Count != right.Slots.Count) return false;
        return left.Slots.All(a => right.Slots.Any(b => a.Index == b.Index && a.ItemId == b.ItemId &&
            string.Equals(a.Category, b.Category, StringComparison.Ordinal) &&
            string.Equals(a.ModelName, b.ModelName, StringComparison.Ordinal) &&
            string.Equals(a.Mode, b.Mode, StringComparison.Ordinal)));
    }

    internal static bool SaveCurrentAppearance()
    {
        if (storageBlocked || saved == null || draft == null) return false;
        var candidate = CopySavedFile();
        var current = draft.Copy();
        current.Name = "CurrentAppearance";
        current.UiSlotIndex = null;
        candidate.CurrentAppearance = current;
        try
        {
            PresetStorage.Save(filePath, candidate);
            saved = candidate;
            dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Warn("CurrentAppearanceSaveError", new { error = ex.Message });
            return false;
        }
    }

    internal static void RevertAppearanceSession()
    {
        if (appearanceEntrySnapshot == null) return;
        LoadDraft(appearanceEntrySnapshot);
        NativeDecorUi.RefreshAfterPresetLoad();
    }

    internal static void EndAppearanceSession() => appearanceEntrySnapshot = null;

    internal static bool CurrentEditorPoseHovered =>
        selectedSlot >= 0 && selectedSlot < slots.Length && slots[selectedSlot].IsEditorHovered;

    internal static void RestoreCurrentEditorPose(bool hovered)
    {
        if (selectedSlot >= 0 && selectedSlot < slots.Length)
            slots[selectedSlot].SetEditorPoseImmediate(hovered);
    }

    internal static void SetEnabled(bool value)
    {
        Enabled = value;
        Rebind();
    }

    internal static void EndNativePreview()
    {
        // The stock editor tears down and recreates its slot models while closing. Keep no
        // replacement clone attached to those transient roots: it can briefly retain an
        // unloaded material (the magenta model flash) on the field transition. Restore
        // now invalidates outstanding loads; Tick reapplies cleanly once field roots are
        // active again.
        foreach (var slot in slots)
        {
            slot.SetFocused(false);
            slot.Restore("native appearance preview ended");
            slot.ResetPreviewAttempt();
        }
        EditorPreview = false;
        // Bindings have not changed. Do not call Rebind here: it would also tear down the
        // closed-shop tent while that root is inactive, then recreate it before its field
        // materials have been initialized.
    }

    internal static void NativeChoice(int index, CustomPartsMasterData part, bool commit)
    {
        if (shop == null) return;
        int nextSlot = RuntimeIndex(part.Category, index);
        if (nextSlot < 0 || nextSlot >= slots.Length) return;
        // A tab change only cancels the preview on the slot we are leaving. Rebinding every
        // slot here briefly restored every real/effect model before its transmog was reapplied.
        if (selectedSlot >= 0 && selectedSlot < slots.Length && selectedSlot != nextSlot)
        {
            slots[selectedSlot].SetFocused(false);
            slots[selectedSlot].Bind(draft.Slots.FirstOrDefault(s => Matches(s, selectedSlot)));
        }
        selectedSlot = nextSlot;
        var choice = new VisualSlot { Index = index, Category = part.Category.ToString(), ItemId = part.Id, ModelName = part.modelName };
        if (commit)
        {
            draft.Slots.RemoveAll(s => Matches(s, selectedSlot));
            draft.Slots.Add(choice);
            dirty = true;
        }
        EditorPreview = true;
        slots[selectedSlot].Bind(choice);
        if (commit) slots[selectedSlot].PlayDecisionFeedback();
        else slots[selectedSlot].SetFocusedAfterVisual(part.Id);
        // Move the game's own cyan selection footprint without asking it to reload the real
        // decor model. The latter caused a one-frame flash before the transmog was reapplied.
        shop.SetCustomCursor(part.Category, index);
        // Tick normally throttles applied slots to 0.5-second checks. Start the latest
        // appearance request on the cursor event so quick list navigation does not lag.
        slots[selectedSlot].Tick(true);
    }

    internal static void Observe(BazaarMyShop instance)
    {
        if (shop != instance) { EditorPreview = false; wasEditing = false; }
        shop = instance;
        foreach (var slot in slots) slot.Observe(instance);
    }

    internal static void Suspend()
    {
        EditorPreview = false;
        foreach (var slot in slots) slot.Suspend();
    }

    internal static void Restore(string reason)
    {
        foreach (var slot in slots) slot.Restore(reason);
        ClosedTentProbe.RestoreAll(reason);
        ClosedShelfProbe.RestoreAll(reason);
    }

    internal static void NativeModeChoice(BazaarCustomItemData.PartsCategory category, int index, string mode, bool commit)
    {
        if (shop == null || mode != "Actual" && mode != "Hidden") return;
        if (mode == "Hidden" && !AllowsHidden(category)) return;
        int nextSlot = RuntimeIndex(category, index);
        if (nextSlot < 0 || nextSlot >= slots.Length) return;
        if (selectedSlot >= 0 && selectedSlot < slots.Length && selectedSlot != nextSlot)
        {
            slots[selectedSlot].SetFocused(false);
            slots[selectedSlot].Bind(draft.Slots.FirstOrDefault(s => Matches(s, selectedSlot)));
        }
        selectedSlot = nextSlot;
        if (commit)
        {
            draft.Slots.RemoveAll(s => Matches(s, selectedSlot));
            if (mode == "Hidden")
                draft.Slots.Add(new VisualSlot { Index = index, Category = category.ToString(), Mode = mode });
            dirty = true;
        }
        EditorPreview = true;
        slots[selectedSlot].Bind(mode == "Actual" ? null :
            new VisualSlot { Index = index, Category = category.ToString(), Mode = mode });
        if (commit) slots[selectedSlot].PlayDecisionFeedback();
        else slots[selectedSlot].SetFocused(mode == "Actual");
        shop.SetCustomCursor(category, index);
        slots[selectedSlot].Tick(true);
    }

    internal static void RefreshFieldModelsNow()
    {
        if (shop?.BM == null || shop.BM.IsCustomMode || !Enabled) return;
        foreach (var slot in slots)
            Plugin.Guard("field-model-ready:" + slot.PartCategory + ":" + slot.Index,
                () => slot.Tick(immediate: true));
    }

    internal static void ActualPlacementChanged(BazaarCustomItemData.PartsCategory category, int index)
    {
        int runtime = RuntimeIndex(category, index);
        if (runtime >= 0 && runtime < slots.Length)
            slots[runtime].InvalidateActualModel("actual placement changed");
        // Closed-shop instances have an independent lifetime and may still own a
        // visual cloned for the previous real model. Rebuild them from the new
        // placement after the game finishes loading it.
        ClosedTentProbe.RestoreAll("actual placement changed");
        ClosedShelfProbe.RestoreAll("actual placement changed");
    }

    internal static void Tick()
    {
        Plugin.Guard("closed-observe", ClosedTentProbe.Tick);
        Plugin.Guard("closed-shelf-observe", ClosedShelfProbe.Tick);
        bool editing = shop != null && shop.BM != null && shop.BM.IsCustomMode;
        if (editing != wasEditing)
        {
            if (!editing) NativeDecorUi.Exit();
            Suspend();
            wasEditing = editing;
            if (editing) NativeDecorUi.Sync();
        }
        if (editing) Plugin.Guard("native-ui-sync", NativeDecorUi.Sync);
        if (choices.Count == 0 && shop != null)
        {
            var mdm = BokuMono.API.Bazaar.MDM;
            var rows = mdm?.CustomPartsMaster?.list;
            if (rows != null && mdm.ItemMaster != null)
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if ((row.Category != BazaarCustomItemData.PartsCategory.OrnamentS &&
                         row.Category != BazaarCustomItemData.PartsCategory.Tent &&
                         row.Category != BazaarCustomItemData.PartsCategory.OrnamentL &&
                         row.Category != BazaarCustomItemData.PartsCategory.Shelf)) continue;
                    string name = mdm.ItemMaster.TryGetData(row.Id, out var item) && item != null ? item.ItemName : row.Id.ToString();
                    choices.Add(new Choice { Id = row.Id, Name = name, Model = row.modelName, Category = row.Category.ToString() });
                }
        }
        for (int i = 0; i < slots.Length; i++)
        {
            int index = i;
            Plugin.Guard("slot-tick:" + i, () => slots[index].Tick());
        }
    }

    internal static void UpdateDecisionDrops()
    {
        if (!EditorPreview) return;
        for (int i = 0; i < slots.Length; i++)
        {
            int index = i;
            Plugin.Guard("decision-drop:" + i, () =>
            {
                slots[index].UpdateDecisionDrop();
                slots[index].HoldNativeTransitionFocus();
            });
        }
    }

    private static void CycleVisual(int direction)
    {
        var filtered = choices.Where(c => c.Category == slots[selectedSlot].PartCategory.ToString() && (string.IsNullOrWhiteSpace(search) || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Id.ToString().Contains(search))).ToList();
        if (filtered.Count == 0) { message = "No matching non-DLC appearances in this category."; return; }
        var existing = draft.Slots.FirstOrDefault(s => Matches(s, selectedSlot));
        int old = filtered.FindIndex(c => c.Id == existing?.ItemId);
        int next = old < 0 ? (direction > 0 ? 0 : filtered.Count - 1) : (old + direction + filtered.Count) % filtered.Count;
        var chosen = filtered[next];
        draft.Slots.RemoveAll(s => Matches(s, selectedSlot));
        draft.Slots.Add(new VisualSlot { Index = slots[selectedSlot].Index, Category = chosen.Category, ItemId = chosen.Id, ModelName = chosen.Model });
        dirty = true;
        slots[selectedSlot].Bind(draft.Slots.First(s => Matches(s, selectedSlot)));
        message = "Draft changed. Save preset to keep it after restart.";
    }

    private static void Save()
    {
        if (storageBlocked) return;
        string name = nameText.Trim();
        if (name.Length == 0 || name.Length > 60) { message = "Enter a preset name (1-60 characters)."; return; }
        var candidate = CopySavedFile();
        var value = draft.Copy();
        value.Name = name;
        int index = value.UiSlotIndex.HasValue
            ? candidate.Presets.FindIndex(p => p.UiSlotIndex == value.UiSlotIndex)
            : candidate.Presets.FindIndex(p => p.Name == name);
        if (index < 0) candidate.Presets.Add(value); else candidate.Presets[index] = value;
        try
        {
            PresetStorage.Save(filePath, candidate);
            saved = candidate;
            LoadDraft(value);
            message = "Saved: " + name;
        }
        catch (Exception ex) { message = "Save failed. Existing preset retained."; Plugin.Warn("PresetSaveError", new { error = ex.Message }); }
    }

    internal static string UiSlotLabel(int index)
    {
        var preset = saved?.Presets.FirstOrDefault(p => p.UiSlotIndex == index + 1);
        return Localization.SlotLabelPrefix(index + 1) +
            (preset == null ? Localization.Get("presets.slots.empty") : preset.Name);
    }

    internal static VisualPreset UiSlotPreset(int index) =>
        index >= 0 && index < PresetStorage.UiSlotCount ? saved?.Presets.FirstOrDefault(p => p.UiSlotIndex == index + 1)?.Copy() : null;

    internal static uint UiSlotAppearanceId(int uiIndex, BazaarCustomItemData.PartsCategory category, int index)
    {
        var preset = UiSlotPreset(uiIndex);
        var runtime = RuntimeIndex(category, index);
        if (preset == null || runtime < 0 || runtime >= slots.Length) return 0;
        var binding = preset.Slots.FirstOrDefault(s => Matches(s, runtime));
        if (binding == null || binding.IsActual || binding.IsHidden && !AllowsHidden(category))
            return slots[runtime].ActualVisualId();
        return SlotRuntime.ResolveVisual(binding, category);
    }

    internal static bool LoadUiSlot(int index)
    {
        if (storageBlocked || draft == null) return false;
        var preset = UiSlotPreset(index);
        if (preset == null) return false;
        var previous = draft.Copy();
        try
        {
            LoadDraft(preset);
            RefreshFieldModelsNow();
            NativeDecorUi.RefreshAfterPresetLoad();
            message = "Loaded slot " + (index + 1) + ": " + preset.Name;
            return true;
        }
        catch (Exception ex)
        {
            try { LoadDraft(previous); NativeDecorUi.RefreshAfterPresetLoad(); }
            catch (Exception rollbackError) { Plugin.Warn("PresetUiLoadRollbackError", new { error = rollbackError.Message }); }
            Plugin.Warn("PresetUiLoadError", new { slot = index + 1, error = ex.Message });
            return false;
        }
    }

    internal static bool SaveUiSlot(int index, string enteredName)
    {
        if (storageBlocked || saved == null || draft == null || index < 0 || index >= PresetStorage.UiSlotCount)
        {
            Plugin.Warn("PresetUiSaveRejected", new { reason = "storage unavailable or invalid slot", slot = index + 1 });
            return false;
        }
        string name = (enteredName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 8)
        {
            Plugin.Warn("PresetUiSaveRejected", new { reason = "invalid name", slot = index + 1 });
            return false;
        }
        var candidate = CopySavedFile();
        var value = draft.Copy();
        value.Name = name;
        value.UiSlotIndex = index + 1;
        int existing = candidate.Presets.FindIndex(p => p.UiSlotIndex == index + 1);
        if (existing >= 0) candidate.Presets[existing] = value;
        else candidate.Presets.Add(value);
        try
        {
            PresetStorage.Save(filePath, candidate);
            saved = candidate;
            LoadDraft(value);
            message = "Saved slot " + (index + 1) + ": " + name;
            return true;
        }
        catch (Exception ex)
        {
            message = "Save failed. Existing preset retained.";
            Plugin.Warn("PresetUiSaveError", new { slot = index + 1, error = ex.Message });
            return false;
        }
    }

    internal static bool DeleteUiSlot(int index)
    {
        if (storageBlocked || saved == null || index < 0 || index >= PresetStorage.UiSlotCount)
        {
            Plugin.Warn("PresetUiDeleteRejected", new { reason = "storage unavailable or invalid slot", slot = index + 1 });
            return false;
        }
        var existing = UiSlotPreset(index);
        if (existing == null)
        {
            Plugin.Warn("PresetUiDeleteRejected", new { reason = "empty slot", slot = index + 1 });
            return false;
        }
        var candidate = CopySavedFile();
        candidate.Presets = candidate.Presets.Where(p => p.UiSlotIndex != index + 1).ToList();
        // PresetStorage rejects an empty file. Normal UI usage always keeps the
        // legacy Default preset, but retain a valid file even if an imported file
        // contained only this slot.
        if (candidate.Presets.Count == 0)
            candidate.Presets.Add(new VisualPreset { Name = "Default" });
        try
        {
            PresetStorage.Save(filePath, candidate);
            saved = candidate;
            message = "Deleted slot " + (index + 1) + ": " + existing.Name;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Warn("PresetUiDeleteError", new { slot = index + 1, name = existing.Name, error = ex.Message });
            return false;
        }
    }

    private static void CyclePreset(int direction)
    {
        if (dirty) { message = "Save or Revert draft before switching presets."; return; }
        int index = Math.Max(0, saved.Presets.FindIndex(p => p.Name == draft.Name));
        LoadDraft(saved.Presets[(index + direction + saved.Presets.Count) % saved.Presets.Count]);
        message = "Loaded: " + draft.Name;
    }

    internal static void Draw()
    {
        if (shop == null || draft == null) return;
        float x = Math.Max(16, Screen.width - 616);
        const float y = 12;
        if (wasEditing) NativeDecorUi.Draw(x, y);
        GUI.Box(new Rect(x, y, 600, collapsed ? 40 : 436), "Bazaar Decor Transmog " + Plugin.Version + " - Debug / presets");
        if (PanelControls.Button(new Rect(x + 554, y + 4, 38, 25), collapsed ? "+" : "-")) { collapsed = !collapsed; PanelTextInput.Blur(); }
        if (collapsed) return;
        GUI.Label(new Rect(x + 12, y + 26, 390, 24), "Preset: " + draft.Name + (dirty ? " * unsaved" : ""));
        if (PanelControls.Button(new Rect(x + 408, y + 26, 78, 24), "Previous")) CyclePreset(-1);
        if (PanelControls.Button(new Rect(x + 492, y + 26, 94, 24), "Next preset")) CyclePreset(1);
        var selectedCategory = slots[selectedSlot].PartCategory;
        if (PanelControls.Button(new Rect(x + 12, y + 58, 136, 28), (selectedCategory == BazaarCustomItemData.PartsCategory.OrnamentS ? "> " : "") + "Small")) selectedSlot = 0;
        if (PanelControls.Button(new Rect(x + 154, y + 58, 136, 28), (selectedCategory == BazaarCustomItemData.PartsCategory.Tent ? "> " : "") + "Tent")) selectedSlot = 4;
        if (PanelControls.Button(new Rect(x + 296, y + 58, 142, 28), (selectedCategory == BazaarCustomItemData.PartsCategory.OrnamentL ? "> " : "") + "Large")) selectedSlot = 5;
        if (PanelControls.Button(new Rect(x + 444, y + 58, 142, 28), (selectedCategory == BazaarCustomItemData.PartsCategory.Shelf ? "> " : "") + "Shelves")) selectedSlot = 8;
        selectedCategory = slots[selectedSlot].PartCategory;
        if (selectedCategory == BazaarCustomItemData.PartsCategory.OrnamentS)
            for (int i = 0; i < 4; i++)
                if (PanelControls.Button(new Rect(x + 12 + i * 144, y + 90, 140, 28), (selectedSlot == i ? "> " : "") + SmallSlotNames[i])) selectedSlot = i;
        if (selectedCategory == BazaarCustomItemData.PartsCategory.Tent)
            PanelControls.Button(new Rect(x + 12, y + 90, 574, 28), "> Tent");
        if (selectedCategory == BazaarCustomItemData.PartsCategory.OrnamentL)
            for (int i = 0; i < 3; i++)
                if (PanelControls.Button(new Rect(x + 12 + i * 192, y + 90, 188, 28), (selectedSlot == 5 + i ? "> " : "") + LargeSlotNames[i])) selectedSlot = 5 + i;
        if (selectedCategory == BazaarCustomItemData.PartsCategory.Shelf)
            for (int i = 0; i < 3; i++)
                if (PanelControls.Button(new Rect(x + 12 + i * 192, y + 90, 188, 28), (selectedSlot == 8 + i ? "> " : "") + ShelfSlotNames[i])) selectedSlot = 8 + i;
        var slot = slots[selectedSlot];
        GUI.Label(new Rect(x + 12, y + 126, 575, 24), (wasEditing ? "Edited placement: " : "Actual / effects: ") + slot.Actual);
        GUI.Label(new Rect(x + 12, y + 150, 575, 24), "Visual target: " + slot.Visual);
        GUI.Label(new Rect(x + 12, y + 174, 575, 24), slot.Status);
        GUI.Label(new Rect(x + 12, y + 206, 65, 24), "Search:");
        search = PanelTextInput.Draw("search", new Rect(x + 80, y + 206, 140, 26), search, 60);
        if (PanelControls.Button(new Rect(x + 224, y + 206, 68, 26), "Paste")) { search = PanelTextInput.Paste(search, 60); PanelTextInput.Blur(); message = "Search: " + search; }
        if (PanelControls.Button(new Rect(x + 300, y + 206, 66, 26), "<")) CycleVisual(-1);
        if (PanelControls.Button(new Rect(x + 372, y + 206, 66, 26), ">")) CycleVisual(1);
        if (PanelControls.Button(new Rect(x + 446, y + 206, 140, 26), "Use actual")) SetDisplayMode("Actual");
        if (AllowsHidden(slot.PartCategory) && PanelControls.Button(new Rect(x + 446, y + 238, 140, 26), "Hide object")) SetDisplayMode("Hidden");
        if (PanelControls.Button(new Rect(x + 12, y + 274, 430, 28), Enabled ? "Transmog: ON" : "Transmog: OFF"))
        {
            SetEnabled(!Enabled);
            NativeDecorUi.Sync();
        }
        GUI.Label(new Rect(x + 12, y + 310, 575, 24), "Tent applies open/closed; shelves keep products and sale points intact.");
        GUI.Label(new Rect(x + 12, y + 340, 90, 24), "Save name:");
        nameText = PanelTextInput.Draw("preset-name", new Rect(x + 105, y + 340, 215, 26), nameText, 60);
        bool enabledBefore = GUI.enabled;
        GUI.enabled = enabledBefore && !storageBlocked;
        if (PanelControls.Button(new Rect(x + 330, y + 340, 128, 26), "Save preset")) Save();
        GUI.enabled = enabledBefore;
        if (PanelControls.Button(new Rect(x + 466, y + 340, 120, 26), "Revert draft"))
            LoadDraft(saved.Presets.FirstOrDefault(p => p.Name == draft.Name) ?? saved.Presets[0]);
        GUI.Label(new Rect(x + 12, y + 374, 575, 24), "Same name overwrites; a new name creates a preset.");
        GUI.Label(new Rect(x + 12, y + 404, 575, 28), message);
    }
}
