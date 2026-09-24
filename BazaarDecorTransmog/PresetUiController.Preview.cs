using BokuMono;
using BokuMono.Data;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarDecorTransmog;

// Kept in the same controller so preview disposal and pooled-dialog restoration
// share the existing close callback and cannot race a subsequent menu.
internal static partial class PresetUiController
{
    private const float ObjectPreviewOffsetX = 390f;
    private const float SlotDialogOffsetX = -420f;
    private static bool rowLoadAttempted;
    private static Il2CppSystem.Action rowLoadedCallback;
    private static GameObject objectPreview;
    private static RectTransform shiftedDialog;
    private static Vector2 originalDialogPosition;
    // The select dialog is pooled. Preserve its shifted position through its
    // close animation, then restore it before the next dialog reuses it.
    private static RectTransform closingShiftedDialog;
    private static Vector2 closingDialogOriginalPosition;
    private static bool previewAttempted;
    private static int previewSlot = -1;
    private static UICustomPartsListItem[] previewRows;
    // Used only when the stock page has no serialized category order.
    private static readonly BazaarCustomPageCategory[] FallbackPreviewOrder =
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

    private static void BuildObjectPreview(UIDialog dialog)
    {
        var prefabs = UnityEngine.Object.FindObjectOfType<UIPrefabsManager>();
        var page = prefabs?.UIPagePrefabCache(UILoadKey.UIBazaarMaxPriceCustomLogPage);
        if (page == null)
        {
            EnsureObjectPreviewTemplate();
            return;
        }
        // These rows are serialized references owned by the stock record page. They are
        // not guaranteed to be discoverable through a component walk while the prefab is
        // cached but has never been shown in this game session.
        var officialRows = page.GetComponent<UIBazaarMaxPriceCustomLogPage>()?.bazaarCustomPartsIconList;
        if (officialRows == null || officialRows.Count != FallbackPreviewOrder.Length)
        {
            Plugin.Warn("PresetObjectPreviewUnavailable", new { reason = "stock rows unavailable", count = officialRows?.Count ?? 0 });
            return;
        }
        var sourceRows = officialRows.ToArray();
        if (sourceRows.Any(row => row == null) ||
            sourceRows.Select(row => row.GetInstanceID()).Distinct().Count() != sourceRows.Length)
            throw new InvalidOperationException("Official preview rows are missing or duplicated.");

        var source = FindPreviewPanel(page.transform, sourceRows);
        var dialogRect = dialog.GetComponent<RectTransform>();
        var targetParent = dialogRect?.parent;
        if (source == null || targetParent == null) return;

        // Resolve the same serialized rows after cloning without relying on component
        // enumeration order, which can differ before and after Unity activates the clone.
        var rowPaths = sourceRows.Select(row => PreviewRowPath(source, row.transform)).ToArray();

        objectPreview = UnityEngine.Object.Instantiate(source.gameObject, targetParent, false);
        objectPreview.name = "BDT_PresetObjectPreview";
        var previewRect = objectPreview.GetComponent<RectTransform>();
        previewRect.anchorMin = new Vector2(0.5f, 0.5f);
        previewRect.anchorMax = new Vector2(0.5f, 0.5f);
        previewRect.pivot = new Vector2(0.5f, 0.5f);
        // Bring the record-style object panel closer to the slot list while
        // retaining a small visual gutter between the two official layouts.
        previewRect.anchoredPosition = new Vector2(ObjectPreviewOffsetX, 0f);
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

        previewRows = rowPaths.Select(path => ResolvePreviewRow(objectPreview.transform, path)).ToArray();
        if (previewRows.Any(row => row == null) ||
            previewRows.Select(row => row.GetInstanceID()).Distinct().Count() != previewRows.Length)
            throw new InvalidOperationException("Cloned preview rows do not match the official list.");

        shiftedDialog = dialogRect;
        originalDialogPosition = dialogRect.anchoredPosition;
        dialogRect.anchoredPosition = originalDialogPosition + new Vector2(SlotDialogOffsetX, 0f);

        var focused = FocusedSlotChoice();
        var focusedIndex = focused?.data?.id ?? 0;
        RefreshObjectPreview(savingSlot ? -1 : Math.Clamp(focusedIndex, 0, PresetStorage.UiSlotCount - 1));
    }

    private static Transform FindPreviewPanel(Transform pageRoot, UICustomPartsListItem[] rows)
    {
        if (rows.Any(row => row.transform == pageRoot || !row.transform.IsChildOf(pageRoot)))
            throw new InvalidOperationException("Official preview row is outside its page.");

        Transform panel = null;
        for (var candidate = rows[0].transform.parent;
             candidate != null && candidate != pageRoot;
             candidate = candidate.parent)
        {
            if (candidate.GetComponent<RectTransform>() == null ||
                candidate.GetComponent<Image>() == null ||
                candidate.GetComponent<VerticalLayoutGroup>() == null ||
                rows.Any(row => !row.transform.IsChildOf(candidate))) continue;
            if (panel != null)
                throw new InvalidOperationException("Official preview panel is ambiguous.");
            panel = candidate;
        }
        if (panel == null)
            throw new InvalidOperationException("Official preview panel is unavailable.");

        var containedRows = panel.GetComponentsInChildren<UICustomPartsListItem>(true);
        var officialIds = new HashSet<int>(rows.Select(row => row.GetInstanceID()));
        if (containedRows.Length != rows.Length ||
            containedRows.Any(row => !officialIds.Contains(row.GetInstanceID())))
            throw new InvalidOperationException("Official preview panel contains unexpected rows.");
        return panel;
    }

