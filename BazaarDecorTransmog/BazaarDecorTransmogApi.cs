namespace BazaarDecorTransmog;

/// <summary>
/// Read-only integration surface for optional companion Mods.
/// Consumers should treat a missing type or an unsupported version as an
/// unavailable integration and continue operating independently.
/// </summary>
public static class BazaarDecorTransmogApi
{
    /// <summary>The version of this API contract, independent of the Mod version.</summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// True from the start of the transition into appearance editing until the
    /// stock decor editor has been restored during the transition out.
    /// </summary>
    public static bool IsAppearanceEditorActive =>
        AppearanceEditorUi.Active || AppearanceEditorUi.IsEnteringAppearanceMode;
}
