using static BazaarDecorTransmog.UiTextIds;
using BokuMono;
using BokuMono.Data;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarDecorTransmog;

// Observe actual input, not focus callbacks: closing a modal also sends focus
// callbacks and must not be mistaken for a new player command.
[HarmonyPatch]
internal static class NativeExitFocusNewInput
{
    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        // Do not hook InputSlotSelect: its native trampoline caused an access
        // violation on this game build. Decisions are observed on the page below.
        foreach (var name in new[] { nameof(ControllableUI.OnEast), nameof(ControllableUI.OnSouth),
            nameof(ControllableUI.OnL), nameof(ControllableUI.OnR) })
            yield return AccessTools.DeclaredMethod(typeof(ControllableUI), name);
        yield return AccessTools.DeclaredMethod(typeof(NestableUI), nameof(NestableUI.InputDirection));
        yield return AccessTools.DeclaredMethod(typeof(NestableUI), nameof(NestableUI.InputLStickDirection));
    }

    static void Prefix(ControllableUI __instance) =>
        Plugin.Guard("appearance-new-input", () => AppearanceEditorUi.ObserveNewEditorInput(__instance));
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.UpdateCachePartsList),
    new[] { typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ItemData>), typeof(BazaarCustomPageCategory) })]
internal static class NativeAppearanceChoiceListUpdate
{
    static void Postfix(BazaarCustomPageCategory category)
    {
        if (AppearanceEditorUi.Active)
            Plugin.Guard("native-choice-list", () => AppearanceEditorUi.EnsureChoiceList(category));
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.BazaarCustomDataList))]
internal static class NativeAppearanceDisplayList
{
    static void Postfix(ref Il2CppSystem.Collections.Generic.List<BazaarCustomItemData> __result)
    {
        if (AppearanceEditorUi.Active)
        {
            var list = __result;
            Plugin.Guard("native-display-list", () => AppearanceEditorUi.DecorateDisplayList(list));
        }
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.UpdateCachePartsList), new Type[0])]
internal static class NativeAppearanceAllChoiceListsUpdate
{
    static void Postfix()
    {
        if (AppearanceEditorUi.Active)
            Plugin.Guard("native-choice-lists", AppearanceEditorUi.EnsureChoiceLists);
    }
}

[HarmonyPatch(typeof(LanguageManager), nameof(LanguageManager.GetLocalizeText),
    new[] { typeof(LocalizeTextTableType), typeof(uint), typeof(bool) })]
