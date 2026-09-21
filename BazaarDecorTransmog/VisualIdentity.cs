namespace BazaarDecorTransmog;

// Pure managed resolver: never accept a reused ID pointing to another model.
internal static class VisualIdentity
{
    internal sealed class Part
    {
        internal readonly uint Id;
        internal readonly string Category;
        internal readonly string Model;
        internal Part(uint id, string category, string model) { Id = id; Category = category; Model = model; }
    }

    internal static uint Resolve(IEnumerable<Part> parts, uint savedId, string category, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0;
        var matches = parts.Where(p => p.Category == category && p.Model == model).ToArray();
        if (matches.Any(p => p.Id == savedId)) return savedId;
        return matches.Length == 1 ? matches[0].Id : 0;
    }
}
