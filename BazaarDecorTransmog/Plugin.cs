using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BokuMono;
using HarmonyLib;
using UnityEngine;
using Category = BokuMono.BazaarCustomItemData.PartsCategory;

namespace BazaarDecorTransmog;

[BepInPlugin("com.icy.bazaardecortransmog", "BazaarDecorTransmog", Plugin.Version)]
public sealed class Plugin : BasePlugin
{
    internal const string Version = "0.9.156";
    internal static ManualLogSource Logger = null!;
    private static bool catalogDone;
    private static long sequence;
    private static readonly HashSet<string> SeenPrefabs = new();
    private static readonly HashSet<string> ReportedErrors = new();

    public override void Load()
    {
        Logger = Log;
        Localization.Load();
        Prototype.Configure();
        var transmogHarmony = new Harmony("com.icy.bazaardecortransmog.prototype");
        try
        {
            transmogHarmony.CreateClassProcessor(typeof(ObserveShop)).Patch();
            transmogHarmony.CreateClassProcessor(typeof(BeforeEditing)).Patch();
            foreach (var patch in new[] { typeof(ClosedTentProbe), typeof(ClosedTentModelReady), typeof(ClosedShelfProbe), typeof(NativeFieldModelReady), typeof(NativeFieldModelInitialized), typeof(NativePageShown), typeof(NativeFocus), typeof(NativeDecide), typeof(NativeAppearanceChoiceSound), typeof(NativeCancel), typeof(NativePageClosed), typeof(NativePlacementGuard), typeof(NativeAppearanceInput), typeof(NativePresetStartProbe), typeof(PresetNameProbeCancel), typeof(PresetCompletionDialogCancel), typeof(PresetEmptySlotDecisionGuard), typeof(PresetNameMasterProbe), typeof(NativeAppearanceGuideLocalization), typeof(NativeAppearanceFooterGuideData), typeof(NativeAppearanceDetailGuard), typeof(NativeAppearanceDetailInputGuard), typeof(NativeStockObjectExitChoiceGuideState) })
                transmogHarmony.CreateClassProcessor(patch).Patch();
            foreach (var patch in new[] { typeof(NativeAppearanceInitialFocus), typeof(NativeAppearanceTabPreviewGuard),
                typeof(NativeAppearanceCheckmark), typeof(NativeAppearanceChoiceListUpdate),
                typeof(NativeAppearanceAllChoiceListsUpdate), typeof(NativeAppearanceDisplayList) })
            {
                transmogHarmony.CreateClassProcessor(patch).Patch();
                Emit("PatchReady", new { patch = patch.Name });
            }
            NativeDecorUi.Ready = true;
            AddComponent<PrototypeDriver>();
            Emit("PrototypeReady", new { version = Version, enabled = Prototype.Enabled });
        }
        catch (Exception ex) { Error("prototype-start", ex); return; }
        Emit("Session", new { version = Version, utc = DateTime.UtcNow, gameVersion = Application.version,
            unityVersion = Application.unityVersion, language = Application.systemLanguage.ToString() });
        var harmony = new Harmony("com.icy.bazaardecortransmog");
        // The model/resource/layout probes served their implementation purpose
        // and produced a very large log. Keep their code available for future
        // investigation without installing the patches during normal use.
        Type[] patches = Array.Empty<Type>();
        int installed = 0;
        foreach (var patch in patches)
        {
            try { harmony.CreateClassProcessor(patch).Patch(); installed++; Emit("PatchReady", new { patch = patch.Name }); }
            catch (Exception ex) { Error("patch:" + patch.Name, ex); }
        }
        Emit("Ready", new { installed, expected = patches.Length, prototypeEnabled = Prototype.Enabled });
    }

    internal static void Emit(string kind, object data) =>
        Logger.LogInfo("BDT " + JsonSerializer.Serialize(new { seq = Interlocked.Increment(ref sequence), kind, data }));

    internal static void Guard(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { Error(operation, ex); }
    }

    private static void Error(string operation, Exception ex)
    {
        if (ReportedErrors.Add(operation))
            Logger.LogWarning($"BDT DiagnosticError operation={operation}: {ex}");
    }

