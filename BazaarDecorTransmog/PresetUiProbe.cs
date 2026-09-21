using BokuMono;
using BokuMono.Data;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarDecorTransmog;

// Owns the preset flow while reusing the game's dialogs and preview rows.
internal static class PresetUiProbe
{
    internal enum HeaderMode
    {
        None,
        Save,
        Load
    }

    internal const uint NameInputTextId = 101031;
    internal const uint NameConfirmTextId = 101045;
    private const uint SaveCompletedTextId = 0xBD70000F;
    private const uint LoadCompletedTextId = 0xBD700010;
    private const uint DeleteCompletedTextId = 0xBD700013;
    private const uint DeleteConfirmTextId = 0xBD700012;
    // Confirmed from the game's save-overwrite dialog: DialogChoiceText / "Yes".
    private const uint StockYesChoiceTextId = 1000;
    // Confirmed from the game's save-overwrite dialog: DialogChoiceText / "Cancel".
    private const uint StockCancelChoiceTextId = 1010;
    private static Il2CppSystem.Action<int> choiceCallback;
    private static Il2CppSystem.Action<int> slotCallback;
    private static Il2CppSystem.Action<int> completionCallback;
    private static Il2CppSystem.Action<int> deleteCallback;
    private static Il2CppSystem.Action completionClosedAfter;
    private static Il2CppSystem.Action completionAfterClose;
    private static Il2CppSystem.Action openCompletionAfterDeleteDialog;
    private static Il2CppSystem.Action reopenSlotsAfterCompletion;
    private static Il2CppSystem.Action openSlotsAfterDialog;
    private static Il2CppSystem.Action reopenMenuAfterDialog;
    private static Il2CppSystem.Action openNameAfterDialog;
    private static Il2CppSystem.Action loadSlotAfterDialog;
    private static Il2CppSystem.Action closePresetAfterDialog;
    private static KeyboardManager.InputCompleteCallback inputCallback;
    private static Il2CppSystem.Action inputCancelled;
    private static bool slotMenuOpen;
    private static bool deleteConfirmOpen;
    private static int deleteTargetSlot = -1;
    private static string deleteTargetName;
    // Covers the whole preset flow: menu, slot list, and preset-name input.
    // The + / Tab guide only applies to the appearance view behind this flow.
    private static bool presetUiOpen;
    private static bool finishPresetAfterName;
    private static CompletionMessage pendingCompletion;
    private static CompletionMessage activeCompletion;
    private static bool completionDialogOpen;
    private static bool completionDialogSeen;
    private static bool completionTextReported;
    private static int completionDialogFrames;
    private static bool savingSlot;
    private static int selectedSlot = -1;
    private static bool nameInputOpen;
    private static bool returnToSaveSlots;
    private static bool nameFooterRequested;
    private static bool rowLoadAttempted;
    private static Il2CppSystem.Action rowLoadedCallback;
    private static GameObject objectPreview;
    private static RectTransform shiftedDialog;
    private static UISelectDialog slotDialog;
    private static Vector2 originalDialogPosition;
    // The select dialog is pooled.  Preserve its shifted position through its
    // close animation, then restore it in the close callback before it can be
    // reused by the next dialog.
    private static RectTransform closingShiftedDialog;
    private static Vector2 closingDialogOriginalPosition;
    private static Il2CppSystem.Action slotDialogClosedCallback;
    private static Il2CppSystem.Action slotDialogClosedAfter;
    private static bool previewAttempted;
    private static int previewSlot = -1;
    private static HeaderMode headerMode;

    private enum CompletionMessage
    {
        None,
        Save,
        Load,
        Delete
    }

    internal static HeaderMode CurrentHeaderMode => headerMode;

    internal static void Open()
    {
        if (!rowLoadAttempted)
        {
            rowLoadAttempted = true;
            Plugin.Guard("preset-row-load", EnsureObjectPreviewTemplate);
        }
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            Plugin.Warn("PresetDialogProbeFailed", new { reason = "UIManager unavailable" });
            return;
        }

