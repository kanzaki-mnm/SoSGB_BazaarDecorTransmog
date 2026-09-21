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

        internal void Tick()
        {
            var model = Parts.m_PartsObject?.Item2;
            int index = Parts.m_PartsIndex;
            var binding = Prototype.ShelfBinding(index);
            string bindingKey = BindingKey(binding);
            if (!Prototype.Enabled || binding == null || binding.IsActual || binding.IsHidden || model == null)
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
            if (Source != null && AppliedBinding != null && AppliedBinding != bindingKey) Restore("closed shelf binding changed");
            if (Pending && Time.unscaledTime - RequestStarted > 20f)
            {
                Generation++;
                Pending = false;
                Plugin.Emit("ClosedShelfRejected", new { index, reason = "target request timed out" });
            }
            if (Source != null || Pending || Time.unscaledTime - RequestStarted < 0.5f) return;

            var manager = BazaarMyShopClosed.BM;
            uint actualId = ActualShelfId(manager, index);
            var actualPart = mdm?.CustomPartsMaster?.GetMasterData(actualId);
            if (actualId == 0 || actualPart == null || actualPart.Category != Category.Shelf) return;
            if (model.name != actualPart.modelName && model.name != actualPart.modelName + "(Clone)") return;
            uint visualId = SlotRuntime.ResolveVisual(binding, Category.Shelf);
            var target = mdm.CustomPartsMaster.GetMasterData(visualId);
            if (visualId == 0 || target == null || !SlotRuntime.ShelfVisualRoot(model)) return;

            Pending = true;
            RequestStarted = Time.unscaledTime;
            int ticket = ++Generation;
            int sourceInstance = model.GetInstanceID();
            Plugin.Emit("ClosedShelfTransmogRequest", new { index, actualId, visualId, target.modelName, source = model.name });
            BazaarMyShopClosed.RM.GetLoadCustomPartsModelData(target.modelName,
                (Il2CppSystem.Action<bool, GameObject>)((ok, prefab) => Plugin.Guard("closed-shelf-callback", () =>
                {
                    if (ticket != Generation) return;
                    Pending = false;
                    var current = Parts.m_PartsObject?.Item2;
                    var currentBinding = Prototype.ShelfBinding(index);
                    if (!Prototype.Enabled || current == null || current.GetInstanceID() != sourceInstance ||
                        BindingKey(currentBinding) != bindingKey || ActualShelfId(BazaarMyShopClosed.BM, index) != actualId) return;
                    if (!ok || !SlotRuntime.ShelfVisualRoot(prefab))
                    {
                        Plugin.Emit("ClosedShelfRejected", new { index, reason = "target root mesh unavailable", actualId, visualId });
                        return;
                    }
                    Apply(current, prefab, bindingKey, index, actualId, visualId);
                })), CacheLevel.Permanent);
        }

        private void Apply(GameObject model, GameObject prefab, string bindingKey, int index, uint actualId, uint visualId)
        {
            Restore("closed shelf reapply");
            Source = model;
            AppliedBinding = bindingKey;
            var sourceNode = SlotRuntime.ShelfVisualNode(model);
            var targetNode = SlotRuntime.ShelfVisualNode(prefab);
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
                Plugin.Emit("ClosedShelfTransmogApplied", new { index, actualId, visualId, unchanged,
                    sourceMesh = OriginalMesh.name, visualMesh = targetFilter.sharedMesh.name,
                    sourceNode = sourceNode.name, visualNode = targetNode.name,
                    preservedChildRenderers = model.GetComponentsInChildren<Renderer>(true).Length - 1 });
                if (!unchanged) Restore("closed shelf placement verification mismatch");
            }
            catch { Restore("closed shelf apply failed"); throw; }
        }

        internal void Restore(string reason)
        {
            Generation++;
            Pending = false;
            bool changed = Source != null;
            if (Filter != null && OriginalMesh != null) Filter.sharedMesh = OriginalMesh;
            if (Renderer != null && OriginalMaterials != null) Renderer.sharedMaterials = OriginalMaterials;
            Source = null;
            AppliedBinding = null;
            Filter = null;
            Renderer = null;
            OriginalMesh = null;
            OriginalMaterials = null;
            if (changed) Plugin.Emit("ClosedShelfTransmogRestored", new { index = Parts.m_PartsIndex, reason });
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
        Plugin.Emit("ClosedShelfTracked", new { index = __instance.m_PartsIndex });
    });

    internal static void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        RemoveDead();
        foreach (var entry in entries) entry.Tick();
    }

    internal static void RestoreAll(string reason)
    {
        foreach (var entry in entries) entry.Restore(reason);
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
