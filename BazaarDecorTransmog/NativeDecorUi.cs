using BokuMono;
using BokuMono.Data;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarDecorTransmog;

internal static class NativeDecorUi
{
    internal static bool Active { get; private set; }
    internal static bool Ready;
    private static UIBazaarCustomPage page;
    private static bool detailActive, effectsActive, detailIconActive, selectedItemUiActive;
    private static TMP_Text modeTitle;
    private static string originalTitle;
    private static Color originalTitleColor;
    private static Image modeBackground;
    private static Color originalBackgroundColor;
    private static Image modeIcon;
    private static Color originalIconColor;
    private static UIButtonGuide appearanceGuide;
    private static UIMenuFooter menuFooter;
    private const uint AppearanceGuideTextId = 0xBD700001;
    private const uint PresetsGuideTextId = 0xBD700003;
    // Confirmed from the stock appearance-mode B/Esc footer:
    // KeyButtonGuideText / "Cancel" ("やめる" in Japanese).
    internal const uint StockCancelFooterTextId = 1200;
    internal const uint PresetDeleteGuideTextId = 0xBD700011;
    internal const uint PresetSaveTextId = 0xBD700005;
    internal const uint PresetLoadTextId = 0xBD700006;
    // Keep the expandable slot-label range separate from the one-off UI
    // strings above. Slots 1-6 previously reached 0xBD70000E, which was the
    // existing name-input Cancel label.
    internal const uint PresetEmptySlotTextId = 0xBD700020;
    internal const uint PresetSaveCompletedTextId = 0xBD70000F;
    internal const uint PresetLoadCompletedTextId = 0xBD700010;
    private const uint AppearanceExitConfirmTextId = 0xBD700014;
    private const uint StockApplyAndReturnChoiceTextId = 1110;
    private const uint StockDiscardAndReturnChoiceTextId = 1120;
    private const uint StockCancelChoiceTextId = 1010;
    private static int lastToggleFrame = -1;
    private static BazaarCustomItemData lastFocusData;
    private static BazaarCustomPageCategory lastFocusCategory;
    // Used only while entering appearance mode to put the stock preview back on the
    // officially equipped model. The transmog preview then replaces that stable source.
    private static bool allowStockFocus;
    private static bool footerRefreshRequested;
    private static bool normalEditorDialogOpen;
    private static bool suppressAppearanceGuideUntilPageExit;
    private static int appearanceGuideResumeFrame = -1;
    private static bool guideSearchReported;
    private static bool titleApplied, titleSearchReported;
    private static bool modeTransitioning, transitionEntering;
    private static bool exitConfirmationOpen;
    private static bool exitPosePreserving;
    private static bool exitPoseHovered;
    private static Il2CppSystem.Action<int> exitChoiceCallback;
    private static Il2CppSystem.Action leaveAfterDialog;
    private static Il2CppSystem.Action restorePoseAfterDialog;
    private static Il2CppSystem.Action transitionAtBlack, transitionFinished;
    private const float ModeTransitionFadeSpeed = 0.20f;
    private static Sprite actualAppearanceBadgeSprite;
    private static Texture2D actualAppearanceBadgeTexture;
    private static readonly Dictionary<int, Sprite> whiteIconSprites = new();
    // Expand the veil along the icon silhouette, in source-texture pixels. This
    // covers the game's dark icon rim without making the whole card look larger.
    private const int ActualVeilOutlinePixels = 2;
    private static readonly Dictionary<BazaarCustomPageCategory, BazaarCustomItemData> actualChoiceData = new();
    private static readonly Dictionary<BazaarCustomPageCategory, BazaarCustomItemData> addedHiddenChoiceData = new();
    private static string note = "Select a supported decor tab. Confirm changes appearance only.";

    internal static void Observe(UIBazaarCustomPage value)
    {
        // A suppression requested by the previous page close must survive its
        // final footer rebuild, but never carry into a newly shown page.
        suppressAppearanceGuideUntilPageExit = false;
        appearanceGuideResumeFrame = -1;
        if (page != value) Exit();
        page = value;
    }

    internal static void Exit()
    {
        bool wasTransitioning = modeTransitioning;
        modeTransitioning = false;
        exitConfirmationOpen = false;
        exitPosePreserving = false;
        bool wasActive = Active;
        Active = false;
        RestoreChoiceLists();
        lastFocusData = null;
        RestoreModeTitle();
        RemoveAppearanceGuide();
        if (wasActive)
        {
            Prototype.EndNativePreview();
            Prototype.EndAppearanceSession();
        }
        if (wasActive && page != null)
        {
            RestoreDetailChildren();
            if (page.bazaarCustomDetail != null) page.bazaarCustomDetail.gameObject.SetActive(detailActive);
            if (page.bazaarEffectDetail != null) page.bazaarEffectDetail.gameObject.SetActive(effectsActive);
        }
        if (wasTransitioning && FadeManager.IsInstance)
            FadeManager.FadeIn(fadeSpeed: ModeTransitionFadeSpeed);
        note = Prototype.Enabled
            ? "Editor closed; Transmog remains ON outside the editor."
            : "Transmog OFF: the official list edits actual placement and effects.";
    }

    internal static bool CanToggle => Ready && Prototype.Enabled && page != null && page.gameObject.activeInHierarchy;
    internal static Transform PageTransform => page?.transform;

    internal static void EnterAppearanceFromWest()
    {
        if (!CanToggle || Active || modeTransitioning) return;
        if (lastToggleFrame == Time.frameCount) return;
        lastToggleFrame = Time.frameCount;
        TransitionAppearance(true);
    }

    internal static void LeaveAppearanceFromCancel()
    {
        if (!Active || modeTransitioning || exitConfirmationOpen) return;
        BeginExitPosePreservation();
        if (!Prototype.HasAppearanceSessionChanges())
        {
            FinishAppearanceSessionAndLeave();
            return;
        }
        OpenExitConfirmation();
    }