    internal static void Catalog()
    {
        if (catalogDone) return;
        var mdm = BokuMono.API.Bazaar.MDM;
        var rows = mdm?.CustomPartsMaster?.list;
        var items = mdm?.ItemMaster;
        if (rows == null || rows.Count == 0 || items == null)
        {
            Emit("CatalogPending", new { reason = "masters not ready; retry on next event" });
            return;
        }
        var models = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        int errors = 0;
        Emit("CatalogBegin", new { count = rows.Count });
        for (int i = 0; i < rows.Count; i++)
        {
            try
            {
                var row = rows[i];
                bool hasItem = items.TryGetData(row.Id, out var item);
                string name = hasItem && item != null ? item.ItemName : null;
                Emit("Part", new { id = row.Id, category = row.Category.ToString(), categoryValue = (int)row.Category,
                    modelName = row.modelName, screenShotId = row.ScreenShotId, passiveEffectId = row.PassiveEffectId,
                    seriesCategory = row.SeriesCategory.ToString(), dlcId = row.DlcId,
                    itemMasterMatchedById = hasItem, name, nameTextId = hasItem && item != null ? (uint?)item.NameTextId : null });
                Add(models, row.Category + ":" + row.modelName, row.Id);
                if (!string.IsNullOrEmpty(name)) Add(names, name, row.Id);
            }
            catch (Exception ex) { errors++; Error("catalog-row:" + i, ex); }
        }
        foreach (var pair in models.Where(p => p.Value.Count > 1)) Emit("DuplicateCategoryModel", new { key = pair.Key, ids = pair.Value });
        foreach (var pair in names.Where(p => p.Value.Count > 1)) Emit("DuplicateDisplayName", new { name = pair.Key, ids = pair.Value });
        Emit("CatalogEnd", new { count = rows.Count, errors });
        // Retry at the next lifecycle event if a transient read failed.
        catalogDone = errors == 0;
    }

    private static void Add(Dictionary<string, List<uint>> map, string key, uint id)
    {
        if (!map.TryGetValue(key, out var ids)) map[key] = ids = new List<uint>();
        ids.Add(id);
    }

    internal static void Snapshot(BazaarManager manager, string stage)
    {
        Emit("ManagerEvent", new { stage, isCustomMode = manager.IsCustomMode });
        Guard("catalog", Catalog);
        DumpLayout("current", manager.customData);
        DumpLayout("edit", manager.editCustomData);
    }

    private static void DumpLayout(string source, BazaarManager.CustomData data)
    {
        if (data == null) { Emit("Layout", new { source, available = false }); return; }
        var slots = new List<object>();
        var groups = data.PutPartsDataDic;
        if (groups != null)
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group?.DataDic == null) continue;
                for (int i = 0; i < group.DataDic.Count; i++)
                    slots.Add(new { category = group.Category.ToString(), index = i, id = group.DataDic[i] });
            }
        Emit("Layout", new { source, available = true, shelfUpgradeCount = data.BazaarShelfUpgradeCount, slots });
    }

    internal static void Inspect(Category category, GameObject prefab, string source = "initialization-argument")
    {
        if (prefab == null) return;
        var key = source + ":" + category + ":" + prefab.name;
        if (SeenPrefabs.Contains(key)) return;
        var nodes = new List<object>();
        var pending = new Stack<(Transform transform, string path, int depth)>();
        pending.Push((prefab.transform, prefab.name, 0));
        while (pending.Count > 0 && nodes.Count < 160)
        {
            var (transform, path, depth) = pending.Pop();
            var components = new List<string>();
            foreach (var component in transform.gameObject.GetComponents<Component>())
                components.Add(component == null ? "<missing>" : component.GetIl2CppType().FullName);
            nodes.Add(new { path, components });
            if (depth < 12)
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    var child = transform.GetChild(i);
                    pending.Push((child, path + "/" + i + ":" + child.name, depth + 1));
                }
        }
        Emit("Prefab", new { source, category = category.ToString(), name = prefab.name, nodeLimit = 160, depthLimit = 12,
            nodeLimitReached = pending.Count > 0, nodes });
        SeenPrefabs.Add(key);
    }
}