    private static int[] PreviewRowPath(Transform root, Transform row)
    {
        var path = new List<int>();
        while (row != root)
        {
            if (row == null)
                throw new InvalidOperationException("Official row is outside the cloned panel.");
            path.Add(row.GetSiblingIndex());
            row = row.parent;
        }
        path.Reverse();
        return path.ToArray();
    }

    private static UICustomPartsListItem ResolvePreviewRow(Transform root, int[] path)
    {
        foreach (int child in path) root = root.GetChild(child);
        var rows = root.GetComponents<UICustomPartsListItem>();
        if (rows.Length != 1)
            throw new InvalidOperationException("Ambiguous cloned preview row.");
        return rows[0];
    }

    private static void RefreshObjectPreview(int uiSlot)
    {
        if (objectPreview == null) return;
        previewSlot = uiSlot;
        var prefabs = UnityEngine.Object.FindObjectOfType<UIPrefabsManager>();
        var page = prefabs?.UIPagePrefabCache(UILoadKey.UIBazaarMaxPriceCustomLogPage);
        if (previewRows != null)
            foreach (var row in previewRows)
                if (row == null) { previewRows = null; break; }
        previewRows ??= objectPreview.GetComponentsInChildren<UICustomPartsListItem>(true).ToArray();
        var rows = previewRows;
        // The official record page owns the slot order. Read its serialized list
        // instead of assuming that an enum's numeric order will always match it.
        var officialOrder = page.GetComponent<UIBazaarMaxPriceCustomLogPage>()?.pageToCategoryList;
        var useOfficialOrder = officialOrder != null && officialOrder.Count == rows.Length;
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
        for (var i = 0; i < rows.Length; i++)
        {
            var slot = useOfficialOrder ? officialOrder[i] : FallbackPreviewOrder[i];
            var category = BazaarCustomItemData.ToPartsCategory(slot);
            var slotIndex = BazaarCustomItemData.ToPartsCategoryIndex(slot);
            var id = savingSlot ? AppearanceSession.AppearanceId(category, slotIndex) :
                AppearanceSession.UiSlotAppearanceId(uiSlot, category, slotIndex);
            if (id != 0 && itemMaster != null && itemMaster.TryGetData(id, out var item) && item != null)
            {
                rows[i].SetDisp(i, item);
                rows[i].partsIcon.enabled = true;
                rows[i].gameObject.SetActive(true);
            }
            else
            {
                // SetDisp also initializes the position's round icon. Use a
                // temporary valid item only for that setup, then hide its
                // object artwork and replace its name with an empty marker.
                var actualId = AppearanceSession.ActualAppearanceId(category, slotIndex);
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
                }
                rows[i].gameObject.SetActive(true);
            }
            // Show the saved behavior, not the item Actual happens to resolve to
            // today. Reuse the editor's cached artwork and localized label.
            var usesActual = savingSlot ? AppearanceSession.UsesActualAppearance(category, slotIndex) :
                AppearanceSession.UiSlotUsesActualAppearance(uiSlot, category, slotIndex);
            if (usesActual)
            {
                rows[i].partsName.text = Localization.Get("actual.title");
                rows[i].partsIcon.sprite = AppearanceEditorUi.ActualAppearanceBadgeSprite();
                rows[i].partsIcon.enabled = rows[i].partsIcon.sprite != null;
            }
        }
    }

    private static void RemoveObjectPreview(bool restoreDialogPosition = true)
    {
        Recovery.Run(() =>
        {
            if (restoreDialogPosition && shiftedDialog != null)
                shiftedDialog.anchoredPosition = originalDialogPosition;
            else if (!restoreDialogPosition && shiftedDialog != null)
            {
                closingShiftedDialog = shiftedDialog;
                closingDialogOriginalPosition = originalDialogPosition;
            }
            shiftedDialog = null;
        }, () =>
        {
            if (objectPreview != null) UnityEngine.Object.Destroy(objectPreview);
            objectPreview = null;
        });
        previewRows = null;
        previewSlot = -1;
        previewAttempted = false;
        slotDialog = null;
        slotChoiceBars = null;
    }

    private static void RestoreClosedSlotDialogPosition()
    {
        if (closingShiftedDialog != null)
            closingShiftedDialog.anchoredPosition = closingDialogOriginalPosition;
        closingShiftedDialog = null;
    }
}