    private static void OpenExitConfirmation()
    {
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null) return;
        exitConfirmationOpen = true;
        exitChoiceCallback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(
            (Action<int>)OnExitChoice);
        var ids = new Il2CppSystem.Collections.Generic.List<uint>();
        ids.Add(StockApplyAndReturnChoiceTextId);
        ids.Add(StockDiscardAndReturnChoiceTextId);
        ids.Add(StockCancelChoiceTextId);
        var choices = new ChoicesData(ids, exitChoiceCallback, LocalizeTextTableType.DialogChoiceText);
        var data = new UIDefaultDialogData(AppearanceExitConfirmTextId, choices,
            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0L));
        manager.OpenDialog(UILoadKey.DefaultDialog, data, DialogMaskType.Translucent,
            UIDialogManager.AnchorType.Center, false, true, null, null);
    }

    private static void OnExitChoice(int index)
    {
        exitConfirmationOpen = false;
        bool leave = false;
        if (index == 0)
            leave = Prototype.SaveCurrentAppearance();
        else if (index == 1)
        {
            Prototype.RevertAppearanceSession();
            leave = true;
        }
        RestoreExitPose();

        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager == null)
        {
            if (leave) FinishAppearanceSessionAndLeave();
            else EndExitPosePreservation();
            return;
        }
        if (leave)
        {
            leaveAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)FinishAppearanceSessionAndLeave);
            manager.CloseDialog(null, leaveAfterDialog, true, true, false);
        }
        else
        {
            restorePoseAfterDialog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)RestoreExitPoseAndEndPreservation);
            manager.CloseDialog(null, restorePoseAfterDialog, true, true, false);
        }
    }

    private static void BeginExitPosePreservation()
    {
        exitPoseHovered = Prototype.CurrentEditorPoseHovered;
        exitPosePreserving = true;
        RestoreExitPose();
    }

    private static void RestoreExitPose()
    {
        if (exitPosePreserving) Prototype.RestoreCurrentEditorPose(exitPoseHovered);
    }

    private static void RestoreExitPoseAndEndPreservation()
    {
        RestoreExitPose();
        EndExitPosePreservation();
    }

    private static void EndExitPosePreservation() => exitPosePreserving = false;

    private static void FinishAppearanceSessionAndLeave()
    {
        RestoreExitPose();
        Prototype.EndAppearanceSession();
        TransitionAppearance(false);
    }

    internal static bool IsModeTransitioning => modeTransitioning;

    private static void TransitionAppearance(bool entering)
    {
        if (modeTransitioning) return;
        if (!FadeManager.IsInstance)
        {
            if (entering) EnterAppearance(); else LeaveAppearance();
            return;
        }

        modeTransitioning = true;
        transitionEntering = entering;
        transitionAtBlack ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)CompleteModeTransitionAtBlack);
        transitionFinished ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)CompleteModeTransition);
        FadeManager.FadeOut(FadeType.Black, afterCallback: transitionAtBlack, fadeSpeed: ModeTransitionFadeSpeed);
    }

    private static void CompleteModeTransitionAtBlack()
    {
        if (!modeTransitioning) return;
        if (transitionEntering) EnterAppearance(); else LeaveAppearance();
        if (FadeManager.IsInstance) FadeManager.FadeIn(afterCallback: transitionFinished, fadeSpeed: ModeTransitionFadeSpeed);
        else CompleteModeTransition();
    }

    private static void CompleteModeTransition() => modeTransitioning = false;

    internal static void Sync()
    {
        bool pageVisible = Ready && page != null && page.gameObject.activeInHierarchy;
        if (!Prototype.Enabled)
        {
            Exit();
            note = "Transmog OFF: the official list edits actual placement and effects.";
            return;
        }
        if (!pageVisible)
        {
            Exit();
            return;
        }
        RefreshFooterForDialogState();
        ApplyAppearanceGuide();
        if (Active)
        {
            ApplyModeTitle();

        }
    }

    private static void RefreshFooterForDialogState()
    {
        bool dialogOpen = false;
        if (!Active)
        {
            var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
            dialogOpen = manager != null && manager.TryGetDialogMask(out _);
        }
        if (dialogOpen != normalEditorDialogOpen)
        {
            normalEditorDialogOpen = dialogOpen;
            if (!dialogOpen && !suppressAppearanceGuideUntilPageExit)
                appearanceGuideResumeFrame = Time.frameCount + 2;
            RefreshOfficialFooter();
            return;
        }
        if (!dialogOpen && appearanceGuideResumeFrame >= 0 &&
            Time.frameCount >= appearanceGuideResumeFrame)
        {
            appearanceGuideResumeFrame = -1;
            RefreshOfficialFooter();
        }
    }

    internal static void ObserveStockObjectExitChoice(int index)
    {
        if (index == 0 || index == 1)
        {
            suppressAppearanceGuideUntilPageExit = true;
            appearanceGuideResumeFrame = -1;
        }
        else if (index == 2)
        {
            suppressAppearanceGuideUntilPageExit = false;
            appearanceGuideResumeFrame = Time.frameCount + 2;
        }
    }

    internal static void RefreshAfterPresetLoad()
    {
        if (!Active || page == null || !page.gameObject.activeInHierarchy) return;
        RefreshListDisplay();
        RefreshCheckmarks();
        FocusAppliedAppearance(page.selectTabCategory);
    }

    private static void EnterAppearance()
    {
        if (Active || !CanToggle) return;
        // Capture this before Active changes GetSetFocusId into the transmog selection.
        // The player may have hovered a different item in the normal editor, leaving
        // that item's model on screen even though it was never equipped.
        int actualFocusId = page.GetSetFocusId(page.selectTabCategory);
        detailActive = page.bazaarCustomDetail != null && page.bazaarCustomDetail.gameObject.activeSelf;
        effectsActive = page.bazaarEffectDetail != null && page.bazaarEffectDetail.gameObject.activeSelf;
        detailIconActive = page.bazaarCustomDetail?.icon != null && page.bazaarCustomDetail.icon.gameObject.activeSelf;
        selectedItemUiActive = page.bazaarCustomDetail?.selectedItemUI != null && page.bazaarCustomDetail.selectedItemUI.activeSelf;
        Active = true;
        ApplyModeTitle();
        ResetVanillaPreviewToActual(page.selectTabCategory, actualFocusId);
        EnsureChoiceLists();
        RefreshListDisplay();
        // Capture the lift baseline only after the stock preview is back on the actually
        // equipped model. Capturing the just-hovered model first made its existing lift
        // get counted again when the appearance preview was applied.
        Prototype.BeginNativePreview(page.selectTabCategory);
        Prototype.BeginAppearanceSession();
        RefreshCheckmarks();
        if (!FocusAppliedAppearance(page.selectTabCategory) && lastFocusData != null)
            Select(lastFocusData, lastFocusCategory, false);
        RefreshOfficialFooter();
        note = "Transmog ON: focus previews appearance; confirm keeps it in the draft.";
    }

    internal static void LeaveAppearance()
    {
        if (!Active) return;
        Active = false;
        RestoreChoiceLists();
        RestoreModeTitle();
        Prototype.EndNativePreview();
        Prototype.EndAppearanceSession();
        if (page != null)
        {
            RestoreDetailChildren();
            if (page.bazaarCustomDetail != null) page.bazaarCustomDetail.gameObject.SetActive(detailActive);
            if (page.bazaarEffectDetail != null) page.bazaarEffectDetail.gameObject.SetActive(effectsActive);
            RefreshListDisplay();
            RefreshCheckmarks();
            RestoreActualFocus();
            RestoreExitPose();
        }
        EndExitPosePreservation();
        RefreshOfficialFooter();
        note = "Editing actual placement and effects; Transmog remains ON outside the editor.";
    }

    internal static void Select(BazaarCustomItemData data, BazaarCustomPageCategory category, bool commit)
    {
        if (data != null && data.Category == category &&
            (IsActualChoice(data) || data.IsUiRemove ||
             data.PartsData?.Category == BazaarCustomItemData.ToPartsCategory(category)))
            UpdateAppearanceDetail(data);
        if (data != null && data.Category == category && IsActualChoice(data))
        {
            Prototype.NativeModeChoice(BazaarCustomItemData.ToPartsCategory(category),
                BazaarCustomItemData.ToPartsCategoryIndex(category), "Actual", commit);
            if (commit) RefreshCheckmarks();
            note = commit ? "Actual appearance kept in draft." : "Previewing the actual appearance.";
            return;
        }
        if (data != null && data.Category == category && data.IsUiRemove &&
            Prototype.AllowsHidden(BazaarCustomItemData.ToPartsCategory(category)))
        {
            Prototype.NativeModeChoice(BazaarCustomItemData.ToPartsCategory(category),
                BazaarCustomItemData.ToPartsCategoryIndex(category), "Hidden", commit);
            if (commit) RefreshCheckmarks();
            note = commit ? "Hidden appearance kept in draft." : "Previewing an empty appearance.";
            return;
        }
        if (data == null || data.PartsData == null || data.IsUiRemove ||
            (data.PartsData.Category != BazaarCustomItemData.PartsCategory.OrnamentS &&
             data.PartsData.Category != BazaarCustomItemData.PartsCategory.Tent &&
             data.PartsData.Category != BazaarCustomItemData.PartsCategory.OrnamentL &&
             data.PartsData.Category != BazaarCustomItemData.PartsCategory.Shelf) ||
            BazaarCustomItemData.ToPartsCategory(category) != data.PartsData.Category)
        {
            // Empty cells and a transient old-category focus are both emitted by the stock
            // list while changing tabs. They are not requests to leave appearance mode.
            note = data == null || data.PartsData == null || data.IsUiRemove
                ? "Empty has no appearance to preview; appearance mode remains active."
                : "Changing decor tab; appearance mode remains active.";
            return;
        }
        int index = BazaarCustomItemData.ToPartsCategoryIndex(category);
        int lastIndex = data.PartsData.Category == BazaarCustomItemData.PartsCategory.Tent ? 0 :
            data.PartsData.Category == BazaarCustomItemData.PartsCategory.OrnamentS ? 3 : 2;
        if (index < 0 || index > lastIndex) return;
        Prototype.NativeChoice(index, data.PartsData, commit);
        if (commit) RefreshCheckmarks();
        note = commit ? "Appearance kept in draft. Save preset to persist it." : "Appearance preview. Confirm to keep; Back cancels preview.";
        // High-frequency appearance diagnostics disabled after implementation validation.
    }

    private static void UpdateAppearanceDetail(BazaarCustomItemData data)
    {
        var detail = page?.bazaarCustomDetail;
        if (!Active || detail == null) return;
        detail.gameObject.SetActive(true);
        if (IsActualChoice(data))
        {
            detail.nameText?.SetText(Localization.Get("actual.title"));
            detail.caption?.SetText(Localization.Get("actual.caption"));
        }
        else if (data.IsUiRemove)
        {
            detail.nameText?.SetText(Localization.Get("hidden.title"));
            detail.caption?.SetText(Localization.Get("hidden.caption"));
        }
        else if (data.PartsData != null)
        {
            detail.SetData(data);
            // SetData has already localized the text, but its caption starts with three
            // generated rows for series, effect, and target. Appearance mode needs only
            // the remaining flavor description.
            if (detail.caption?.text is string caption)
                detail.caption.SetText(AppearanceFlavor(caption));
        }
        if (detail.icon != null) detail.icon.gameObject.SetActive(false);
        if (detail.selectedItemUI != null) detail.selectedItemUI.SetActive(false);
        if (page.bazaarEffectDetail != null) page.bazaarEffectDetail.gameObject.SetActive(false);
    }

    private static string AppearanceFlavor(string caption)
    {
        int start = 0;
        for (int row = 0; row < 3; row++)
        {
            int newline = caption.IndexOf('\n', start);
            if (newline < 0) return caption;
            start = newline + 1;
        }
        return caption[start..].TrimStart('\r', '\n');
    }

    private static void RestoreDetailChildren()
    {
        var detail = page?.bazaarCustomDetail;
        if (detail?.icon != null) detail.icon.gameObject.SetActive(detailIconActive);
        if (detail?.selectedItemUI != null) detail.selectedItemUI.SetActive(selectedItemUiActive);
    }

    internal static bool TryGetAppearanceFocusId(UIBazaarCustomPage owner, BazaarCustomPageCategory category, out int focusId)
    {
        focusId = -1;
        if (!Active || owner?.cacheCustomPartsListDic == null) return false;
        var partCategory = BazaarCustomItemData.ToPartsCategory(category);
        int slot = BazaarCustomItemData.ToPartsCategoryIndex(category);
        EnsureChoiceList(category);
        if (!owner.cacheCustomPartsListDic.TryGetValue(category, out var items) || items == null)
            return false;
        if (Prototype.UsesActualAppearance(partCategory, slot) && actualChoiceData.TryGetValue(category, out var actual) &&
            items.Count > 0 && SameData(items[0], actual)) { focusId = 0; return true; }
        if (Prototype.UsesHiddenAppearance(partCategory, slot))
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i]?.IsUiRemove == true) { focusId = i; return true; }
        }
        uint appearanceId = Prototype.AppearanceId(partCategory, slot);
        if (appearanceId == 0) return false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item?.PartsData == null || IsActualChoice(item) || item.PartsData.Id != appearanceId) continue;
            focusId = i;
            return true;
        }
        Plugin.Warn("NativeAppearanceFocusMissing", new { category = category.ToString(), slot, appearanceId });
        return false;
    }

    internal static bool TryGetAppearanceCheckmark(BazaarCustomItemData item, out bool selected)
    {
        selected = false;
        if (!Active || item == null) return false;
        int slot = BazaarCustomItemData.ToPartsCategoryIndex(item.Category);
        var category = BazaarCustomItemData.ToPartsCategory(item.Category);
        if (IsActualChoice(item)) selected = Prototype.UsesActualAppearance(category, slot);
        else if (item.IsUiRemove) selected = Prototype.UsesHiddenAppearance(category, slot);
        else selected = !Prototype.UsesActualAppearance(category, slot) &&
            !Prototype.UsesHiddenAppearance(category, slot) && item.PartsData != null &&
            Prototype.AppearanceId(category, slot) == item.PartsData.Id;
        return true;
    }

    private static bool SameData(BazaarCustomItemData left, BazaarCustomItemData right) =>
        left != null && right != null && IL2CPP.Il2CppObjectBaseToPtr(left) == IL2CPP.Il2CppObjectBaseToPtr(right);

    private static bool IsActualChoice(BazaarCustomItemData data) =>
        data != null && actualChoiceData.TryGetValue(data.Category, out var choice) && SameData(data, choice);

    internal static bool IsModeChoiceForSound(BazaarCustomItemData data) =>
        IsActualChoice(data) || data?.IsUiRemove == true &&
        Prototype.AllowsHidden(BazaarCustomItemData.ToPartsCategory(data.Category));

    internal static void EnsureChoiceLists()
    {
        foreach (var category in Enum.GetValues<BazaarCustomPageCategory>()) EnsureChoiceList(category);
    }

    internal static void EnsureChoiceList(BazaarCustomPageCategory category)
    {
        if (!Active || page?.cacheCustomPartsListDic == null ||
            !page.cacheCustomPartsListDic.TryGetValue(category, out var items) || items == null) return;
        var partCategory = BazaarCustomItemData.ToPartsCategory(category);
        int slot = BazaarCustomItemData.ToPartsCategoryIndex(category);
        if (slot < 0 || partCategory != BazaarCustomItemData.PartsCategory.Tent &&
            partCategory != BazaarCustomItemData.PartsCategory.OrnamentS &&
            partCategory != BazaarCustomItemData.PartsCategory.OrnamentL &&
            partCategory != BazaarCustomItemData.PartsCategory.Shelf) return;
        uint actualId = Prototype.ActualAppearanceId(partCategory, slot);
        if (actualId == 0) return;
        BazaarCustomItemData source = null;
        for (int i = 0; i < items.Count; i++)
            if (items[i]?.PartsData != null && items[i].PartsData.Id == actualId && !IsActualChoice(items[i]))
            { source = items[i]; break; }
        if (source == null) return;
        if (actualChoiceData.TryGetValue(category, out var old))
        {
            if (old.PartsData?.Id == actualId && items.Count > 0 && SameData(items[0], old))
            {
                EnsureHiddenChoice(category, items, partCategory);
                return;
            }
            for (int i = items.Count - 1; i >= 0; i--)
                if (SameData(items[i], old)) items.RemoveAt(i);
        }
        var choice = new BazaarCustomItemData(category, source.PartsData, 1, null);
        actualChoiceData[category] = choice;
        items.Insert(0, choice);
        EnsureHiddenChoice(category, items, partCategory);
        // High-frequency choice-list diagnostics disabled after implementation validation.
    }

    private static void EnsureHiddenChoice(BazaarCustomPageCategory category,
        Il2CppSystem.Collections.Generic.List<BazaarCustomItemData> items,
        BazaarCustomItemData.PartsCategory partCategory)
    {
        if (!Prototype.AllowsHidden(partCategory)) return;
        for (int i = 1; i < items.Count; i++)
            if (items[i]?.IsUiRemove == true)
            {
                if (i != 1)
                {
                    var empty = items[i];
                    items.RemoveAt(i);
                    items.Insert(1, empty);
                }
                return;
            }
        var hidden = new BazaarCustomItemData(category, null, 0, null);
        addedHiddenChoiceData[category] = hidden;
        items.Insert(1, hidden);
        // High-frequency choice-list diagnostics disabled after implementation validation.
    }

    internal static void DecorateDisplayList(Il2CppSystem.Collections.Generic.List<BazaarCustomItemData> items)
    {
        if (!Active || items == null || page == null) return;
        var category = page.selectTabCategory;
        EnsureChoiceList(category);
        if (!actualChoiceData.TryGetValue(category, out var actual)) return;
        for (int i = items.Count - 1; i >= 0; i--)
            if (SameData(items[i], actual)) items.RemoveAt(i);
        items.Insert(0, actual);
        if (Prototype.AllowsHidden(BazaarCustomItemData.ToPartsCategory(category)))
        {
            BazaarCustomItemData hidden = null;
            for (int i = items.Count - 1; i >= 1; i--)
                if (items[i]?.IsUiRemove == true)
                {
                    hidden = items[i];
                    items.RemoveAt(i);
                    break;
                }
            if (hidden == null) addedHiddenChoiceData.TryGetValue(category, out hidden);
            if (hidden != null) items.Insert(1, hidden);
        }
        // High-frequency display-list diagnostics disabled after implementation validation.
    }

    private static void RestoreChoiceLists()
    {
        if (page?.cacheCustomPartsListDic != null)
        {
            foreach (var pair in addedHiddenChoiceData)
                if (page.cacheCustomPartsListDic.TryGetValue(pair.Key, out var items) && items != null)
                    for (int i = items.Count - 1; i >= 0; i--)
                        if (SameData(items[i], pair.Value)) items.RemoveAt(i);
            foreach (var pair in actualChoiceData)
                if (page.cacheCustomPartsListDic.TryGetValue(pair.Key, out var items) && items != null)
                    for (int i = items.Count - 1; i >= 0; i--)
                        if (SameData(items[i], pair.Value)) items.RemoveAt(i);
        }
        actualChoiceData.Clear();
        addedHiddenChoiceData.Clear();
    }

    private static void RefreshListDisplay()
    {
        if (page?.partsScrollGroup == null) return;
        var data = page.BazaarCustomScrollGroupData();
        if (data != null) page.partsScrollGroup.SetData(data);
        page.partsScrollGroup.UpdateChildren();
    }

    internal static void UpdateCheckmark(UIBazaarCustomPartsIconContent icon)
    {
        if (icon?.PutIcon == null || icon.cacheData == null) return;
        bool selected = icon.cacheData.IsSelectPut;
        if (TryGetAppearanceCheckmark(icon.cacheData, out bool appearanceSelected))
            selected = appearanceSelected;
        icon.PutIcon.SetActive(selected);
        if (Active) icon.itemIcon?.SetStackActive(false);
        bool followActual = IsActualChoice(icon.cacheData);
        // UIIcon.bg is the cream card itself. Keep it opaque, and clear the stock
        // dark tint on the synthetic follow choice.
        var iconBackground = icon.icon?.bg;
        if (iconBackground != null)
        {
            iconBackground.gameObject.SetActive(true);
            if (followActual) iconBackground.color = Color.white;
        }
        var itemImage = icon.icon?.icon;
        if (itemImage != null)
        {
            // Keep the source art visible. A separate white silhouette made from its
            // alpha lightens only the item's pixels, without tinting the cream card.
            itemImage.color = Color.white;
            if (followActual)
            {
                itemImage.material = null;
                itemImage.canvasRenderer.SetColor(Color.white);
                UpdateActualIconVeil(itemImage, icon.PutIcon.GetComponent<Image>());
            }
            var veil = itemImage.transform.Find("BazaarDecorTransmog.ActualIconVeil");
            if (!followActual)
            {
                if (veil != null) veil.gameObject.SetActive(false);
            }
        }
        UpdateActualAppearanceBadge(icon);
    }

    private static void UpdateActualIconVeil(Image source, Image template)
    {
        if (template == null) return;
        var sprite = source.overrideSprite ?? source.sprite;
        if (sprite == null) return;
        var whiteSprite = WhiteIconSprite(sprite);
        if (whiteSprite == null) return;
        var existing = source.transform.Find("BazaarDecorTransmog.ActualIconVeil");
        Image veil;
        if (existing == null)
        {
            // The item's Image belongs to the game's icon state hierarchy. Cloning it
            // carried that hierarchy's visual state into the overlay. The stock
            // checkmark Image is a plain, known-working UI drawing template.
            var veilObject = UnityEngine.Object.Instantiate(template.gameObject, source.transform);
            veilObject.name = "BazaarDecorTransmog.ActualIconVeil";
            veil = veilObject.GetComponent<Image>();
            if (veil == null) { UnityEngine.Object.Destroy(veilObject); return; }
            veil.raycastTarget = false;
        }
        else veil = existing.GetComponent<Image>();
        if (veil == null) return;
        veil.sprite = whiteSprite;
        veil.overrideSprite = whiteSprite;
        veil.type = Image.Type.Simple;
        veil.preserveAspect = source.preserveAspect;
        veil.material = null;
        // Match the cream card rather than pure white, so the art reads as if it
        // fades into the card. WhiteIconSprite expands only the silhouette.
        veil.color = new Color(1f, 0.975f, 0.89f, 0.5f);
        var rect = veil.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        rect.pivot = source.rectTransform.pivot;
        rect.localScale = Vector3.one;
        veil.canvasRenderer.SetColor(Color.white);
        veil.gameObject.SetActive(true);
        veil.transform.SetAsLastSibling();
    }

    private static Sprite WhiteIconSprite(Sprite source)
    {
        int key = source.GetInstanceID();
        if (whiteIconSprites.TryGetValue(key, out var cached) && cached != null) return cached;
        Texture2D readback = null;
        RenderTexture target = null;
        var previous = RenderTexture.active;
        try
        {
            var texture = source.texture;
            var region = source.textureRect;
            var offset = source.textureRectOffset;
            int width = Mathf.RoundToInt(source.rect.width);
            int height = Mathf.RoundToInt(source.rect.height);
            int copyX = Mathf.RoundToInt(offset.x);
            int copyY = Mathf.RoundToInt(offset.y);
            if (texture == null || width <= 0 || height <= 0 ||
                copyX < 0 || copyY < 0 || copyX + region.width > width + 0.5f ||
                copyY + region.height > height + 0.5f) return null;
            // Game icon atlases may be non-readable. Read from the GPU once per sprite,
            // then retain only a small white texture carrying the original alpha.
            target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
            // textureRect may be trimmed inside an atlas. Preserve the original
            // sprite's transparent margins so the veil has the same visual size.
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) readback.SetPixel(x, y, Color.clear);
            readback.Apply(false, false);
            readback.ReadPixels(region, copyX, copyY, false);
            readback.Apply(false, false);
            var pixels = readback.GetPixels();
            var veilPixels = new Color[pixels.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // Dilate alpha only: this makes a narrow cream outline that
                    // follows the art rather than scaling the full rectangular UI Image.
                    float alpha = 0f;
                    int minY = Mathf.Max(0, y - ActualVeilOutlinePixels);
                    int maxY = Mathf.Min(height - 1, y + ActualVeilOutlinePixels);
                    int minX = Mathf.Max(0, x - ActualVeilOutlinePixels);
                    int maxX = Mathf.Min(width - 1, x + ActualVeilOutlinePixels);
                    for (int sampleY = minY; sampleY <= maxY; sampleY++)
                        for (int sampleX = minX; sampleX <= maxX; sampleX++)
                            alpha = Mathf.Max(alpha, pixels[sampleY * width + sampleX].a);
                    veilPixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
                }
            readback.SetPixels(veilPixels);
            readback.Apply(false, true);
            readback.name = "BazaarDecorTransmog.WhiteIcon." + key;
            var pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
            var result = Sprite.Create(readback, new Rect(0, 0, width, height), pivot, source.pixelsPerUnit);
            result.name = readback.name;
            whiteIconSprites[key] = result;
            readback = null;
            return result;
        }
        catch (Exception error)
        {
            Plugin.Warn("WhiteIconFailed", new { sprite = source.name, error = error.Message });
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (readback != null) UnityEngine.Object.Destroy(readback);
        }
    }

    // Only the synthetic first card carries this badge. The ordinary owned-item card
    // remains a fixed replacement even when it depicts the currently equipped item.
    private static void UpdateActualAppearanceBadge(UIBazaarCustomPartsIconContent icon)
    {
        bool show = IsActualAppearanceBadgeTarget(icon);

        var existing = icon.transform.Find("BazaarDecorTransmog.ActualAppearanceBadge");
        if (!show)
        {
            if (existing != null) existing.gameObject.SetActive(false);
            return;
        }
        var desiredSprite = ActualAppearanceBadgeSprite();
        if (desiredSprite == null)
        {
            if (existing != null) existing.gameObject.SetActive(false);
            return;
        }

        Image badge;
        if (existing == null)
        {
            var template = icon.PutIcon?.GetComponent<Image>();
            if (template == null)
            {
                Plugin.Warn("NativeActualBadgeMissingTemplate", new { page = page?.name, reason = "no stock icon image" });
                return;
            }
            var badgeObject = UnityEngine.Object.Instantiate(template.gameObject, icon.transform);
            badgeObject.name = "BazaarDecorTransmog.ActualAppearanceBadge";
            badge = badgeObject.GetComponent<Image>();
            if (badge == null) return;
            badge.sprite = desiredSprite;
            badge.type = Image.Type.Simple;
            badge.preserveAspect = true;
            badge.color = Color.white;
            badge.raycastTarget = false;
            badge.transform.SetAsLastSibling();
        }
        else badge = existing.GetComponent<Image>();

        if (badge == null) return;
        // Existing cards survive a DLL replacement while the editor remains open.
        // Reapply this layout every update so a previous full-card test badge cannot
        // cover the actual icon or its cream veil.
        var rect = badge.rectTransform;
        rect.anchorMin = new Vector2(0.09f, 0.10f);
        rect.anchorMax = new Vector2(0.44f, 0.45f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        badge.sprite = desiredSprite;
        badge.type = Image.Type.Simple;
        badge.preserveAspect = true;
        badge.raycastTarget = false;
        badge.color = Color.white;
        badge.gameObject.SetActive(true);
    }

    private static Sprite ActualAppearanceBadgeSprite()
    {
        if (actualAppearanceBadgeSprite != null) return actualAppearanceBadgeSprite;
        const string resource = "BazaarDecorTransmog.Assets.actual_appearance_icon.rgba";
        using var stream = typeof(NativeDecorUi).Assembly.GetManifestResourceStream(resource);
        if (stream == null)
        {
            Plugin.Warn("NativeActualBadgeImageMissing", new { resource });
            return null;
        }
        const int size = 128;
        var pixels = new byte[size * size * 4];
        if (stream.Read(pixels, 0, pixels.Length) != pixels.Length)
        {
            Plugin.Warn("NativeActualBadgeImageInvalid", new { resource });
            return null;
        }
        actualAppearanceBadgeTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int offset = ((size - 1 - y) * size + x) * 4;
                actualAppearanceBadgeTexture.SetPixel(x, y, new Color32(
                    pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]));
            }
        actualAppearanceBadgeTexture.Apply(false, true);
        actualAppearanceBadgeTexture.name = "BazaarDecorTransmog.ActualAppearanceIconTexture";
        actualAppearanceBadgeSprite = Sprite.Create(actualAppearanceBadgeTexture,
            new Rect(0, 0, actualAppearanceBadgeTexture.width, actualAppearanceBadgeTexture.height),
            new Vector2(0.5f, 0.5f), 100f);
        actualAppearanceBadgeSprite.name = "BazaarDecorTransmog.ActualAppearanceIcon";
        return actualAppearanceBadgeSprite;
    }

    private static bool IsActualAppearanceBadgeTarget(UIBazaarCustomPartsIconContent icon)
    {
        return Active && IsActualChoice(icon?.cacheData);
    }

    private static void RefreshCheckmarks()
    {
        if (page == null) return;
        foreach (var icon in page.GetComponentsInChildren<UIBazaarCustomPartsIconContent>(true))
            UpdateCheckmark(icon);
    }

    private static void RestoreActualFocus()
    {
        if (page == null) return;
        int focusId = page.GetSetFocusId(page.selectTabCategory);
        if (focusId < 0) return;
        page.partsScrollGroup?.SetFocus(focusId);
    }

    private static bool FocusAppliedAppearance(BazaarCustomPageCategory category)
    {
        if (!TryGetAppearanceFocusId(page, category, out int focusId)) return false;
        page.partsScrollGroup?.SetFocus(focusId);
        if (page.cacheCustomPartsListDic.TryGetValue(category, out var items) &&
            items != null && focusId >= 0 && focusId < items.Count)
        {
            lastFocusData = items[focusId];
            lastFocusCategory = category;
            if (exitPosePreserving && !exitPoseHovered) RestoreExitPose();
            else Select(lastFocusData, category, false);
        }
        return true;
    }

    private static void ResetVanillaPreviewToActual(BazaarCustomPageCategory category, int focusId)
    {
        if (page?.cacheCustomPartsListDic == null || focusId < 0 ||
            !page.cacheCustomPartsListDic.TryGetValue(category, out var items) ||
            items == null || focusId >= items.Count || items[focusId] == null) return;
        // Let the stock focus route reload the genuinely equipped model once. All later
        // appearance-mode focus changes stay intercepted, so list navigation remains a
        // transmog-only preview with no real/effect-model flashes.
        allowStockFocus = true;
        try
        {
            page.OnFocusIn(items[focusId], category);
        }
        finally { allowStockFocus = false; }
    }

    internal static bool AllowStockFocus => allowStockFocus;

    internal static bool TryPreserveExitPoseOnFocus(BazaarCustomItemData data,
        BazaarCustomPageCategory category)
    {
        if (!exitPosePreserving || exitPoseHovered) return false;
        RememberFocus(data, category);
        RestoreExitPose();
        return true;
    }

    internal static void RememberFocus(BazaarCustomItemData data, BazaarCustomPageCategory category)
    {
        lastFocusData = data;
        lastFocusCategory = category;
    }

    internal static void Draw(float x, float y)
    {
        if (!Ready || page == null) return;
        ApplyAppearanceGuide();
        if (Active)
        {
            ApplyModeTitle();
            // Keep only the focused visual's name and caption visible.
            if (page.bazaarCustomDetail != null) page.bazaarCustomDetail.gameObject.SetActive(true);
            if (page.bazaarEffectDetail != null) page.bazaarEffectDetail.gameObject.SetActive(false);
        }
        if (!Active) return;
        if (titleApplied) return;
        GUI.Box(new Rect(x, y - 70, 600, 66), "Official decor list - follows Transmog ON/OFF");
        GUI.Label(new Rect(x + 12, y - 48, 576, 42), note);
    }

    private static void ApplyModeTitle()
    {
        if (!Active || page == null) return;
        if (modeTitle == null)
        {
            var root = page.transform.root;
            foreach (var header in root.GetComponentsInChildren<UIPageHeader>(true))
            {
                var text = header?.headerText;
                string replacement = ReplacementTitle(text?.text);
                if (text == null || replacement == null) continue;
                modeTitle = text;
                originalTitle = text.text;
                if (originalTitle == "オブジェの変更") Localization.SetLanguage(Language.ja);
                else if (originalTitle == "Redecorate") Localization.SetLanguage(Language.en);
                originalTitleColor = text.color;
                modeBackground = header.bg;
                if (modeBackground != null) originalBackgroundColor = modeBackground.color;
                modeIcon = header.icon;
                if (modeIcon != null) originalIconColor = modeIcon.color;
                break;
            }
            if (modeTitle == null && !titleSearchReported)
            {
                titleSearchReported = true;
                Plugin.Warn("NativeModeTitleNotFound", new { expected = new[] { "オブジェの変更", "Redecorate" } });
            }
        }
        if (modeTitle == null) return;
        modeTitle.text = ReplacementTitle(originalTitle);
        modeTitle.color = new Color(0.38f, 0.20f, 0.55f, 1f);
        if (modeBackground != null)
            modeBackground.color = new Color(0.95f, 0.72f, 1f, originalBackgroundColor.a);
        if (modeIcon != null)
            modeIcon.color = new Color(1f, 0.55f, 1f, originalIconColor.a);
        titleApplied = true;
    }

    // Saving/loading remains within appearance mode. Reuse its stock header and
    // only change the label for the active preset branch.
    internal static void RefreshModeTitleForPresetState() => ApplyModeTitle();

    private static void RestoreModeTitle()
    {
        if (modeTitle != null && titleApplied)
        {
            modeTitle.text = originalTitle;
            modeTitle.color = originalTitleColor;
        }
        if (modeBackground != null && titleApplied) modeBackground.color = originalBackgroundColor;
        if (modeIcon != null && titleApplied) modeIcon.color = originalIconColor;
        titleApplied = false;
        modeTitle = null;
        modeBackground = null;
        modeIcon = null;
        originalTitle = null;
        titleSearchReported = false;
    }

    private static string ReplacementTitle(string current)
    {
        switch (PresetUiProbe.CurrentHeaderMode)
        {
            case PresetUiProbe.HeaderMode.Save:
                return Localization.Get("presets.menu.save");
            case PresetUiProbe.HeaderMode.Load:
                return Localization.Get("presets.menu.load");
        }
        return current switch
        {
            "オブジェの変更" => Localization.Get("appearance.title"),
            "Redecorate" => Localization.Get("appearance.title"),
            _ => null
        };
    }

    private static void ApplyAppearanceGuide()
    {
        if (!CanToggle) return;
        if (menuFooter == null)
            menuFooter = page.transform.root.GetComponentInChildren<UIMenuFooter>(true);
        if (appearanceGuide == null)
        {
            foreach (var guide in page.transform.root.GetComponentsInChildren<UIButtonGuide>(true))
            {
                if (guide == null || !guide.gameObject.activeInHierarchy || guide.guideText == null) continue;
                string text = guide?.guideText?.text;
                if (text != "みためを変更" && text != "Edit Appearance" &&
                    text != "効果を変更" && text != "Edit Effects") continue;
                appearanceGuide = guide;
                break;
            }
            if (appearanceGuide != null)
            {
                if (appearanceGuide.collider != null) appearanceGuide.collider.enabled = false;
            }
            else if (!footerRefreshRequested && menuFooter != null)
            {
                RefreshOfficialFooter();
            }
            else if (menuFooter == null && !guideSearchReported)
            {
                guideSearchReported = true;
                Plugin.Warn("NativeMenuFooterNotFound", new { page = page.name });
            }
        }
    }

    private static void RefreshOfficialFooter()
    {
        if (page == null) return;
        if (menuFooter == null)
            menuFooter = page.transform.root.GetComponentInChildren<UIMenuFooter>(true);
        if (menuFooter == null || menuFooter.LastGuideId < 0) return;
        footerRefreshRequested = true;
        appearanceGuide = null;
        menuFooter.SetGuide((KeyButtonGuideMasterId)(uint)menuFooter.LastGuideId);
    }

    private static void RemoveAppearanceGuide()
    {
        if (appearanceGuide != null)
        {
            appearanceGuide = null;
        }
        menuFooter = null;
        footerRefreshRequested = false;
        guideSearchReported = false;
        normalEditorDialogOpen = false;
        appearanceGuideResumeFrame = -1;
    }

    internal static bool ShouldInjectGuide => Prototype.Enabled && page != null &&
        !normalEditorDialogOpen && !suppressAppearanceGuideUntilPageExit &&
        (appearanceGuideResumeFrame < 0 || Time.frameCount >= appearanceGuideResumeFrame);
    internal static void RefreshFooterForPresetState() => RefreshOfficialFooter();
    internal static uint CurrentGuideTextId => AppearanceGuideTextId;
    internal static uint CurrentPresetsGuideTextId => PresetsGuideTextId;
    internal static bool TryGetGuideText(uint textId, out string text)
    {
        if (PresetUiProbe.TryGetNameText(textId, out text)) return true;
        if (PresetUiProbe.TryGetDeleteText(textId, out text)) return true;
        if (textId == AppearanceGuideTextId)
        {
            text = Localization.Get("appearance.title");
            return true;
        }
        if (textId == PresetsGuideTextId)
        {
            text = Localization.Get("presets.guide");
            return true;
        }
        if (textId == PresetDeleteGuideTextId)
        {
            text = Localization.Get("presets.delete.guide");
            return true;
        }
        if (textId == PresetSaveTextId)
        {
            text = Localization.Get("presets.menu.save");
            return true;
        }
        if (textId == PresetLoadTextId)
        {
            text = Localization.Get("presets.menu.load");
            return true;
        }
        if (textId >= PresetEmptySlotTextId && textId < PresetEmptySlotTextId + PresetStorage.UiSlotCount)
        {
            text = Prototype.UiSlotLabel((int)(textId - PresetEmptySlotTextId));
            return true;
        }
        if (textId == PresetSaveCompletedTextId)
        {
            text = Localization.Get("presets.save.completed");
            return true;
        }
        if (textId == PresetLoadCompletedTextId)
        {
            text = Localization.Get("presets.load.completed");
            return true;
        }
        if (textId == AppearanceExitConfirmTextId)
        {
            text = Localization.Get("appearance.exit.confirm");
            return true;
        }
        text = null;
        return false;
    }

}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.UpdateCachePartsList),
    new[] { typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ItemData>), typeof(BazaarCustomPageCategory) })]
