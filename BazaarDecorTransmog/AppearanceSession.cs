using BepInEx;
using BokuMono;
using BokuMono.Data;
using UnityEngine;

namespace BazaarDecorTransmog;

internal static class AppearanceSession
{
    internal static bool Enabled { get; private set; } = true;
    internal static bool EditorPreview { get; private set; }
    internal static void Stop()
    {
        Enabled = false;
        AppearanceEditorUi.Ready = false;
        AppearanceModelPreloader.Cancel();
        // Keep runtime patches installed so an already open Mod dialog can close.
    }
    private static readonly SlotAppearance[] slots = Enumerable.Range(0, 4).Select(i => new SlotAppearance(i))
        .Append(new SlotAppearance(0, BazaarCustomItemData.PartsCategory.Tent))
        .Concat(Enumerable.Range(0, 3).Select(i => new SlotAppearance(i, BazaarCustomItemData.PartsCategory.OrnamentL)))
        .Concat(Enumerable.Range(0, 3).Select(i => new SlotAppearance(i, BazaarCustomItemData.PartsCategory.Shelf))).ToArray();
    private static PresetFile saved;
    private static VisualPreset draft;
    private static VisualPreset appearanceEntrySnapshot;
    private static BazaarMyShop shop;
    private static int selectedSlot;
    private static string filePath;
    private static bool storageBlocked, wasEditing;

    internal static void Configure()
    {
        filePath = Path.Combine(Paths.ConfigPath, "BazaarDecorTransmog.cfg");
        saved = new PresetFile { Presets = new() { new VisualPreset { Name = "Default" } } };
        try
        {
            if (File.Exists(filePath) || File.Exists(filePath + ".bak")) saved = PresetStorage.Load(filePath);
            else PresetStorage.Save(filePath, saved);
        }
        catch (Exception ex)
        {
            // Never silently overwrite unreadable or newer-schema data.
            storageBlocked = true;
            saved = new PresetFile { Presets = new() { new VisualPreset { Name = "Recovery" } } };
            Plugin.Warn("PresetFileError", new { filePath, error = ex.Message });
        }
        LoadDraft(saved.CurrentAppearance ??
            saved.Presets[0]);
    }