        SetHeaderMode(HeaderMode.None);
        presetUiOpen = true;
        NativeDecorUi.RefreshFooterForPresetState();
        choiceCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)OnChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        ids.Add(NativeDecorUi.PresetSaveTextId);
        ids.Add(NativeDecorUi.PresetLoadTextId);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, choiceCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDialogData(choices);
        manager.OpenDialog(UILoadKey.SelectDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    private static void EnsureObjectPreviewTemplate()
    {
        var prefabs = UnityEngine.Object.FindObjectOfType<UIPrefabsManager>();
        if (prefabs == null)
        {
            Plugin.Warn("PresetStockRowLoadUnavailable", new { reason = "UIPrefabsManager unavailable" });
            return;
        }

        var key = UILoadKey.UIBazaarMaxPriceCustomLogPage;
        rowLoadedCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)(() => Plugin.Guard("preset-row-load-complete", OnStockRowsLoaded)));
        prefabs.Load(key, rowLoadedCallback);
    }

    private static void OnStockRowsLoaded() => previewAttempted = false;

    internal static void Tick()
    {
        if (completionDialogOpen)
        {
            // MessageDialogSmall is opened through the game's asynchronous
            // dialog queue and is not registered in UIDialogManager's page
            // lookup while it is visible.  Find its live default-dialog
            // instance instead; this still operates only on the spawned UI.
            var completionDialog = Resources.FindObjectsOfTypeAll<UIDefaultDialog>()
                .LastOrDefault(dialog => dialog != null && dialog.gameObject.activeInHierarchy);
            var completionVisible = completionDialog != null;
            completionDialogFrames++;
            if (completionVisible)
            {
                completionDialogSeen = true;
                ApplyCompletionText(completionDialog);
            }
            if (completionDialogSeen && !completionVisible)
            {
                completionDialogOpen = false;
                EndPresetUi();
            }
            else if (!completionDialogSeen && completionDialogFrames > 120)
            {
                completionDialogOpen = false;
                Plugin.Warn("PresetCompletionDialogUnavailable", new { pendingCompletion = pendingCompletion.ToString() });
                EndPresetUi();
            }
            return;
        }
        if (finishPresetAfterName)
        {
            if (Resources.FindObjectsOfTypeAll<UIInputDialog>()
                .Any(item => item != null && item.gameObject.activeInHierarchy)) return;
            finishPresetAfterName = false;
            OpenCompletionDialog();
            return;
        }

        if (returnToSaveSlots)
        {
            if (Resources.FindObjectsOfTypeAll<UIInputDialog>()
                .Any(item => item != null && item.gameObject.activeInHierarchy)) return;
            returnToSaveSlots = false;
            Plugin.Guard("preset-name-return-to-slots", OpenSlotMenu);
            return;
        }
        if (nameInputOpen)
        {
            if (!nameFooterRequested)
                Plugin.Guard("preset-name-guide", RefreshNameInputFooter);
        }
        if (!slotMenuOpen) return;
        if (slotDialog == null || !slotDialog.gameObject.activeInHierarchy)
            slotDialog = Resources.FindObjectsOfTypeAll<UISelectDialog>()
                .LastOrDefault(item => item != null && item.gameObject.activeInHierarchy &&
                    item.GetComponentsInChildren<UIDialogChoiceBar>(true)
                        .Count(bar => bar != null && bar.gameObject.activeInHierarchy) == PresetStorage.UiSlotCount + 1);
        var dialog = slotDialog;
        if (dialog == null) return;
        if (objectPreview != null)
        {
            if (!savingSlot)
            {
                var focused = dialog.GetComponentsInChildren<UIDialogChoiceBar>(true)
                    .FirstOrDefault(bar => bar.gameObject.activeInHierarchy && bar.IsFocused);
                int index = focused?.data?.id ?? -1;
                if (index >= 0 && index < PresetStorage.UiSlotCount && index != previewSlot)
                    Plugin.Guard("preset-object-preview-refresh", () => RefreshObjectPreview(index));
            }
            return;
        }
        if (previewAttempted) return;
        previewAttempted = true;
        Plugin.Guard("preset-object-preview", () =>
        {
            try { BuildObjectPreview(dialog); }
            catch
            {
                RemoveObjectPreview();
                throw;
            }
        });
    }

    private static void BuildObjectPreview(UIDialog dialog)
    {
        var prefabs = UnityEngine.Object.FindObjectOfType<UIPrefabsManager>();
        var page = prefabs?.UIPagePrefabCache(UILoadKey.UIBazaarMaxPriceCustomLogPage);
        var sourceRows = page?.GetComponentsInChildren<UICustomPartsListItem>(true);
        if (sourceRows == null || sourceRows.Length != 11)
        {
            Plugin.Warn("PresetObjectPreviewUnavailable", new { reason = "stock rows unavailable", count = sourceRows?.Length ?? 0 });
            return;
        }

        var source = sourceRows[0].transform.parent;
        var dialogRect = dialog.GetComponent<RectTransform>();
        var targetParent = dialogRect?.parent;
        if (source == null || targetParent == null) return;

        objectPreview = UnityEngine.Object.Instantiate(source.gameObject, targetParent, false);
        objectPreview.name = "BDT_PresetObjectPreview";
        var previewRect = objectPreview.GetComponent<RectTransform>();
        previewRect.anchorMin = new Vector2(0.5f, 0.5f);
        previewRect.anchorMax = new Vector2(0.5f, 0.5f);
        previewRect.pivot = new Vector2(0.5f, 0.5f);
        // Bring the record-style object panel closer to the slot list while
        // retaining a small visual gutter between the two official layouts.
        previewRect.anchoredPosition = new Vector2(390f, 0f);
        // This is the stock highest-sales-record list at its native scale.
        // Keeping that scale makes object icons and names readable in both
        // preset save and load previews.
        previewRect.localScale = Vector3.one;
        previewRect.SetAsLastSibling();
        // The clone already carries the official highest-sales-record panel
        // background. Do not tint it: a custom alpha/color made this panel
        // darker and slightly transparent than its stock counterpart.
        var canvasGroup = objectPreview.GetComponent<CanvasGroup>() ?? objectPreview.AddComponent<CanvasGroup>();
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;

        shiftedDialog = dialogRect;
        originalDialogPosition = dialogRect.anchoredPosition;
        dialogRect.anchoredPosition = originalDialogPosition + new Vector2(-420f, 0f);

        var focused = dialog.GetComponentsInChildren<UIDialogChoiceBar>(true)
            .FirstOrDefault(bar => bar.gameObject.activeInHierarchy && bar.IsFocused);
        var focusedIndex = focused?.data?.id ?? 0;
        RefreshObjectPreview(savingSlot ? -1 : Math.Clamp(focusedIndex, 0, PresetStorage.UiSlotCount - 1));
    }

    private static void RefreshObjectPreview(int uiSlot)
    {
        if (objectPreview == null) return;
        previewSlot = uiSlot;
        var prefabs = UnityEngine.Object.FindObjectOfType<UIPrefabsManager>();
        var page = prefabs?.UIPagePrefabCache(UILoadKey.UIBazaarMaxPriceCustomLogPage);
        var rows = objectPreview.GetComponentsInChildren<UICustomPartsListItem>(true);
        // The official record page owns the slot order. Read its serialized list
        // instead of assuming that an enum's numeric order will always match it.
        var officialOrder = page.GetComponent<UIBazaarMaxPriceCustomLogPage>()?.pageToCategoryList;
        var useOfficialOrder = officialOrder != null && officialOrder.Count == rows.Length;
        var fallbackOrder = new[]
        {
            BazaarCustomPageCategory.Tent,
            BazaarCustomPageCategory.ShelfLeft,
            BazaarCustomPageCategory.ShelfCenter,
            BazaarCustomPageCategory.ShelfRight,
            BazaarCustomPageCategory.OrnamentSLeftOutSide,
            BazaarCustomPageCategory.OrnamentSLeftInSide,
            BazaarCustomPageCategory.OrnamentSRightInSide,
            BazaarCustomPageCategory.OrnamentSRightOutSide,
            BazaarCustomPageCategory.OrnamentLLeft,
            BazaarCustomPageCategory.OrnamentLRightInside,
            BazaarCustomPageCategory.OrnamentLRightOutside
        };
        var itemMaster = BokuMono.API.Bazaar.MDM?.ItemMaster;
        ItemMasterData representativeItem = null;
        var partsMaster = BokuMono.API.Bazaar.MDM?.CustomPartsMaster;
        if (itemMaster != null && partsMaster?.list != null)
        {
            foreach (var part in partsMaster.list)
            {
                if (part != null && itemMaster.TryGetData(part.Id, out var candidate) && candidate != null)
                {
                    representativeItem = candidate;
                    break;
                }
            }
        }
        var shown = 0;
        var emptyWithCategoryIcon = 0;
        var orderNames = new string[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var slot = useOfficialOrder ? officialOrder[i] : fallbackOrder[i];
            orderNames[i] = slot.ToString();
            var category = BazaarCustomItemData.ToPartsCategory(slot);
            var slotIndex = BazaarCustomItemData.ToPartsCategoryIndex(slot);
            var id = savingSlot ? Prototype.AppearanceId(category, slotIndex) :
                Prototype.UiSlotAppearanceId(uiSlot, category, slotIndex);
            if (id != 0 && itemMaster != null && itemMaster.TryGetData(id, out var item) && item != null)
            {
                rows[i].SetDisp(i, item);
                rows[i].partsIcon.enabled = true;
                rows[i].gameObject.SetActive(true);
                shown++;
            }
            else
            {
                // SetDisp also initializes the position's round icon. Use a
                // temporary valid item only for that setup, then hide its
                // object artwork and replace its name with an empty marker.
                var actualId = Prototype.ActualAppearanceId(category, slotIndex);
                var iconSeed = representativeItem;
                if (actualId != 0 && itemMaster != null &&
                    itemMaster.TryGetData(actualId, out var actualItem) && actualItem != null)
                    iconSeed = actualItem;
                if (iconSeed != null) rows[i].SetDisp(i, iconSeed);
                rows[i].partsName.text = "—";
                rows[i].partsIcon.enabled = false;
                if (rows[i].partsCategoryIcon != null)
                {
                    rows[i].partsCategoryIcon.gameObject.SetActive(true);
                    rows[i].partsCategoryIcon.enabled = true;
                    if (rows[i].partsCategoryIcon.sprite != null) emptyWithCategoryIcon++;
                }
                rows[i].gameObject.SetActive(true);
            }
        }
    }

    private static void RemoveObjectPreview(bool restoreDialogPosition = true)
    {
        if (restoreDialogPosition && shiftedDialog != null)
            shiftedDialog.anchoredPosition = originalDialogPosition;
        else if (!restoreDialogPosition && shiftedDialog != null)
        {
            closingShiftedDialog = shiftedDialog;
            closingDialogOriginalPosition = originalDialogPosition;
        }
        shiftedDialog = null;
        if (objectPreview != null) UnityEngine.Object.Destroy(objectPreview);
        objectPreview = null;
        previewSlot = -1;
        previewAttempted = false;
        slotDialog = null;
    }

    private static void RestoreClosedSlotDialogPosition()
    {
        if (closingShiftedDialog != null)
            closingShiftedDialog.anchoredPosition = closingDialogOriginalPosition;
        closingShiftedDialog = null;
    }

    private static void CloseSlotDialog(UIManager manager, Il2CppSystem.Action after)
    {
        if (manager == null)
        {
            RestoreClosedSlotDialogPosition();
            return;
        }
        slotDialogClosedAfter = after;
        slotDialogClosedCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnSlotDialogClosed);
        manager.CloseDialog(null, slotDialogClosedCallback, true, true, false);
    }

    private static void OnSlotDialogClosed()
    {
        RestoreClosedSlotDialogPosition();
        var after = slotDialogClosedAfter;
        slotDialogClosedAfter = null;
        if (after != null) after.Invoke();
    }

    private static bool CancelActiveMenu()
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null) return false;

        if (slotMenuOpen)
        {
            slotMenuOpen = false;
            // Keep the shifted position for this dialog's closing animation.
            RemoveObjectPreview(restoreDialogPosition: false);
            SetHeaderMode(HeaderMode.None);
            reopenMenuAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)Open);
            CloseSlotDialog(manager, reopenMenuAfterDialog);
            return true;
        }
        // The final argument is isMaskLeave.  It must be false so this dialog owns
        // and removes its translucent mask instead of leaving one behind.
        closePresetAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)EndPresetUi);
        manager.CloseDialog(null, closePresetAfterDialog, true, true, false);
        return true;
    }

    private static void OnChoice(int index)
    {
        if (index == 2)
        {
            CancelActiveMenu();
            return;
        }
        if (index < 0 || index > 1) return;
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            Plugin.Warn("PresetDialogProbeFailed", new { reason = "UIManager unavailable while closing" });
            return;
        }
        savingSlot = index == 0;
        SetHeaderMode(savingSlot ? HeaderMode.Save : HeaderMode.Load);
        openSlotsAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenSlotMenu);
        manager.CloseDialog(null, openSlotsAfterDialog, true, true, false);
    }

    private static void OpenSlotMenu()
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null) return;
        slotMenuOpen = true;
        slotDialog = null;
        previewAttempted = false;
        slotCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)OnSlotChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        for (var i = 0; i < PresetStorage.UiSlotCount; i++) ids.Add(NativeDecorUi.PresetEmptySlotTextId + (uint)i);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, slotCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDialogData(choices);
        manager.OpenDialog(UILoadKey.SelectDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    // Called from the same controllable-UI route as the stock Y/LShift action.
    // It only claims the input while our four-row slot selector is actually open.
    internal static bool TryOpenDeleteForFocusedSlot()
    {
        if (!slotMenuOpen || deleteConfirmOpen) return false;
        if (slotDialog == null || !slotDialog.gameObject.activeInHierarchy) return false;
        var focused = slotDialog.GetComponentsInChildren<UIDialogChoiceBar>(true)
            .FirstOrDefault(bar => bar != null && bar.gameObject.activeInHierarchy && bar.IsFocused);
        var index = focused?.data?.id ?? -1;
        if (index < 0 || index >= PresetStorage.UiSlotCount) return true;

        var preset = Prototype.UiSlotPreset(index);
        if (preset == null)
        {
            return true;
        }

        deleteTargetSlot = index;
        deleteTargetName = preset.Name;
        slotMenuOpen = false;
        // Keep the shifted position for this dialog's closing animation.
        RemoveObjectPreview(restoreDialogPosition: false);
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            RestoreClosedSlotDialogPosition();
            Plugin.Warn("PresetUiDeleteUnavailable", new { slot = index + 1, reason = "UIManager unavailable" });
            EndPresetUi();
            return true;
        }
        openDeleteConfirmationAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenDeleteConfirmation);
        CloseSlotDialog(manager, openDeleteConfirmationAfterDialog);
        return true;
    }

    // UIDialog marks its choice group as completed before it invokes the
    // ChoicesData callback. Intercept an empty load here so the dialog never
    // enters that completed state; the selection remains focused and usable.
    internal static bool TryConsumeEmptyLoadChoice(UIDialog dialog, int index)
    {
        if (savingSlot || !slotMenuOpen || dialog == null || slotDialog == null || index < 0 || index >= PresetStorage.UiSlotCount)
            return false;
        if (IL2CPP.Il2CppObjectBaseToPtr(dialog) != IL2CPP.Il2CppObjectBaseToPtr(slotDialog))
            return false;
        if (Prototype.UiSlotPreset(index) != null) return false;
        return true;
    }

    private static Il2CppSystem.Action openDeleteConfirmationAfterDialog;
    private static Il2CppSystem.Action reopenSlotsAfterDeleteDialog;

    private static void OpenDeleteConfirmation()
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null || deleteTargetSlot < 0 || deleteTargetSlot >= PresetStorage.UiSlotCount)
        {
            Plugin.Warn("PresetUiDeleteUnavailable", new { slot = deleteTargetSlot + 1, reason = "confirmation unavailable" });
            EndPresetUi();
            return;
        }
        deleteConfirmOpen = true;
        deleteCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)OnDeleteChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        ids.Add(StockYesChoiceTextId);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, deleteCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDefaultDialogData(DeleteConfirmTextId, choices, new Il2CppStringArray(0L));
        manager.OpenDialog(UILoadKey.DefaultDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    private static void OnDeleteChoice(int index)
    {
        var slot = deleteTargetSlot;
        deleteConfirmOpen = false;
        deleteTargetSlot = -1;
        deleteTargetName = null;
        if (index == 0)
        {
            var deleted = false;
            Plugin.Guard("preset-ui-delete", () => deleted = Prototype.DeleteUiSlot(slot));
            if (deleted)
            {
                pendingCompletion = CompletionMessage.Delete;
                reopenSlotsAfterCompletion ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenSlotMenu);
                completionAfterClose = reopenSlotsAfterCompletion;
            }
        }
        else
        {
        }

        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            EndPresetUi();
            return;
        }
        if (pendingCompletion == CompletionMessage.Delete)
        {
            openCompletionAfterDeleteDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenCompletionDialog);
            manager.CloseDialog(null, openCompletionAfterDeleteDialog, true, true, false);
        }
        else
        {
            reopenSlotsAfterDeleteDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenSlotMenu);
            manager.CloseDialog(null, reopenSlotsAfterDeleteDialog, true, true, false);
        }
    }

    private static void OnSlotChoice(int index)
    {
        if (index == PresetStorage.UiSlotCount)
        {
            CancelActiveMenu();
            return;
        }
        if (index < 0 || index >= PresetStorage.UiSlotCount) return;
        if (!savingSlot && Prototype.UiSlotPreset(index) == null)
        {
            // Keep the official select dialog alive. Reopening it merely to
            // reject an empty load causes a distracting close/open animation.
            return;
        }
        slotMenuOpen = false;
        // Keep the shifted position for this dialog's closing animation.
        RemoveObjectPreview(restoreDialogPosition: false);
        selectedSlot = index;
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            RestoreClosedSlotDialogPosition();
            EndPresetUi();
            return;
        }
        if (savingSlot)
        {
            openNameAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OpenNameInput);
            CloseSlotDialog(manager, openNameAfterDialog);
        }
        else
        {
            loadSlotAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)LoadSelectedSlot);
            CloseSlotDialog(manager, loadSlotAfterDialog);
        }
    }

    private static void LoadSelectedSlot()
    {
        if (!Prototype.LoadUiSlot(selectedSlot))
        {
            EndPresetUi();
            return;
        }
        pendingCompletion = CompletionMessage.Load;
        OpenCompletionDialog();
    }

    private static void OpenNameInput()
    {
        var keyboard = UnityEngine.Object.FindObjectOfType<KeyboardManager>();
        if (keyboard == null)
        {
            Plugin.Warn("PresetNameProbeFailed", new { reason = "KeyboardManager unavailable" });
            EndPresetUi();
            return;
        }
        inputCallback ??= DelegateSupport.ConvertDelegate<KeyboardManager.InputCompleteCallback>(
            (Action<KeyboardManager.Result, string>)OnNameInputCompleted);
        inputCancelled ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)OnNameInputCancelled);
        nameInputOpen = true;
        nameFooterRequested = false;
        try
        {
            keyboard.ShowRequest(KeyboardManager.KeyboardType.BuyPetAnimal, string.Empty, inputCallback,
                inputCancelled, false, true);
        }
        catch
        {
            nameInputOpen = false;
            EndPresetUi();
            throw;
        }
    }

    private static void OnNameInputCompleted(KeyboardManager.Result result, string input)
    {
        if (result == KeyboardManager.Result.Success)
        {
            nameInputOpen = false;
            RestoreNameInputFooter();
            var saved = false;
            Plugin.Guard("preset-ui-save", () => saved = Prototype.SaveUiSlot(selectedSlot, input));
            if (saved)
            {
                pendingCompletion = CompletionMessage.Save;
                finishPresetAfterName = true;
            }
            else EndPresetUi();
        }
        else if (result == KeyboardManager.Result.Cancel)
            OnNameInputCancelled();
    }

    private static void OnNameInputCancelled()
    {
        if (!nameInputOpen) return;
        nameInputOpen = false;
        RestoreNameInputFooter();
        returnToSaveSlots = true;
        Plugin.Guard("preset-name-cancel-sound", () =>
            UnityEngine.Object.FindObjectOfType<UIAccessor>()?.PlaySe(UISoundTypes.Cancel));
    }

    private static void RefreshNameInputFooter()
    {
        var footers = Resources.FindObjectsOfTypeAll<UIMenuFooter>();
        foreach (var footer in footers)
        {
            if (footer == null || !footer.gameObject.activeInHierarchy ||
                footer.guideTarget == null || footer.LastGuideId < 0) continue;
            if (!footer.guideTarget.GetComponentsInChildren<UIButtonGuide>(true)
                .Any(guide => guide != null && guide.gameObject.activeInHierarchy &&
                    guide.button == GuideKey.East)) continue;
            nameFooterRequested = true;
            footer.SetGuide((KeyButtonGuideMasterId)(uint)footer.LastGuideId);
            return;
        }
    }

    private static void RestoreNameInputFooter()
    {
        nameFooterRequested = false;
    }

    private static void OpenCompletionDialog()
    {
        var completion = pendingCompletion;
        pendingCompletion = CompletionMessage.None;
        if (completion == CompletionMessage.None)
        {
            EndPresetUi();
            return;
        }
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            Plugin.Warn("PresetCompletionDialogUnavailable", new { reason = "UIManager unavailable", completion = completion.ToString() });
            EndPresetUi();
            return;
        }

        var textId = completion == CompletionMessage.Save ? SaveCompletedTextId :
            completion == CompletionMessage.Load ? LoadCompletedTextId : DeleteCompletedTextId;
        activeCompletion = completion;
        completionDialogOpen = true;
        completionDialogSeen = false;
        completionTextReported = false;
        completionDialogFrames = 0;
        try
        {
            // MessageDialogSmall is backed by UIDefaultDialog and requires
            // UIDefaultDialogData. Even a notification with no visible choices
            // still needs a non-null ChoicesData contract for its input setup.
            completionCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(
                (Action<int>)OnCompletionChoice);
            var ids = new Il2CppSystem.Collections.Generic.List<uint>();
            var choices = new ChoicesData(ids, completionCallback, LocalizeTextTableType.DialogChoiceText);
            var data = new UIDefaultDialogData(textId, choices, new Il2CppStringArray(0L));
            manager.OpenDialog(UILoadKey.MessageDialogSmall, data, DialogMaskType.Translucent,
                UIDialogManager.AnchorType.Center, false, true, null, null);
        }
        catch (Exception ex)
        {
            completionDialogOpen = false;
            Plugin.Warn("PresetCompletionDialogUnavailable", new { completion = completion.ToString(), error = ex.Message });
            EndPresetUi();
        }
    }

    private static void OnCompletionChoice(int index)
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            Plugin.Warn("PresetCompletionDialogCloseUnavailable", new { reason = "UIManager unavailable" });
            return;
        }

        // A zero-choice UIDefaultDialog forwards B/Esc to this callback but
        // does not close itself. Close it once through the same UIManager path,
        // then either restore appearance mode or return to the delete slot list.
        completionClosedAfter ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnCompletionClosed);
        manager.CloseDialog(null, completionClosedAfter, true, true, false);
    }

    private static void OnCompletionClosed()
    {
        var after = completionAfterClose;
        completionAfterClose = null;
        pendingCompletion = CompletionMessage.None;
        activeCompletion = CompletionMessage.None;
        completionDialogOpen = false;
        completionDialogSeen = false;
        completionTextReported = false;
        completionDialogFrames = 0;
        if (after != null) after.Invoke();
        else EndPresetUi();
    }

    private static void ApplyCompletionText(UIDefaultDialog completionDialog)
    {
        if (completionDialog == null || activeCompletion == CompletionMessage.None) return;
        var text = Localization.Get(activeCompletion == CompletionMessage.Save
            ? "presets.save.completed"
            : activeCompletion == CompletionMessage.Load
                ? "presets.load.completed"
                : "presets.delete.completed");
        // MessageDialogSmall is the stock UIDefaultDialog prefab.  Set only
        // this spawned dialog instance's body text; do not replace a game's
        // localization entry or alter the shared prefab.
        var body = completionDialog.infomationText;
        if (body == null)
        {
            if (!completionTextReported)
            {
                completionTextReported = true;
                Plugin.Warn("PresetCompletionDialogTextUnavailable", new
                {
                    dialog = completionDialog.gameObject.name
                });
            }
            return;
        }

        // The game can refresh the dialog during its opening animation, so
        // reapply the instance-local string while this short-lived dialog is
        // visible.  This never touches a global localization table.
        body.SetText(text, false);
        if (!completionTextReported)
        {
            completionTextReported = true;
        }
    }

    private static void EndPresetUi()
    {
        pendingCompletion = CompletionMessage.None;
        activeCompletion = CompletionMessage.None;
        completionAfterClose = null;
        completionDialogOpen = false;
        completionDialogSeen = false;
        completionTextReported = false;
        completionDialogFrames = 0;
        deleteConfirmOpen = false;
        deleteTargetSlot = -1;
        deleteTargetName = null;
        SetHeaderMode(HeaderMode.None);
        presetUiOpen = false;
        NativeDecorUi.RefreshFooterForPresetState();
    }

    private static void SetHeaderMode(HeaderMode value)
    {
        if (headerMode == value) return;
        headerMode = value;
        NativeDecorUi.RefreshModeTitleForPresetState();
    }

    internal static bool IsNameInputOpen => nameInputOpen;
    internal static bool IsPresetUiOpen => presetUiOpen;
    internal static bool IsCompletionDialogOpen => completionDialogOpen;
    internal static bool IsSlotMenuOpen => slotMenuOpen;

    internal static bool IsOwnNameInput(KeyboardManager keyboard)
    {
        if (!nameInputOpen || keyboard == null || inputCallback == null) return false;
        var activeCallback = keyboard.completeCallback;
        return activeCallback != null &&
            IL2CPP.Il2CppObjectBaseToPtr(activeCallback) == IL2CPP.Il2CppObjectBaseToPtr(inputCallback);
    }

    internal static bool TryGetNameText(uint textId, out string text)
    {
        // These are stock IDs.  Only substitute their displayed text while our
        // keyboard request is active; the game's other naming flows stay vanilla.
        if (!nameInputOpen)
        {
            text = null;
            return false;
        }
        if (textId == NameInputTextId)
        {
            text = Localization.Get("presets.name.prompt");
            return true;
        }
        if (textId == NameConfirmTextId)
        {
            var keyboard = UnityEngine.Object.FindObjectOfType<KeyboardManager>();
            var name = keyboard?.inputedText ?? string.Empty;
            text = Localization.Get("presets.name.confirm").Replace("{0}", name);
            return true;
        }
        text = null;
        return false;
    }

    internal static bool TryGetDeleteText(uint textId, out string text)
    {
        if (deleteConfirmOpen && textId == DeleteConfirmTextId)
        {
            text = Localization.Get("presets.delete.confirm").Replace("{0}", deleteTargetName ?? string.Empty);
            return true;
        }
        text = null;
        return false;
    }

}