internal static class NativeAppearanceChoiceListUpdate
{
    static void Postfix(BazaarCustomPageCategory category)
    {
        if (NativeDecorUi.Active)
            Plugin.Guard("native-choice-list", () => NativeDecorUi.EnsureChoiceList(category));
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.BazaarCustomDataList))]
internal static class NativeAppearanceDisplayList
{
    static void Postfix(ref Il2CppSystem.Collections.Generic.List<BazaarCustomItemData> __result)
    {
        if (NativeDecorUi.Active)
        {
            var list = __result;
            Plugin.Guard("native-display-list", () => NativeDecorUi.DecorateDisplayList(list));
        }
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.UpdateCachePartsList), new Type[0])]
internal static class NativeAppearanceAllChoiceListsUpdate
{
    static void Postfix()
    {
        if (NativeDecorUi.Active)
            Plugin.Guard("native-choice-lists", NativeDecorUi.EnsureChoiceLists);
    }
}

[HarmonyPatch(typeof(LanguageManager), nameof(LanguageManager.GetLocalizeText),
    new[] { typeof(LocalizeTextTableType), typeof(uint), typeof(bool) })]
internal static class NativeAppearanceGuideLocalization
{
    static bool Prefix(LanguageManager __instance, LocalizeTextTableType tableType, uint textId, ref string __result)
    {
        Localization.SetLanguage(__instance.CurrentLanguage);
        if (!NativeDecorUi.TryGetGuideText(textId, out var text)) return true;
        __result = text;
        return false;
    }
}

