using System.Reflection;
using System.Text.Json;
using BokuMono;

namespace BazaarDecorTransmog;

internal static class Localization
{
    private static readonly Dictionary<string, Dictionary<string, string>> texts = new(StringComparer.OrdinalIgnoreCase);
    private static Language currentLanguage = Language.en;

    internal static void Load()
    {
        texts.Clear();
        string directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "i18n");
        if (!Directory.Exists(directory))
        {
            Plugin.Logger.LogWarning($"BDT i18n folder not found: {directory}");
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (file != null) texts[Path.GetFileNameWithoutExtension(path)] = file;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"BDT could not load translation file {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    internal static void SetLanguage(Language language) => currentLanguage = language;

    internal static string Get(string key)
    {
        string language = currentLanguage.ToString();
        if (texts.TryGetValue(language, out var selected) && selected.TryGetValue(key, out var text)) return text;
        if (texts.TryGetValue("en", out var english) && english.TryGetValue(key, out text)) return text;
        return key;
    }

    internal static string GetOrFallback(string key, string japanese, string english)
    {
        string value = Get(key);
        return value == key ? (currentLanguage == Language.ja ? japanese : english) : value;
    }

    // Slot numbers and their separator are structural UI, not translated prose.
    // Keep the punctuation natural for the two supported language families while
    // leaving the actual slot-state label in the translation files.
    internal static string SlotLabelPrefix(int oneBasedSlot) =>
        currentLanguage == Language.ja ? oneBasedSlot + "：" : oneBasedSlot + ": ";
}