// Override the computed cancel flag only for our own keyboard request.  The
// game's shared BuyPetAnimal master data is never changed.
[HarmonyPatch(typeof(KeyboardManager), "get_IsCancelButtonDisabled")]
internal static class PresetNameProbeCancel
{
    static void Postfix(KeyboardManager __instance, ref bool __result)
    {
        if (PresetUiProbe.IsOwnNameInput(__instance)) __result = false;
    }
}

[HarmonyPatch(typeof(UIDefaultDialog), nameof(UIDefaultDialog.IsEnabledInputEast))]
internal static class PresetCompletionDialogCancel
{
    static void Postfix(ref bool __result)
    {
        // Our direct message text has no DialogMaster record, so the stock
        // default-dialog check disables East/B.  Enable it only for our live
        // completion notice and let the game's own dialog path close it.
        if (PresetUiProbe.IsCompletionDialogOpen) __result = true;
    }
}

// A select-dialog decision normally completes the current choice group before
// its callback runs. Keep empty load slots from completing that group at all.
[HarmonyPatch(typeof(UIDialog), nameof(UIDialog.OnDecide))]
internal static class PresetEmptySlotDecisionGuard
{
    static bool Prefix(UIDialog __instance, int id) =>
        !PresetUiProbe.TryConsumeEmptyLoadChoice(__instance, id);
}