[HarmonyPatch(typeof(UIMenuFooter), nameof(UIMenuFooter.SortGuide))]
internal static class NativeAppearanceFooterGuideData
{
    static void Postfix(ref Il2CppSystem.Collections.Generic.List<UIButtonGuideData> __result)
    {
        if (__result == null) return;

        // The Start guide is ours (+ / Tab: Presets).  While a preset dialog is
        // in front, remove it from the official guide data before the footer
        // constructs its buttons.  Do not toggle a rendered GameObject here:
        // UIMenuFooter recreates those objects during updates.
        if (PresetUiProbe.IsPresetUiOpen)
        {
            for (var i = __result.Count - 1; i >= 0; i--)
            {
                var guide = __result[i];
                if (guide != null && guide.button == GuideKey.Start &&
                    guide.textId == NativeDecorUi.CurrentPresetsGuideTextId)
                    __result.RemoveAt(i);
            }
        }

        if (PresetUiProbe.IsNameInputOpen)
        {
            // In the Menu action set, East is the stock A / Confirm guide.
            // Keep it intact and add South as B / Cancel.  GuideKey names are
            // logical positions, not Nintendo face-button labels.
            var hasCancel = false;
            for (var i = 0; i < __result.Count; i++)
            {
                var guide = __result[i];
                if (guide == null) continue;
                if (guide.button == GuideKey.South)
                {
                    hasCancel = true;
                }
            }

            if (!hasCancel)
            {
                __result.Insert(0, new UIButtonGuideData(
                    GuideKey.South,
                    NativeDecorUi.StockCancelFooterTextId,
                    LocalizeTextTableType.KeyButtonGuideText,
                    InputKeyBind.InputActionSet.Menu));
            }
            return;
        }
        if (PresetUiProbe.IsSlotMenuOpen)
        {
            for (var i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null && __result[i].button == GuideKey.North)
                    __result.RemoveAt(i);
            __result.Insert(0, new UIButtonGuideData(
                GuideKey.North,
                NativeDecorUi.PresetDeleteGuideTextId,
                LocalizeTextTableType.KeyButtonGuideText,
                InputKeyBind.InputActionSet.Menu));
            return;
        }
        if (!NativeDecorUi.ShouldInjectGuide || __result == null) return;
        // Appearance mode is a child view: B / Esc is the only exit action.
        // Hide both the stock detail guide and the X / F guide while it is active.
        if (NativeDecorUi.Active)
        {
            for (int i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null &&
                    (__result[i].button == GuideKey.North || __result[i].button == GuideKey.West))
                    __result.RemoveAt(i);
            if (!PresetUiProbe.IsPresetUiOpen)
            {
                __result.Insert(0, new UIButtonGuideData(
                    GuideKey.Start,
                    NativeDecorUi.CurrentPresetsGuideTextId,
                    LocalizeTextTableType.KeyButtonGuideText,
                    InputKeyBind.InputActionSet.Menu));
            }
            return;
        }
        // Rejoin the stock editor footer in the exact rebuild that restores its
        // Y / North guide. This avoids a frame-count race after closing a modal.
        bool hasStockDetailGuide = false;
        for (int i = 0; i < __result.Count; i++)
        {
            if (__result[i] != null && __result[i].button == GuideKey.North)
            {
                hasStockDetailGuide = true;
                break;
            }
        }
        if (!hasStockDetailGuide) return;
        var data = new UIButtonGuideData(
            GuideKey.West,
            NativeDecorUi.CurrentGuideTextId,
            LocalizeTextTableType.KeyButtonGuideText,
            InputKeyBind.InputActionSet.Menu);
        __result.Insert(0, data);
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnShowDetail))]
internal static class NativeAppearanceDetailGuard
{
    static bool Prefix()
    {
        if (!NativeDecorUi.Active) return true;
        return false;
    }
}

