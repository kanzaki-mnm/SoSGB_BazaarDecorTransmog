using BokuMono;
using HarmonyLib;
using UnityEngine;
using Category = BokuMono.BazaarCustomItemData.PartsCategory;
using UObject = UnityEngine.Object;

namespace BazaarDecorTransmog;

// The closed shop owns a second instance under BazaarMyShopClose/TentSpace.
// It uses the same model resource as the active shop but has a separate lifetime.
[HarmonyPatch(typeof(BazaarMyShopClosed.PartsObject), nameof(BazaarMyShopClosed.PartsObject.LoadParts))]
internal static class ClosedTentProbe
{
    private sealed class Entry
    {
        internal BazaarMyShopClosed.PartsObject Parts;
        internal string LastState;
        internal string AppliedBinding;
        internal GameObject Source;
        internal GameObject Visual;
        internal readonly List<(MeshRenderer Renderer, bool Enabled)> OriginalRenderers = new();
        internal int Generation;
        internal bool Pending;
        internal float RequestStarted;

        internal void Tick()
        {
            var root = Parts.m_PartsRoot;
            var model = Parts.m_PartsObject?.Item2;
            ObserveState(root, model);

            // During the editor-to-field transition this root exists but is inactive. Its
            // prefab materials are not ready yet; instantiating a replacement here produces
            // the one-frame magenta tent. Wait until the stock field root is live.
            if (root == null || !root.gameObject.activeInHierarchy) return;

            var binding = Prototype.TentBinding;
            string bindingKey = BindingKey(binding);
            if (!Prototype.Enabled || binding == null || binding.IsActual || model == null)
            {
                Restore("disabled, vanilla, or source unavailable");
                return;
            }
            var mdm = BokuMono.API.Bazaar.MDM;
            uint currentActualId = ActualTentId(BazaarMyShopClosed.BM);
            var currentActualPart = mdm?.CustomPartsMaster?.GetMasterData(currentActualId);
            if (Source != null && (currentActualPart == null ||
                model.name != currentActualPart.modelName && model.name != currentActualPart.modelName + "(Clone)"))
            {
                Restore("closed actual tent changed");
                return;
            }
            if (Source != null && Source != model) Restore("closed source replaced");
            if (Source != null && AppliedBinding != null && AppliedBinding != bindingKey) Restore("closed binding changed");
            if (binding.IsHidden)
            {
                if (Source != model || OriginalRenderers.Count == 0) MaskSource(model);
                Plugin.Emit("ClosedTentHidden", new { source = model.name });
                return;
            }
            if (Pending && Time.unscaledTime - RequestStarted > 20f)
            {
                Restore("closed request timed out");
                Plugin.Emit("ClosedTentRejected", new { reason = "target request timed out" });
            }
            if (Visual != null || Pending) return;
            if (Time.unscaledTime - RequestStarted < 0.5f) return;

            var manager = BazaarMyShopClosed.BM;
            uint actualId = ActualTentId(manager);
            var actualPart = mdm?.CustomPartsMaster?.GetMasterData(actualId);
            if (actualId == 0 || actualPart == null || actualPart.Category != Category.Tent) return;
            if (model.name != actualPart.modelName && model.name != actualPart.modelName + "(Clone)") return;
            uint visualId = SlotRuntime.ResolveVisual(binding, Category.Tent);
            var target = mdm.CustomPartsMaster.GetMasterData(visualId);
            if (visualId == 0 || target == null) return;
            if (!SlotRuntime.StaticVisualTree(model))
            {
                Plugin.Emit("ClosedTentRejected", new { reason = "source hierarchy unsupported", actualId, visualId });
                RequestStarted = Time.unscaledTime + 20f;
                return;
            }

            MaskSource(model);

            Pending = true;
            RequestStarted = Time.unscaledTime;
            int ticket = ++Generation;
            int sourceInstance = model.GetInstanceID();
            Plugin.Emit("ClosedTentTransmogRequest", new { actualId, visualId, target.modelName,
                source = model.name, active = root.gameObject.activeInHierarchy });
            BazaarMyShopClosed.RM.GetLoadCustomPartsModelData(target.modelName,
                (Il2CppSystem.Action<bool, GameObject>)((ok, prefab) => Plugin.Guard("closed-tent-callback", () =>
                {
                    if (ticket != Generation) return;
                    Pending = false;
                    var current = Parts.m_PartsObject?.Item2;
                    var currentBinding = Prototype.TentBinding;
                    if (!Prototype.Enabled || current == null || current.GetInstanceID() != sourceInstance ||
                        BindingKey(currentBinding) != bindingKey || ActualTentId(BazaarMyShopClosed.BM) != actualId)
                    { Restore("closed request superseded"); return; }
                    if (!ok || prefab == null || !SlotRuntime.StaticVisualTree(prefab))
                    {
                        Restore("closed target load failed");
                        Plugin.Emit("ClosedTentRejected", new { reason = "target load failed or hierarchy unsupported", actualId, visualId });
                        return;
                    }
                    Apply(current, prefab, bindingKey, actualId, visualId);
                })), CacheLevel.Permanent);
        }

