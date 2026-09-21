using BokuMono;
using HarmonyLib;
using UnityEngine;
using Category = BokuMono.BazaarCustomItemData.PartsCategory;

namespace BazaarDecorTransmog;

// Closed shops own a separate center shelf instance. Only its root shell mesh is
// changed: child ShopItem objects, icons, sale points, and colliders remain native.
[HarmonyPatch(typeof(BazaarMyShopClosed.PartsObject), nameof(BazaarMyShopClosed.PartsObject.LoadParts))]
internal static class ClosedShelfProbe
{
    private sealed class Entry
    {
        internal BazaarMyShopClosed.PartsObject Parts;
        internal GameObject Source;
        internal string AppliedBinding;
        internal MeshFilter Filter;
        internal MeshRenderer Renderer;
        internal Mesh OriginalMesh;
        internal Material[] OriginalMaterials;
        internal int Generation;
        internal bool Pending;
        internal float RequestStarted;
        private GameObject pendingSource;
        private MeshRenderer pendingRenderer;
        private bool pendingRendererEnabled;
        private int maskedInstance;

        internal void Tick(bool immediate = false)
        {
            var model = Parts.m_PartsObject?.Item2;
            int index = Parts.m_PartsIndex;
            var binding = AppearanceSession.ShelfBinding(index);
            string bindingKey = BindingKey(binding);
            if (!AppearanceSession.Enabled || binding == null || binding.IsActual || binding.IsHidden || model == null)
            {
                Restore("disabled, vanilla, or source unavailable");
                return;
            }
            var mdm = BokuMono.API.Bazaar.MDM;
            uint currentActualId = ActualShelfId(BazaarMyShopClosed.BM, index);
            var currentActualPart = mdm?.CustomPartsMaster?.GetMasterData(currentActualId);
            if (Source != null && (currentActualPart == null ||
                model.name != currentActualPart.modelName && model.name != currentActualPart.modelName + "(Clone)"))
            {
                Restore("closed actual shelf changed");
                return;
            }
            if (Source != null && Source != model) Restore("closed shelf source replaced");
            if (Pending && pendingSource != model) Restore("closed shelf pending source replaced");
            if (Source != null && AppliedBinding != null && AppliedBinding != bindingKey) Restore("closed shelf binding changed");
            if (Pending && Time.unscaledTime - RequestStarted > 20f)
            {
                Restore("closed shelf request timed out");
                Plugin.Warn("ClosedShelfRejected", new { index, reason = "target request timed out" });
                return;
            }
            if (Source != null || Pending || !immediate && Time.unscaledTime - RequestStarted < 0.5f) return;

            var manager = BazaarMyShopClosed.BM;
            uint actualId = ActualShelfId(manager, index);
            var actualPart = mdm?.CustomPartsMaster?.GetMasterData(actualId);
            if (actualId == 0 || actualPart == null || actualPart.Category != Category.Shelf) return;
            if (model.name != actualPart.modelName && model.name != actualPart.modelName + "(Clone)") return;
            uint visualId = SlotAppearance.ResolveVisual(binding, Category.Shelf);
            var target = mdm.CustomPartsMaster.GetMasterData(visualId);
            if (visualId == 0 || target == null || !SlotAppearance.ShelfVisualRoot(model)) return;

            Pending = true;
            RequestStarted = Time.unscaledTime;
            int ticket = ++Generation;
            int sourceInstance = model.GetInstanceID();
            try
            {
                // Only the shelf shell is masked while its replacement loads.
                // Products, sale points and colliders belong to the stock instance.
                pendingSource = model;
                // A failed request may be retried by the periodic check. Keep
                // the restored vanilla shell visible during those retries.
                if (maskedInstance != sourceInstance)
                {
                    pendingRenderer = SlotAppearance.ShelfVisualNode(model).GetComponent<MeshRenderer>();
                    pendingRendererEnabled = pendingRenderer.enabled;
                    maskedInstance = sourceInstance;
                    pendingRenderer.enabled = false;
                }
                BazaarMyShopClosed.RM.GetLoadCustomPartsModelData(target.modelName,
                    (Il2CppSystem.Action<bool, GameObject>)((ok, prefab) => Plugin.Guard("closed-shelf-callback", () =>
                    {
                        if (ticket != Generation) return;
                        Pending = false;
                        try
                        {
                            var current = Parts.m_PartsObject?.Item2;
                            var currentBinding = AppearanceSession.ShelfBinding(index);
                            if (!AppearanceSession.Enabled || current == null || current.GetInstanceID() != sourceInstance ||
                                currentBinding == null || !currentBinding.IsReplacement ||
                                BindingKey(currentBinding) != bindingKey || ActualShelfId(BazaarMyShopClosed.BM, index) != actualId) return;
                            if (!ok || !SlotAppearance.ShelfVisualRoot(prefab))
                            {
                                Plugin.Warn("ClosedShelfRejected", new { index, reason = "target root mesh unavailable", actualId, visualId });
                                return;
                            }
                            Apply(current, prefab, bindingKey, index, actualId, visualId);
                        }
                        finally
                        {
                            // Apply restores the mask itself. A stale callback must
                            // never unmask a newer request's source.
                            if (ticket == Generation) UnmaskPendingSource();
                        }
                    }, () => Restore("closed shelf callback failed"))), CacheLevel.Permanent);
            }
            catch { Restore("closed shelf request failed"); throw; }
        }

