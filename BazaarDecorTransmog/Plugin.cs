using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BokuMono;
using HarmonyLib;
using UnityEngine;

namespace BazaarDecorTransmog;

[BepInPlugin("com.icy.bazaardecortransmog", "BazaarDecorTransmog", Plugin.Version)]
public sealed class Plugin : BasePlugin
{
    internal const string Version = "0.9.165";
    internal static ManualLogSource Logger = null!;
    private static readonly HashSet<string> ReportedErrors = new();

    public override void Load()
    {
        Logger = Log;
        var transmogHarmony = new Harmony("com.icy.bazaardecortransmog.prototype");
        try
        {
            Localization.Load();
            PresetStorage.Report = ReportStorage;
            AppearanceSession.Configure();
            transmogHarmony.CreateClassProcessor(typeof(ObserveShop)).Patch();
            transmogHarmony.CreateClassProcessor(typeof(BeforeEditing)).Patch();
            foreach (var patch in new[] { typeof(ClosedTentProbe), typeof(ClosedTentModelReady), typeof(ClosedShelfProbe), typeof(NativeFieldModelReady), typeof(NativeFieldModelInitialized), typeof(NativePageShown), typeof(NativeFocus), typeof(NativeDecide), typeof(NativeAppearanceChoiceSound), typeof(NativeCancel), typeof(NativePageClosed), typeof(NativePlacementGuard), typeof(NativeAppearanceInput), typeof(NativePresetStartProbe), typeof(PresetNameProbeCancel), typeof(PresetCompletionDialogCancel), typeof(PresetEmptySlotDecisionGuard), typeof(NativeAppearanceGuideLocalization), typeof(NativeAppearanceFooterGuideData), typeof(NativeAppearanceDetailGuard), typeof(NativeAppearanceDetailInputGuard), typeof(NativeStockObjectExitChoiceGuideState) })
                transmogHarmony.CreateClassProcessor(patch).Patch();
            foreach (var patch in new[] { typeof(NativeAppearanceInitialFocus), typeof(NativeAppearanceTabPreviewGuard),
                typeof(NativeAppearanceCheckmark), typeof(NativeAppearanceChoiceListUpdate),
                typeof(NativeAppearanceAllChoiceListsUpdate), typeof(NativeAppearanceDisplayList) })
            {
                transmogHarmony.CreateClassProcessor(patch).Patch();
            }
            AppearanceEditorUi.Ready = true;
            AddComponent<PrototypeDriver>();
        }
        catch (Exception ex)
        {
            Error("startup", ex);
            AppearanceSession.Stop();
            Guard("startup-restore", () => AppearanceSession.Restore("startup failed"));
            // Only remove our registrations. Other mods must remain patched.
            Guard("startup-unpatch", transmogHarmony.UnpatchSelf);
            return;
        }
        Logger.LogInfo($"BazaarDecorTransmog {Version} loaded.");
    }

    internal static void Warn(string kind, object data) =>
        Logger.LogWarning("BDT " + JsonSerializer.Serialize(new { kind, data }));

    private static void ReportStorage(string kind, object data)
    {
        // Recovery matters to support; a normal load needs no diagnostic output.
        if (kind == "AppearanceDataRecovered") Warn(kind, data);
    }

    internal static void Guard(string operation, Action action, Action rollback = null)
    {
        try { action(); }
        catch (Exception ex)
        {
            Error(operation, ex);
            if (rollback == null) return;
            try { rollback(); }
            catch (Exception recoveryError)
            {
                Error(operation + ":restore", recoveryError);
                AppearanceSession.Stop();
                // A failed renderer must not prevent restoring the other slots.
                Guard("emergency-model-restore", () => AppearanceSession.Restore("recovery failed"));
            }
        }
    }

    private static void Error(string operation, Exception ex)
    {
        if (ReportedErrors.Add(operation))
            try { Logger?.LogWarning($"BDT DiagnosticError operation={operation}: {ex}"); } catch { }
    }

}

[HarmonyPatch(typeof(BazaarMyShop.__c__DisplayClass52_0),
    nameof(BazaarMyShop.__c__DisplayClass52_0._LoadBazaarPartsModel_b__0))]
internal static class NativeFieldModelReady
{
    static void Postfix() => Plugin.Guard("field-model-ready", AppearanceSession.RefreshFieldModelsNow);
}

[HarmonyPatch(typeof(BazaarMyShop), nameof(BazaarMyShop.InitializeBazaarPartsModel))]
internal static class NativeFieldModelInitialized
{
    static void Postfix() => Plugin.Guard("field-model-initialized", AppearanceSession.RefreshFieldModelsNow);
}