        private void Apply(GameObject model, GameObject prefab, string bindingKey, uint actualId, uint visualId)
        {
            Restore("closed reapply");
            Source = model;
            AppliedBinding = bindingKey;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                OriginalRenderers.Add((renderer, renderer.enabled));
            try
            {
                Visual = UObject.Instantiate(prefab, model.transform.parent, false);
                Visual.name = "BDT_ClosedTentVisual_" + visualId;
                foreach (var node in Visual.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = model.layer;
                Visual.transform.localPosition = model.transform.localPosition;
                Visual.transform.localRotation = prefab.transform.localRotation;
                Visual.transform.localScale = prefab.transform.localScale;
                Visual.SetActive(true);
                int renderers = 0;
                foreach (var renderer in Visual.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.forceRenderingOff = false;
                    if (renderer.enabled) renderers++;
                }
                if (renderers == 0) throw new InvalidOperationException("Closed visual has no enabled renderer");
                foreach (var original in OriginalRenderers) original.Renderer.enabled = false;
                bool unchanged = ActualTentId(BazaarMyShopClosed.BM) == actualId;
                Plugin.Emit("ClosedTentTransmogApplied", new { actualId, visualId, unchanged,
                    source = model.name, visual = Visual.name, parent = model.transform.parent?.name,
                    active = Visual.activeInHierarchy, renderers });
                if (!unchanged) Restore("closed placement verification mismatch");
            }
            catch
            {
                Restore("closed apply failed");
                throw;
            }
        }

        private void MaskSource(GameObject model)
        {
            if (Source == model && OriginalRenderers.Count != 0) return;
            Restore("closed source remasked");
            Source = model;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                OriginalRenderers.Add((renderer, renderer.enabled));
                renderer.enabled = false;
            }
            Plugin.Emit("ClosedTentSourceMasked", new { source = model.name });
        }

        internal void Restore(string reason)
        {
            Generation++;
            Pending = false;
            bool changed = Visual != null || OriginalRenderers.Count != 0;
            foreach (var original in OriginalRenderers)
                if (original.Renderer != null) original.Renderer.enabled = original.Enabled;
            OriginalRenderers.Clear();
            if (Visual != null)
            {
                Visual.SetActive(false);
                UObject.Destroy(Visual);
            }
            Visual = null;
            Source = null;
            AppliedBinding = null;
            if (changed) Plugin.Emit("ClosedTentTransmogRestored", new { reason });
        }

        private void ObserveState(Transform root, GameObject model)
        {
            string key = (model == null ? "none" : model.GetInstanceID().ToString()) + ":" + root.gameObject.activeInHierarchy;
            if (LastState == key) return;
            LastState = key;
            var nodes = new List<object>();
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (nodes.Count >= 160) break;
                var components = new List<string>();
                foreach (var component in node.gameObject.GetComponents<Component>())
                    components.Add(component == null ? "missing" : component.GetIl2CppType().FullName);
                var filter = node.GetComponent<MeshFilter>();
                nodes.Add(new { name = node.name, parent = node.parent?.name, active = node.gameObject.activeSelf,
                    mesh = filter?.sharedMesh?.name, vertices = filter?.sharedMesh?.vertexCount ?? 0, components });
            }
            Plugin.Emit("ClosedTentState", new { model = model == null ? null : model.name,
                root = root.name, parent = root.parent?.name, active = root.gameObject.activeInHierarchy, nodes });
        }
    }

    private static readonly List<Entry> entries = new();

    static void Prefix(BazaarMyShopClosed.PartsObject __instance) => Plugin.Guard("closed-track", () =>
    {
        if (__instance.m_PartsCategory != Category.Tent) return;
        RemoveDead();
        if (entries.Any(e => e.Parts.Pointer == __instance.Pointer) || entries.Count >= 32) return;
        entries.Add(new Entry { Parts = __instance });
        Plugin.Emit("ClosedTentTracked", new { index = __instance.m_PartsIndex });
    });

    internal static void Tick()
    {
        RemoveDead();
        foreach (var entry in entries) entry.Tick();
    }

    internal static void OnModelLoaded(BazaarMyShopClosed.PartsObject parts)
    {
        if (parts == null || parts.m_PartsCategory != Category.Tent) return;
        RemoveDead();
        var entry = entries.FirstOrDefault(e => e.Parts.Pointer == parts.Pointer);
        entry?.Tick();
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
            entries[i].Restore("closed object destroyed");
            entries.RemoveAt(i);
        }
    }

    private static string BindingKey(VisualSlot value) => value == null ? "" : value.ItemId + ":" + value.ModelName;

    private static uint ActualTentId(BazaarManager manager)
    {
        var groups = manager?.customData?.PutPartsDataDic;
        if (groups == null) return 0;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group != null && group.Category == Category.Tent && group.DataDic != null && group.DataDic.Count > 0)
                return group.DataDic[0];
        }
        return 0;
    }
}

[HarmonyPatch(typeof(BazaarMyShopClosed.PartsObject.__c__DisplayClass4_0),
    nameof(BazaarMyShopClosed.PartsObject.__c__DisplayClass4_0._LoadParts_b__0))]
internal static class ClosedTentModelReady
{
    static void Postfix(BazaarMyShopClosed.PartsObject.__c__DisplayClass4_0 __instance) =>
        Plugin.Guard("closed-tent-model-ready", () => ClosedTentProbe.OnModelLoaded(__instance.__4__this));
}
