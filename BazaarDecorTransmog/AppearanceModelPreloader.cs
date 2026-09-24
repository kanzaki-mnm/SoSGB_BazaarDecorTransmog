using BokuMono;
using UnityEngine;

namespace BazaarDecorTransmog;

/// <summary>
/// Warms the game's own custom-parts cache while the stock editor is already open.
/// It deliberately keeps no prefab or instantiated object of its own: normal preview
/// loading remains the source of truth if warming is interrupted or fails.
/// </summary>
internal static class AppearanceModelPreloader
{
    private static readonly Queue<string> pending = new();
    private static BazaarMyShop shop;
    private static bool loading;
    private static int generation;

    internal static void Start(BazaarMyShop owner, IEnumerable<string> modelNames)
    {
        Cancel();
        if (owner?.RM == null || modelNames == null) return;

        shop = owner;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelName in modelNames)
            if (!string.IsNullOrWhiteSpace(modelName) && unique.Add(modelName))
                pending.Enqueue(modelName);
    }

    internal static void Cancel()
    {
        generation++;
        pending.Clear();
        loading = false;
        shop = null;
    }

    internal static void Tick()
    {
        if (loading || shop?.RM == null || pending.Count == 0) return;

        string modelName = pending.Dequeue();
        if (shop.RM.IsCachedCustomPartsModelData(modelName)) return;

        loading = true;
        int ticket = generation;
        try
        {
            shop.RM.GetLoadCustomPartsModelData(modelName,
                (Il2CppSystem.Action<bool, GameObject>)((_, _) =>
                {
                    // The resource manager owns the cached prefab. A callback from an old
                    // editor session must not advance or otherwise touch the new session.
                    if (ticket == generation) loading = false;
                }), CacheLevel.Permanent);
        }
        catch
        {
            // Preloading is only an optimization. Leave regular on-demand loading intact.
            if (ticket == generation) loading = false;
        }
    }
}
