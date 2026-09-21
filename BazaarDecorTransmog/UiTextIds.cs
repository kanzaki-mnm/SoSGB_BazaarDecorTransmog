namespace BazaarDecorTransmog;

// IDs are scoped to the table used at the call site; none of these constants
// grant ownership of a stock localization entry or its shared master data.
internal static class UiTextIds
{
    // Stock footer: KeyButtonGuideText. Stock choices: DialogChoiceText.
    // Keep expandable slot labels (0xBD700020 onward) separate from one-off IDs.
    internal const uint AppearanceGuideTextId = 0xBD700001;
    internal const uint PresetsGuideTextId = 0xBD700003;
    internal const uint StockCancelFooterTextId = 1200;
    internal const uint PresetDeleteGuideTextId = 0xBD700011;
    internal const uint PresetSaveTextId = 0xBD700005;
    internal const uint PresetLoadTextId = 0xBD700006;
    internal const uint PresetEmptySlotTextId = 0xBD700020;
    internal const uint PresetSaveCompletedTextId = 0xBD70000F;
    internal const uint PresetLoadCompletedTextId = 0xBD700010;
    internal const uint AppearanceExitConfirmTextId = 0xBD700014;
    internal const uint StockApplyAndReturnChoiceTextId = 1110;
    internal const uint StockDiscardAndReturnChoiceTextId = 1120;
    internal const uint StockCancelChoiceTextId = 1010;
    internal const uint NameInputTextId = 101031;
    internal const uint NameConfirmTextId = 101045;
    internal const uint DeleteCompletedTextId = 0xBD700013;
    internal const uint DeleteConfirmTextId = 0xBD700012;
    internal const uint StockYesChoiceTextId = 1000;
    internal const uint StockObjectExitDialogId = 116000;
}
