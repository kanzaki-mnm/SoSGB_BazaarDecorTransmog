using BokuMono;
using HarmonyLib;
using UnityEngine;

namespace BazaarDecorTransmog;

// Preserve the registered IL2CPP component name while separating its lifecycle
// from the managed model and session responsibilities.
public sealed class PrototypeDriver : MonoBehaviour
{
    public PrototypeDriver(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        Plugin.Guard("transmog-update", AppearanceSession.Tick, AppearanceEditorUi.Abort);
        Plugin.Guard("preset-ui-update", PresetUiController.Tick, PresetUiController.Abort);
    }
    public void LateUpdate()
    {
        Plugin.Guard("transmog-decision-landing", AppearanceSession.UpdateDecisionDrops);
        // Reassert presentation after the game's Update, before rendering. This used
        // to run from the debug panel's OnGUI and must survive removal of that panel.
        Plugin.Guard("appearance-presentation", AppearanceSession.RefreshEditorPresentation, AppearanceEditorUi.Abort);
    }
    public void OnDestroy() => Plugin.Guard("transmog-cleanup", AppearanceEditorUi.Abort);
}

[HarmonyPatch(typeof(BazaarMyShop), nameof(BazaarMyShop.LoadBazaarPartsModel))]
internal static class ObserveShop
{
    static void Prefix(BazaarMyShop __instance) => Plugin.Guard("observe-shop", () => AppearanceSession.Observe(__instance));
}

[HarmonyPatch(typeof(BazaarManager), nameof(BazaarManager.OpenBazaarCustomMenu), new Type[] { })]
internal static class BeforeEditing
{
    static void Prefix() => Plugin.Guard("before-editor", AppearanceSession.Suspend);
}