// The stock input route plays its UI sound before calling OnShowDetail.  Blocking
// the detail callback alone therefore leaves a stray sound behind in appearance
// mode.  Consume the North / Y action at the controllable-UI boundary instead.
[HarmonyPatch(typeof(ControllableUI), nameof(ControllableUI.OnNorth))]
internal static class NativeAppearanceDetailInputGuard
{
    static bool Prefix()
    {
        // UISelectDialog receives Y through ControllableUI.OnNorth.  Handle
        // our slot-list command here before the appearance page's generic
        // detail guard consumes it.
        if (PresetUiProbe.TryOpenDeleteForFocusedSlot()) return false;
        if (!NativeDecorUi.Active) return true;
        return false;
    }
}

// The stock object-edit exit dialog uses choice indexes 0/1 to leave the page
// and index 2 to return to editing. Observe the decision before the stock
// callback runs so our extra footer guide follows the same transition.
[HarmonyPatch(typeof(UIDialog), nameof(UIDialog.OnDecide))]
internal static class NativeStockObjectExitChoiceGuideState
{
    static void Prefix(UIDialog __instance, int id)
    {
        if (NativeDecorUi.Active || __instance == null) return;
        var dialogs = Resources.FindObjectsOfTypeAll<UIDefaultDialog>();
        foreach (var dialog in dialogs)
        {
            if (dialog == null || !dialog.gameObject.activeInHierarchy || dialog.infoId != 116000) continue;
            NativeDecorUi.ObserveStockObjectExitChoice(id);
            return;
        }
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.ShowPage))]
internal static class NativePageShown
{
    static void Prefix(UIBazaarCustomPage __instance) =>
        Plugin.Guard("native-ui-page-prepare", () => NativeDecorUi.Observe(__instance));