internal static class NativeAppearanceGuideLocalization
{
    static bool Prefix(LanguageManager __instance, LocalizeTextTableType tableType, uint textId, ref string __result)
    {
        Localization.SetLanguage(__instance.CurrentLanguage);
        if (!AppearanceEditorUi.TryGetGuideText(textId, out var text)) return true;
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
        if (PresetUiController.IsPresetUiOpen)
        {
            for (var i = __result.Count - 1; i >= 0; i--)
            {
                var guide = __result[i];
                if (guide != null && guide.button == GuideKey.Start &&
                    guide.textId == AppearanceEditorUi.CurrentPresetsGuideTextId)
                    __result.RemoveAt(i);
            }
        }

        if (PresetUiController.IsNameInputOpen)
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
                    StockCancelFooterTextId,
                    LocalizeTextTableType.KeyButtonGuideText,
                    InputKeyBind.InputActionSet.Menu));
            }
            return;
        }
        if (PresetUiController.IsSlotMenuOpen)
        {
            for (var i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null && __result[i].button == GuideKey.North)
                    __result.RemoveAt(i);
            __result.Insert(0, new UIButtonGuideData(
                GuideKey.North,
                PresetDeleteGuideTextId,
                LocalizeTextTableType.KeyButtonGuideText,
                InputKeyBind.InputActionSet.Menu));
            return;
        }
        if (!AppearanceEditorUi.ShouldInjectGuide || __result == null) return;
        // Appearance mode is a child view: B / Esc is the only exit action.
        // Hide both the stock detail guide and the X / F guide while it is active.
        if (AppearanceEditorUi.Active)
        {
            for (int i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null &&
                    (__result[i].button == GuideKey.North || __result[i].button == GuideKey.West))
                    __result.RemoveAt(i);
            if (!PresetUiController.IsPresetUiOpen)
            {
                __result.Insert(0, new UIButtonGuideData(
                    GuideKey.Start,
                    AppearanceEditorUi.CurrentPresetsGuideTextId,
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
            AppearanceEditorUi.CurrentGuideTextId,
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
        if (!AppearanceEditorUi.Active) return true;
        return false;
    }
}

// The stock input route plays its UI sound before calling OnShowDetail.  Blocking
// the detail callback alone therefore leaves a stray sound behind in appearance
// mode.  Consume the North / Y action at the controllable-UI boundary instead.
[HarmonyPatch(typeof(ControllableUI), nameof(ControllableUI.OnNorth))]
internal static class NativeAppearanceDetailInputGuard
{
    static bool Prefix(ControllableUI __instance)
    {
        // UISelectDialog receives Y through ControllableUI.OnNorth.  Handle
        // our slot-list command here before the appearance page's generic
        // detail guard consumes it.
        bool handled = false;
        Plugin.Guard("preset-delete-input", () => handled = PresetUiController.TryOpenDeleteForFocusedSlot(__instance), PresetUiController.Abort);
        if (handled) return false;
        if (!AppearanceEditorUi.Active) return true;
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
        if (AppearanceEditorUi.Active || __instance == null) return;
        var dialogs = Resources.FindObjectsOfTypeAll<UIDefaultDialog>();
        foreach (var dialog in dialogs)
        {
            if (dialog == null || !dialog.gameObject.activeInHierarchy || dialog.infoId != StockObjectExitDialogId) continue;
            AppearanceEditorUi.ObserveStockObjectExitChoice(id);
            return;
        }
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.ShowPage))]
internal static class NativePageShown
{
    static void Prefix(UIBazaarCustomPage __instance) =>
        Plugin.Guard("native-ui-page-prepare", () => AppearanceEditorUi.Observe(__instance), AppearanceEditorUi.Abort);

    static void Postfix() => Plugin.Guard("native-ui-page-ready", AppearanceEditorUi.Sync, AppearanceEditorUi.Abort);
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnFocusIn))]
internal static class NativeFocus
{
    static bool Prefix(BazaarCustomItemData data, BazaarCustomPageCategory category)
    {
        if (AppearanceEditorUi.TryPreserveExitPoseOnFocus(data, category)) return false;
        if (!AppearanceEditorUi.Active || AppearanceEditorUi.AllowStockFocus)
        {
            AppearanceEditorUi.RememberFocus(data, category);
            return true;
        }
        // Keep the fallback aligned with browsing, including unconfirmed previews.
        AppearanceEditorUi.RememberFocus(data, category);
        Plugin.Guard("native-ui-focus", () => AppearanceEditorUi.Select(data, category, false), AppearanceEditorUi.Abort);
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.GetSetFocusId))]
internal static class NativeAppearanceInitialFocus
{
    static void Postfix(UIBazaarCustomPage __instance, BazaarCustomPageCategory category, ref int __result)
    {
        if (AppearanceEditorUi.TryGetAppearanceFocusId(__instance, category, out int focusId))
            __result = focusId;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.FocusCategoryBeforeModel))]
internal static class NativeAppearanceTabPreviewGuard
{
    static bool Prefix(BazaarCustomPageCategory category)
    {
        if (!AppearanceEditorUi.Active) return true;
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
        if (AppearanceEditorUi.Active)
            Plugin.Guard("appearance-checkmark", () => AppearanceEditorUi.UpdateCheckmark(__instance));
    }

}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnDeside))]
internal static class NativeDecide
{
    static bool Prefix(UIBazaarCustomPage __instance, BazaarCustomItemData data)
    {
        if (!AppearanceEditorUi.Active) return true;
        AppearanceEditorUi.CancelPendingExitFocus();
        if (PresetUiController.IsPresetUiOpen) return false;
        Plugin.Guard("native-ui-decide", () => AppearanceEditorUi.Select(data, __instance.selectTabCategory, true), AppearanceEditorUi.Abort);
        return false;
    }
}