[HarmonyPatch]
internal static class ManagerEvents
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Exact("FromSaveData", typeof(BazaarManager.CustomData), typeof(int));
        yield return Exact("OpenBazaarCustomMenu");
        yield return Exact("SetCustomParts", typeof(uint), typeof(Category), typeof(int));
        yield return Exact("SaveAndCloseBazaarCustom", typeof(Il2CppSystem.Action));
        yield return Exact("CloseBazaarCustom", typeof(Il2CppSystem.Action));
        yield return Exact("SetupPartsBuff");
    }
    private static MethodBase Exact(string name, params Type[] parameters) =>
        AccessTools.Method(typeof(BazaarManager), name, parameters) ?? throw new MissingMethodException(name);
    // Postfix only observes the synchronous return, not async UI/asset completion.
    static void Postfix(BazaarManager __instance, MethodBase __originalMethod) =>
        Plugin.Guard("snapshot:" + __originalMethod.Name, () => Plugin.Snapshot(__instance, __originalMethod.Name + ":returned"));
}

[HarmonyPatch(typeof(BazaarMyShop), nameof(BazaarMyShop.LoadBazaarPartsModel))]
internal static class ModelRequests
{
    static void Prefix(uint itemId, Category category, int index) => Plugin.Guard("model-request", () =>
    {
        Plugin.Emit("ModelRequest", new { itemId, category = category.ToString(), index });
        Plugin.Guard("catalog", Plugin.Catalog);
    });
}

[HarmonyPatch(typeof(ResourceManager), nameof(ResourceManager.GetLoadCustomPartsModelData))]
internal static class ResourceRequests
{
    static void Prefix(string modelName) => Plugin.Guard("resource-request", () =>
        Plugin.Emit("ResourceRequest", new { modelName }));
}

[HarmonyPatch(typeof(BazaarMyShop), nameof(BazaarMyShop.InitializeBazaarPartsModel))]
internal static class PrefabInspection
{
    static void Prefix(Category category, GameObject prefab) =>
        Plugin.Guard("prefab:" + category, () => Plugin.Inspect(category, prefab));
}

[HarmonyPatch(typeof(BazaarMyShop.__c__DisplayClass52_0),
    nameof(BazaarMyShop.__c__DisplayClass52_0._LoadBazaarPartsModel_b__0))]
internal static class NativeFieldModelReady
{
    static void Postfix() => Plugin.Guard("field-model-ready", Prototype.RefreshFieldModelsNow);
}

[HarmonyPatch(typeof(BazaarMyShop), nameof(BazaarMyShop.InitializeBazaarPartsModel))]
internal static class NativeFieldModelInitialized
{
    static void Postfix() => Plugin.Guard("field-model-initialized", Prototype.RefreshFieldModelsNow);
}

[HarmonyPatch(typeof(BazaarMyShopClosed.PartsObject), nameof(BazaarMyShopClosed.PartsObject.LoadParts))]
internal static class ClosedRequests
{
    static void Prefix(BazaarMyShopClosed.PartsObject __instance) => Plugin.Guard("closed-request", () =>
    {
        Plugin.Emit("ClosedModelRequest", new { category = __instance.m_PartsCategory.ToString(), index = __instance.m_PartsIndex });
        Plugin.Guard("catalog", Plugin.Catalog);
    });
}

[HarmonyPatch]
internal static class ShopSnapshots
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BazaarMyShop), "SetCustomCursor", new[] { typeof(Category), typeof(int) })
            ?? throw new MissingMethodException("SetCustomCursor");
        yield return AccessTools.Method(typeof(BazaarMyShop), "CloseBazaarCustomMode", Type.EmptyTypes)
            ?? throw new MissingMethodException("CloseBazaarCustomMode");
    }

    static void Postfix(BazaarMyShop __instance, MethodBase __originalMethod) => Plugin.Guard("shop-snapshot", () =>
    {
        var models = __instance.putPartsModelDic;
        Plugin.Emit("ShopSnapshot", new { stage = __originalMethod.Name + ":returned", available = models != null });
        if (models == null) return;
        foreach (Category category in new[] { Category.Tent, Category.Shelf, Category.OrnamentS, Category.OrnamentL, Category.OrnamentSp })
        {
            if (!models.TryGetValue(category, out var slots) || slots == null) continue;
            for (int i = 0; i < slots.Length; i++)
            {
                var model = slots[i];
                Plugin.Emit("LiveModel", new { category = category.ToString(), index = i, name = model == null ? null : model.name });
                Plugin.Guard("live-model:" + category, () => Plugin.Inspect(category, model, "live-instance"));
            }
        }
    });
}








