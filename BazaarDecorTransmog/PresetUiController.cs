using static BazaarDecorTransmog.UiTextIds;
using BokuMono;
using BokuMono.Data;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarDecorTransmog;

// Owns the preset flow while reusing the game's dialogs and preview rows.
internal static partial class PresetUiController
{
    internal enum HeaderMode
    {
        None,
        Save,
        Load
    }

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
    private static UISelectDialog slotDialog;
    private static UIDialogChoiceBar[] slotChoiceBars;
    private static Il2CppSystem.Action slotDialogClosedCallback;
    private static Il2CppSystem.Action slotDialogClosedAfter;
    private static HeaderMode headerMode;
    private static int sessionGeneration;
    private static bool failed;
    private static bool failureClosePending;
    private static Il2CppSystem.Action failureClosedCallback;
    private static Action notificationAfterClose;

    private static Il2CppSystem.Action SessionCallback(Action action)
    {
        int ticket = sessionGeneration;
        return DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() =>
        {
            if (ticket == sessionGeneration && !failed)
                Plugin.Guard("preset-callback", action, Abort);
        }));
    }

    private static Il2CppSystem.Action<int> SessionCallback(Action<int> action)
    {
        int ticket = sessionGeneration;
        return DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((Action<int>)(index =>
        {
            if (ticket == sessionGeneration && !failed)
                Plugin.Guard("preset-choice", () => action(index), Abort);
        }));
    }

    internal static bool OwnsCompletion(UIDefaultDialog dialog) => completionDialogOpen &&
        dialog != null && dialog.infoId == (activeCompletion == CompletionMessage.Save ? PresetSaveCompletedTextId :
            activeCompletion == CompletionMessage.Load ? PresetLoadCompletedTextId :
            activeCompletion == CompletionMessage.SaveFailed ? SaveFailedTextId : DeleteCompletedTextId);

    private static bool HasChoiceId(UIDialog dialog, uint id) =>
        dialog.GetComponentsInChildren<UIDialogChoiceBar>(true).Any(bar =>
            // data is the UIData wrapper; cacheData is the ChoiceData populated
            // by the stock bar. Casting the wrapper never identifies our menu.
            bar != null && bar.gameObject.activeInHierarchy && bar.cacheData?.TextId == id);

    private static void CloseFailedDialog()
    {
        if (failureClosePending) return;
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null || !manager.IsDialog) return;
        // CloseDialog targets the current UI history entry. Check its key and
        // our unique IDs, not merely the existence of a dialog elsewhere.
        foreach (var dialog in Resources.FindObjectsOfTypeAll<UIDialog>())
        {
            if (dialog == null || !dialog.gameObject.activeInHierarchy || dialog.myKey != manager.CurrentUIKey) continue;
            var message = dialog.TryCast<UIDefaultDialog>();
            bool owned = message != null && (message.infoId == PresetSaveCompletedTextId ||
                message.infoId == PresetLoadCompletedTextId || message.infoId == DeleteCompletedTextId ||
                message.infoId == DeleteConfirmTextId || message.infoId == SaveFailedTextId ||
                !AppearanceSession.Enabled && message.infoId == AppearanceExitConfirmTextId);
            if (!owned && dialog.TryCast<UISelectDialog>() != null)
                owned = HasChoiceId(dialog, PresetSaveTextId) || HasChoiceId(dialog, PresetEmptySlotTextId);
            if (!owned) continue;
            failureClosedCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)(() => failureClosePending = false));
            failureClosePending = true;
            try { manager.CloseDialog(null, failureClosedCallback, true, false, false); }
            catch { failureClosePending = false; throw; }
            return;
        }
    }

    internal static void Abort()
    {
        failed = true;
        sessionGeneration++;
        Recovery.Run(() =>
        {
            var keyboard = UnityEngine.Object.FindObjectOfType<KeyboardManager>();
            if (IsOwnNameInput(keyboard)) keyboard.SetResultCancel();
        }, CloseFailedDialog, EndPresetUi);
    }

    private enum CompletionMessage
    {
        None,
        Save,
        Load,
        Delete,
        SaveFailed
    }

    internal static HeaderMode CurrentHeaderMode => headerMode;

    internal static void ShowSaveFailure(Action afterClose)
    {
        if (failed || !AppearanceSession.Enabled) { afterClose?.Invoke(); return; }
        notificationAfterClose = afterClose;
        presetUiOpen = true;
        pendingCompletion = CompletionMessage.SaveFailed;
        OpenCompletionDialog();
    }

    internal static void Open()
    {
        if (failed || !AppearanceSession.Enabled) return;
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
        AppearanceEditorUi.RefreshFooterForPresetState();
        choiceCallback ??= SessionCallback(OnChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        ids.Add(PresetSaveTextId);
        ids.Add(PresetLoadTextId);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, choiceCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDialogData(choices);
        manager.OpenDialog(UILoadKey.SelectDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    internal static void Tick()
    {
        if (failed) { CloseFailedDialog(); return; }
        if (completionDialogOpen)
        {
            // MessageDialogSmall is opened through the game's asynchronous
            // dialog queue and is not registered in UIDialogManager's page
            // lookup while it is visible.  Find its live default-dialog
            // instance instead; this still operates only on the spawned UI.
            var completionDialog = Resources.FindObjectsOfTypeAll<UIDefaultDialog>()
                .LastOrDefault(dialog => OwnsCompletion(dialog) && dialog.gameObject.activeInHierarchy);
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
                Plugin.Warn("PresetCompletionDialogUnavailable", new { completion = activeCompletion.ToString(), reason = "open timed out" });
                // OpenDialog may still be queued. Normal teardown invalidates
                // its callback but cannot cancel that request. Abort keeps Tick
                // watching for our late dialog and closes it through UIManager.
                Abort();
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
        {
            slotChoiceBars = null;
            slotDialog = Resources.FindObjectsOfTypeAll<UISelectDialog>()
                .LastOrDefault(item => item != null && item.gameObject.activeInHierarchy &&
                    HasChoiceId(item, PresetEmptySlotTextId));
        }
        var dialog = slotDialog;
        if (dialog == null) return;
        if (objectPreview != null)
        {
            if (!savingSlot)
            {
                var focused = FocusedSlotChoice();
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

    private static UIDialogChoiceBar FocusedSlotChoice()
    {
        if (slotDialog == null) return null;
        // The pooled dialog builds its bars before it is selected in Tick.
        // Keep them only for this opening; a destroyed bar invalidates the cache.
        if (slotChoiceBars != null)
            foreach (var bar in slotChoiceBars)
                if (bar == null) { slotChoiceBars = null; break; }
        slotChoiceBars ??= slotDialog.GetComponentsInChildren<UIDialogChoiceBar>(true).ToArray();
        foreach (var bar in slotChoiceBars)
            if (bar != null && bar.gameObject.activeInHierarchy && bar.IsFocused) return bar;
        return null;
    }

    private static void CloseSlotDialog(UIManager manager, Il2CppSystem.Action after)
    {
        if (manager == null)
        {
            RestoreClosedSlotDialogPosition();
            return;
        }
        slotDialogClosedAfter = after;
        slotDialogClosedCallback ??= SessionCallback(OnSlotDialogClosed);
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
            reopenMenuAfterDialog ??= SessionCallback(Open);
            CloseSlotDialog(manager, reopenMenuAfterDialog);
            return true;
        }
        // The final argument is isMaskLeave.  It must be false so this dialog owns
        // and removes its translucent mask instead of leaving one behind.
        closePresetAfterDialog ??= SessionCallback(EndPresetUi);
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
        openSlotsAfterDialog ??= SessionCallback(OpenSlotMenu);
        manager.CloseDialog(null, openSlotsAfterDialog, true, true, false);
    }

    private static void OpenSlotMenu()
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null) return;
        slotMenuOpen = true;
        slotDialog = null;
        slotChoiceBars = null;
        previewAttempted = false;
        slotCallback ??= SessionCallback(OnSlotChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        for (var i = 0; i < PresetStorage.UiSlotCount; i++) ids.Add(PresetEmptySlotTextId + (uint)i);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, slotCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDialogData(choices);
        manager.OpenDialog(UILoadKey.SelectDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    // Called from the same controllable-UI route as the stock Y/LShift action.
    // It only claims the input while our slot selector is actually open.
    internal static bool TryOpenDeleteForFocusedSlot()
    {
        if (!slotMenuOpen || deleteConfirmOpen) return false;
        if (slotDialog == null || !slotDialog.gameObject.activeInHierarchy) return false;
        var focused = FocusedSlotChoice();
        var index = focused?.data?.id ?? -1;
        if (index < 0 || index >= PresetStorage.UiSlotCount) return true;

        var preset = AppearanceSession.UiSlotPreset(index);
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
        openDeleteConfirmationAfterDialog ??= SessionCallback(OpenDeleteConfirmation);
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
        if (AppearanceSession.UiSlotPreset(index) != null) return false;
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
        deleteCallback ??= SessionCallback(OnDeleteChoice);
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
            Plugin.Guard("preset-ui-delete", () => deleted = AppearanceSession.DeleteUiSlot(slot));
            if (!failed && AppearanceSession.Enabled)
            {
                pendingCompletion = deleted ? CompletionMessage.Delete : CompletionMessage.SaveFailed;
                reopenSlotsAfterCompletion ??= SessionCallback(OpenSlotMenu);
                completionAfterClose = reopenSlotsAfterCompletion;
            }
        }

        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            EndPresetUi();
            return;
        }
        if (pendingCompletion == CompletionMessage.Delete || pendingCompletion == CompletionMessage.SaveFailed)
        {
            openCompletionAfterDeleteDialog ??= SessionCallback(OpenCompletionDialog);
            manager.CloseDialog(null, openCompletionAfterDeleteDialog, true, true, false);
        }
        else
        {
            reopenSlotsAfterDeleteDialog ??= SessionCallback(OpenSlotMenu);
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
        if (!savingSlot && AppearanceSession.UiSlotPreset(index) == null)
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
            openNameAfterDialog ??= SessionCallback(OpenNameInput);
            CloseSlotDialog(manager, openNameAfterDialog);
        }
        else
        {
            loadSlotAfterDialog ??= SessionCallback(LoadSelectedSlot);
            CloseSlotDialog(manager, loadSlotAfterDialog);
        }
    }

    private static void LoadSelectedSlot()
    {
        if (!AppearanceSession.LoadUiSlot(selectedSlot))
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
        int ticket = sessionGeneration;
        inputCallback ??= DelegateSupport.ConvertDelegate<KeyboardManager.InputCompleteCallback>(
            (Action<KeyboardManager.Result, string>)((result, text) =>
            {
                if (ticket == sessionGeneration && !failed)
                    Plugin.Guard("preset-name-complete", () => OnNameInputCompleted(result, text), Abort);
            }));
        inputCancelled ??= SessionCallback(OnNameInputCancelled);
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
            Plugin.Guard("preset-ui-save", () => saved = AppearanceSession.SaveUiSlot(selectedSlot, input));
            if (saved)
            {
                pendingCompletion = CompletionMessage.Save;
                finishPresetAfterName = true;
            }
            else if (!failed && AppearanceSession.Enabled)
            {
                // Wait for the keyboard UI to close just as on successful save.
                pendingCompletion = CompletionMessage.SaveFailed;
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

        var textId = completion == CompletionMessage.Save ? PresetSaveCompletedTextId :
            completion == CompletionMessage.Load ? PresetLoadCompletedTextId :
            completion == CompletionMessage.SaveFailed ? SaveFailedTextId : DeleteCompletedTextId;
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
            completionCallback ??= SessionCallback(OnCompletionChoice);
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
        completionClosedAfter ??= SessionCallback(OnCompletionClosed);
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
        var text = activeCompletion == CompletionMessage.SaveFailed ? SaveFailureText : Localization.Get(activeCompletion == CompletionMessage.Save
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
        if (body.text != text) body.SetText(text, false);
        if (!completionTextReported)
        {
            completionTextReported = true;
        }
    }

    private static void EndPresetUi()
    {
        var afterClose = notificationAfterClose;
        notificationAfterClose = null;
        sessionGeneration++;
        slotMenuOpen = nameInputOpen = finishPresetAfterName = returnToSaveSlots = nameFooterRequested = false;
        selectedSlot = -1;
        slotDialogClosedAfter = null;
        choiceCallback = slotCallback = completionCallback = deleteCallback = null;
        completionClosedAfter = openCompletionAfterDeleteDialog = reopenSlotsAfterCompletion = null;
        openSlotsAfterDialog = reopenMenuAfterDialog = openNameAfterDialog = loadSlotAfterDialog = null;
        closePresetAfterDialog = slotDialogClosedCallback = inputCancelled = null;
        openDeleteConfirmationAfterDialog = reopenSlotsAfterDeleteDialog = null;
        inputCallback = null;
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
        presetUiOpen = false;
        Recovery.Run(() => RemoveObjectPreview(), RestoreClosedSlotDialogPosition,
            () => SetHeaderMode(HeaderMode.None), AppearanceEditorUi.RefreshFooterForPresetState,
            () => afterClose?.Invoke());
    }

    internal static string SaveFailureText => Localization.GetOrFallback("save.failed",
        "保存できませんでした。詳しくはログをご確認ください。",
        "Could not save. Please check the log for details.");

    private static void SetHeaderMode(HeaderMode value)
    {
        if (headerMode == value) return;
        headerMode = value;
        AppearanceEditorUi.RefreshModeTitleForPresetState();
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
        if (PresetUiController.IsOwnNameInput(__instance)) __result = false;
    }
}

[HarmonyPatch(typeof(UIDefaultDialog), nameof(UIDefaultDialog.IsEnabledInputEast))]
internal static class PresetCompletionDialogCancel
{
    static void Postfix(UIDefaultDialog __instance, ref bool __result)
    {
        // Our direct message text has no DialogMaster record, so the stock
        // default-dialog check disables East/B.  Enable it only for our live
        // completion notice and let the game's own dialog path close it.
        if (PresetUiController.OwnsCompletion(__instance)) __result = true;
    }
}

// A select-dialog decision normally completes the current choice group before
// its callback runs. Keep empty load slots from completing that group at all.
[HarmonyPatch(typeof(UIDialog), nameof(UIDialog.OnDecide))]
internal static class PresetEmptySlotDecisionGuard
{
    static bool Prefix(UIDialog __instance, int id) =>
        !PresetUiController.TryConsumeEmptyLoadChoice(__instance, id);
}