    private static void LoadDraft(VisualPreset value)
    {
        draft = value.Copy();
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
        return SlotAppearance.ResolveVisual(binding, category);
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

    private static void Rebind()
    {
        for (int i = 0; i < slots.Length; i++) slots[i].Bind(draft.Slots.FirstOrDefault(s => Matches(s, i)));
        ClosedTentProbe.RestoreAll("preset or mode rebound");
        ClosedShelfProbe.RestoreAll("preset or mode rebound");
    }

    private static IEnumerable<string> CurrentAppearanceModelNames()
    {
        var master = BokuMono.API.Bazaar.MDM?.CustomPartsMaster;
        if (master == null) yield break;
        for (int i = 0; i < slots.Length; i++)
        {
            uint id = AppearanceId(slots[i].PartCategory, slots[i].Index);
            if (id == 0) continue;
            var part = master.GetMasterData(id);
            if (part != null && !string.IsNullOrWhiteSpace(part.modelName))
                yield return part.modelName;
        }
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
        if (!Enabled || storageBlocked || saved == null || draft == null) return false;
        var candidate = CopySavedFile();
        var current = draft.Copy();
        current.Name = "CurrentAppearance";
        current.UiSlotIndex = null;
        candidate.CurrentAppearance = current;
        try
        {
            PresetStorage.Save(filePath, candidate);
            saved = candidate;
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
        // Discard exits the editor; it is not a preset load that keeps editing.
        // Rebuilding/focusing the appearance list while the exit dialog owns
        // focus can leave its cursor behind. LeaveAppearance restores the stock
        // list and focus once, under the transition fade.
    }

    internal static void EndAppearanceSession() => appearanceEntrySnapshot = null;

    internal static bool CurrentEditorPoseHovered =>
        selectedSlot >= 0 && selectedSlot < slots.Length && slots[selectedSlot].IsEditorHovered;

    internal static void RestoreCurrentEditorPose(bool hovered)
    {
        if (selectedSlot >= 0 && selectedSlot < slots.Length)
            slots[selectedSlot].SetEditorPoseImmediate(hovered);
    }

    internal static void EndNativePreview()
    {
        // The stock editor tears down and recreates its slot models while closing. Keep no
        // replacement clone attached to those transient roots: it can briefly retain an
        // unloaded material (the magenta model flash) on the field transition. Restore
        // now invalidates outstanding loads; Tick reapplies cleanly once field roots are
        // active again.
        try
        {
            Recovery.Run(slots.Select(slot => (Action)(() =>
        {
            Recovery.Run(() => slot.SetFocused(false),
                () => slot.Restore("native appearance preview ended"), slot.ResetPreviewAttempt);
        })));
        }
        finally { EditorPreview = false; }
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
        if (shop != instance)
        {
            EditorPreview = false;
            wasEditing = false;
            AppearanceModelPreloader.Cancel();
        }
        shop = instance;
        foreach (var slot in slots) slot.Observe(instance);
    }

    internal static void Suspend()
    {
        EditorPreview = false;
        AppearanceModelPreloader.Cancel();
        Recovery.Run(slots.Select(slot => (Action)slot.Suspend));
    }

    internal static void Restore(string reason)
    {
        Recovery.Run(slots.Select(slot => (Action)(() => slot.Restore(reason)))
            .Append(() => ClosedTentProbe.RestoreAll(reason))
            .Append(() => ClosedShelfProbe.RestoreAll(reason)));
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
                () => slot.Tick(immediate: true), () => slot.Restore("field update failed"));
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
            if (!editing) AppearanceEditorUi.Exit();
            Suspend();
            wasEditing = editing;
            if (editing)
            {
                AppearanceEditorUi.Sync();
                AppearanceModelPreloader.Start(shop, CurrentAppearanceModelNames());
            }
        }
        if (editing) Plugin.Guard("appearance-preload", AppearanceModelPreloader.Tick,
            AppearanceModelPreloader.Cancel);
        if (editing) Plugin.Guard("native-ui-sync", AppearanceEditorUi.Sync, AppearanceEditorUi.Abort);
        for (int i = 0; i < slots.Length; i++)
        {
            int index = i;
            Plugin.Guard("slot-tick:" + i, () => slots[index].Tick(), () => slots[index].Restore("slot update failed"));
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
        return SlotAppearance.ResolveVisual(binding, category);
    }

    internal static bool UiSlotUsesActualAppearance(int uiIndex, BazaarCustomItemData.PartsCategory category, int index)
    {
        var preset = UiSlotPreset(uiIndex);
        var runtime = RuntimeIndex(category, index);
        // An empty save slot has no settings to preview; omitted bindings in an
        // existing preset, however, intentionally mean Actual.
        if (preset == null || runtime < 0 || runtime >= slots.Length) return false;
        var binding = preset.Slots.FirstOrDefault(s => Matches(s, runtime));
        return binding == null || binding.IsActual || binding.IsHidden && !AllowsHidden(category);
    }

    internal static bool LoadUiSlot(int index)
    {
        if (!Enabled || storageBlocked || draft == null) return false;
        var preset = UiSlotPreset(index);
        if (preset == null) return false;
        var previous = draft.Copy();
        try
        {
            LoadDraft(preset);
            RefreshFieldModelsNow();
            AppearanceEditorUi.RefreshAfterPresetLoad();
        }
        catch (Exception ex)
        {
            try { LoadDraft(previous); AppearanceEditorUi.RefreshAfterPresetLoad(); }
            catch (Exception rollbackError)
            {
                Plugin.Warn("PresetUiLoadRollbackError", new { error = rollbackError.Message });
                Plugin.Guard("preset-load-emergency", AppearanceEditorUi.Abort);
            }
            Plugin.Warn("PresetUiLoadError", new { slot = index + 1, error = ex.Message });
            return false;
        }
        // Animation is feedback for a successful load, not part of the transaction.
        // A cosmetic failure must not roll back the preset or report a failed load.
        if (EditorPreview)
            foreach (var slot in slots)
            {
                if (!Enabled || !EditorPreview) break;
                bool modelUpdateSucceeded = false;
                // Tick also changes renderer visibility. A failed update needs the
                // same full restoration as the regular slot-update path.
                Plugin.Guard("preset-load-model:" + slot.PartCategory + ":" + slot.Index, () =>
                {
                    slot.Tick(immediate: true);
                    modelUpdateSucceeded = true;
                }, () => slot.Restore("preset model update failed"));
                if (!modelUpdateSucceeded || !Enabled || !EditorPreview) continue;
                Plugin.Guard("preset-load-landing:" + slot.PartCategory + ":" + slot.Index,
                    () => slot.PlayDecisionFeedback(includeActual: true),
                    () => slot.SetEditorPoseImmediate(false));
            }
        return true;
    }

    internal static bool SaveUiSlot(int index, string enteredName)
    {
        if (!Enabled || storageBlocked || saved == null || draft == null || index < 0 || index >= PresetStorage.UiSlotCount)
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
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Warn("PresetUiSaveError", new { slot = index + 1, error = ex.Message });
            if (ReferenceEquals(saved, candidate))
                Plugin.Guard("preset-save-presentation-emergency", AppearanceEditorUi.Abort);
            return false;
        }
    }

    internal static bool DeleteUiSlot(int index)
    {
        if (!Enabled || storageBlocked || saved == null || index < 0 || index >= PresetStorage.UiSlotCount)
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
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Warn("PresetUiDeleteError", new { slot = index + 1, name = existing.Name, error = ex.Message });
            return false;
        }
    }

    internal static void RefreshEditorPresentation()
    {
        if (shop != null && draft != null && wasEditing)
            AppearanceEditorUi.RefreshPresentation();
    }
}