        private void UnmaskPendingSource()
        {
            if (pendingRenderer != null) pendingRenderer.enabled = pendingRendererEnabled;
            pendingRenderer = null;
            pendingSource = null;
        }

        private void Apply(GameObject model, GameObject prefab, string bindingKey, int index, uint actualId, uint visualId)
        {
            Restore("closed shelf reapply");
            Source = model;
            AppliedBinding = bindingKey;
            var sourceNode = SlotAppearance.ShelfVisualNode(model);
            var targetNode = SlotAppearance.ShelfVisualNode(prefab);
            Filter = sourceNode.GetComponent<MeshFilter>();
            Renderer = sourceNode.GetComponent<MeshRenderer>();
            OriginalMesh = Filter.sharedMesh;
            OriginalMaterials = Renderer.sharedMaterials.ToArray();
            var targetFilter = targetNode.GetComponent<MeshFilter>();
            var targetRenderer = targetNode.GetComponent<MeshRenderer>();
            try
            {
                Filter.sharedMesh = targetFilter.sharedMesh;
                Renderer.sharedMaterials = targetRenderer.sharedMaterials;
                bool unchanged = ActualShelfId(BazaarMyShopClosed.BM, index) == actualId;
                if (!unchanged)
                {
                    Plugin.Warn("ClosedShelfVerificationMismatch", new { index, actualId });
                    Restore("closed shelf placement verification mismatch");
                }
            }
            catch { Restore("closed shelf apply failed"); throw; }
        }

        internal void Restore(string reason)
        {
            Generation++;
            Pending = false;
            Recovery.Run(UnmaskPendingSource, () =>
            {
                if (Filter != null && OriginalMesh != null) Filter.sharedMesh = OriginalMesh;
                Filter = null;
                OriginalMesh = null;
            }, () =>
            {
                if (Renderer != null && OriginalMaterials != null) Renderer.sharedMaterials = OriginalMaterials;
                Renderer = null;
                OriginalMaterials = null;
            });
            Source = null;
            AppliedBinding = null;
        }

    }

    private static readonly List<Entry> entries = new();
    private static float next;

    static void Prefix(BazaarMyShopClosed.PartsObject __instance) => Plugin.Guard("closed-shelf-track", () =>
    {
        if (__instance.m_PartsCategory != Category.Shelf) return;
        RemoveDead();
        if (entries.Any(e => e.Parts.Pointer == __instance.Pointer) || entries.Count >= 8) return;
        entries.Add(new Entry { Parts = __instance });
    });

    internal static void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        RemoveDead();
        foreach (var entry in entries) Plugin.Guard("closed-shelf-entry", () => entry.Tick(), () => entry.Restore("closed shelf update failed"));
    }

    internal static void OnModelLoaded(BazaarMyShopClosed.PartsObject parts)
    {
        if (parts == null || parts.m_PartsCategory != Category.Shelf) return;
        RemoveDead();
        var entry = entries.FirstOrDefault(e => e.Parts.Pointer == parts.Pointer);
        // The stock instance now exists. Do not wait for the periodic check:
        // the next rendered frame would otherwise expose the original mesh.
        if (entry != null) Plugin.Guard("closed-shelf-ready", () => entry.Tick(immediate: true), () => entry.Restore("closed shelf update failed"));
    }

    internal static void RestoreAll(string reason)
    {
        Recovery.Run(entries.Select(entry => (Action)(() => entry.Restore(reason))));
    }

    private static void RemoveDead()
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Parts.m_PartsRoot != null) continue;
            entries[i].Restore("closed shelf object destroyed");
            entries.RemoveAt(i);
        }
    }

    private static string BindingKey(VisualSlot value) => value == null ? "" : value.ItemId + ":" + value.ModelName;

    private static uint ActualShelfId(BazaarManager manager, int index)
    {
        var groups = manager?.customData?.PutPartsDataDic;
        if (groups == null) return 0;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group != null && group.Category == Category.Shelf && group.DataDic != null && index >= 0 && index < group.DataDic.Count)
                return group.DataDic[index];
        }
        return 0;
    }
}