[HarmonyPatch]
internal static class PresetEditorInputEligibility
{
    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        // Icons override eligibility independently of the page. Neither should
        // accept background input in the gap between official preset dialogs.
        yield return AccessTools.DeclaredMethod(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.IsInputEnable));
        yield return AccessTools.DeclaredMethod(typeof(UIIconContent), nameof(UIIconContent.IsInputEnable));
    }

    static void Postfix(ControllableUI __instance, ref bool __result)
    {
        if (!__result) return;
        bool blocked = false;
        Plugin.Guard("preset-background-input", () =>
            blocked = PresetUiController.ShouldBlockEditorInput(__instance), PresetUiController.Abort);
        if (blocked) __result = false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnCanacel))]
internal static class NativeCancel
{
    static bool Prefix()
    {
        // The preset flow owns B even between two official dialogs.
        if (AppearanceEditorUi.Active && PresetUiController.IsPresetUiOpen) return false;
        if (AppearanceEditorUi.IsModeTransitioning) return false;
        if (!AppearanceEditorUi.Active) return true;
        // A modal dialog or keyboard owns B / Esc until it closes. Let the game's
        // own dialog manager handle it instead of leaving the appearance editor.
        var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
        if (manager != null && manager.TryGetDialogMask(out _)) return true;
        Plugin.Guard("native-ui-cancel", AppearanceEditorUi.LeaveAppearanceFromCancel, AppearanceEditorUi.Abort);
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage.OnClose))]
internal static class NativePageClosed
{
    static void Prefix() => Plugin.Guard("native-ui-close", AppearanceEditorUi.Exit, AppearanceEditorUi.Abort);
}

[HarmonyPatch(typeof(BazaarManager), nameof(BazaarManager.SetCustomParts))]
internal static class NativePlacementGuard
{
    static bool Prefix(uint itemId, BazaarCustomItemData.PartsCategory category, int index, ref bool __state)
    {
        __state = !AppearanceEditorUi.Active;
        if (__state) return true;
        return false;
    }

    static void Postfix(BazaarCustomItemData.PartsCategory category, int index, bool __state)
    {
        if (!__state) return;
        Plugin.Guard("actual-placement-changed", () => AppearanceSession.ActualPlacementChanged(category, index));
    }
}

[HarmonyPatch(typeof(ControllableUI), nameof(ControllableUI.OnWest))]
internal static class NativeAppearanceInput
{
    static bool Prefix(ControllableUI __instance)
    {
        if (!AppearanceEditorUi.CanToggle || __instance == null || AppearanceEditorUi.PageTransform == null ||
            !__instance.transform.IsChildOf(AppearanceEditorUi.PageTransform)) return true;
        if (AppearanceEditorUi.IsModeTransitioning) return false;
        // West/X/F is the entry command only. Leaving the appearance mode is always
        // handled by the normal cancel command, so it reads as a child editor.
        Plugin.Guard("native-ui-mode-enter", AppearanceEditorUi.EnterAppearanceFromWest, AppearanceEditorUi.Abort);
        return false;
    }
}

[HarmonyPatch(typeof(UIMenuManager), nameof(UIMenuManager.OnStart))]
internal static class NativePresetStartProbe
{
    static bool Prefix()
    {
        if (!AppearanceEditorUi.Active) return true;
        if (PresetUiController.IsPresetUiOpen) return false;
        Plugin.Guard("preset-dialog-probe", PresetUiController.Open, PresetUiController.Abort);
        return false;
    }
}

[HarmonyPatch(typeof(UIBazaarCustomPage), nameof(UIBazaarCustomPage._BazaarCustomScrollGroupData_b__26_0))]
internal static class NativeAppearanceChoiceSound
{
    private static Il2CppSystem.Func<UISoundTypes> cancelSound;

    static void Postfix(BazaarCustomItemData data, ref UISound __result)
    {
        if (!AppearanceEditorUi.Active || data == null ||
            !AppearanceEditorUi.IsModeChoiceForSound(data) || __result == null) return;
        cancelSound ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Func<UISoundTypes>>(
            (Func<UISoundTypes>)(() => UISoundTypes.Cancel));
        __result.ResetFuncEast(cancelSound);
    }
}