    static void Postfix() => Plugin.Guard("native-ui-page-ready", NativeDecorUi.Sync);
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnFocusIn))]
internal static class NativeFocus
{
    static bool Prefix(BazaarCustomItemData data, BazaarCustomPageCategory category)
    {
        if (NativeDecorUi.TryPreserveExitPoseOnFocus(data, category)) return false;
        if (!NativeDecorUi.Active || NativeDecorUi.AllowStockFocus)
        {
            NativeDecorUi.RememberFocus(data, category);
            return true;
        }
        Plugin.Guard("native-ui-focus", () => NativeDecorUi.Select(data, category, false));
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.GetSetFocusId))]
internal static class NativeAppearanceInitialFocus
{
    static void Postfix(UIBazaarCustomPage __instance, BazaarCustomPageCategory category, ref int __result)
    {
        if (NativeDecorUi.TryGetAppearanceFocusId(__instance, category, out int focusId))
            __result = focusId;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.FocusCategoryBeforeModel))]
internal static class NativeAppearanceTabPreviewGuard
{
    static bool Prefix(BazaarCustomPageCategory category)
    {
        if (!NativeDecorUi.Active) return true;
        // The stock tab transition briefly reloads the outgoing slot's real/effect model
        // before the list sends its new focus. Our focus handler supplies the appearance
        // preview immediately afterward, so the intermediate real-model reload is unwanted.
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPartsIconContent), nameof(UIBazaarCustomPartsIconContent.UpdateContentUniqueData))]
internal static class NativeAppearanceCheckmark
{
    static void Postfix(UIBazaarCustomPartsIconContent __instance)
    {
        if (NativeDecorUi.Active)
            Plugin.Guard("appearance-checkmark", () => NativeDecorUi.UpdateCheckmark(__instance));
    }

}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnDeside))]
internal static class NativeDecide
{
    static bool Prefix(UIBazaarCustomPage __instance, BazaarCustomItemData data)
    {
        if (!NativeDecorUi.Active) return true;
        Plugin.Guard("native-ui-decide", () => NativeDecorUi.Select(data, __instance.selectTabCategory, true));
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnCanacel))]
internal static class NativeCancel
{
    static bool Prefix()
    {
        if (NativeDecorUi.IsModeTransitioning) return false;
        if (!NativeDecorUi.Active) return true;
        // A modal dialog or keyboard owns B / Esc until it closes. Let the game's
        // own dialog manager handle it instead of leaving the appearance editor.
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager != null && manager.TryGetDialogMask(out _)) return true;
        Plugin.Guard("native-ui-cancel", NativeDecorUi.LeaveAppearanceFromCancel);
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnClose))]
internal static class NativePageClosed
{
    static void Prefix() => Plugin.Guard("native-ui-close", NativeDecorUi.Exit);
}

[HarmonyPatch(typeof(BazaarManager), nameof(BazaarManager.SetCustomParts))]
internal static class NativePlacementGuard
{
    static bool Prefix(uint itemId, BazaarCustomItemData.PartsCategory category, int index, ref bool __state)
    {
        __state = !NativeDecorUi.Active;
        if (__state) return true;
        return false;
    }

    static void Postfix(BazaarCustomItemData.PartsCategory category, int index, bool __state)
    {
        if (!__state) return;
        Plugin.Guard("actual-placement-changed", () => Prototype.ActualPlacementChanged(category, index));
    }
}

[HarmonyPatch(typeof(ControllableUI), nameof(ControllableUI.OnWest))]
internal static class NativeAppearanceInput
{
    static bool Prefix(ControllableUI __instance)
    {
        if (!NativeDecorUi.CanToggle || __instance == null || NativeDecorUi.PageTransform == null ||
            !__instance.transform.IsChildOf(NativeDecorUi.PageTransform)) return true;
        if (NativeDecorUi.IsModeTransitioning) return false;
        // West/X/F is the entry command only. Leaving the appearance mode is always
        // handled by the normal cancel command, so it reads as a child editor.
        Plugin.Guard("native-ui-mode-enter", NativeDecorUi.EnterAppearanceFromWest);
        return false;
    }
}

[HarmonyPatch(typeof(UIMenuManager), nameof(UIMenuManager.OnStart))]
internal static class NativePresetStartProbe
{
    static bool Prefix()
    {
        if (!NativeDecorUi.Active) return true;
        Plugin.Guard("preset-dialog-probe", PresetUiProbe.Open);
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage._BazaarCustomScrollGroupData_b__26_0))]
internal static class NativeAppearanceChoiceSound
{
    private static Il2CppSystem.Func<UISoundTypes> cancelSound;

    static void Postfix(BazaarCustomItemData data, ref UISound __result)
    {
        if (!NativeDecorUi.Active || data == null ||
            !NativeDecorUi.IsModeChoiceForSound(data) || __result == null) return;
        cancelSound ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Func<UISoundTypes>>(
            (Func<UISoundTypes>)(() => UISoundTypes.Cancel));
        __result.ResetFuncEast(cancelSound);
    }
}
