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
        var match = new Match(savedId, model);
        foreach (var part in parts)
            if (part.Category == category) match.Consider(part.Id, part.Model);
        return match.Result;
    }

    // Shared by the managed tests and the IL2CPP master walk, without building
    // a managed copy of every master row. Duplicate rows remain ambiguous;
    // only the saved ID with the same model can disambiguate them.
    internal struct Match
    {
        private readonly uint savedId;
        private readonly string model;
        private uint candidate;
        private bool found, ambiguous, exact;

        internal Match(uint savedId, string model)
        {
            this.savedId = savedId;
            this.model = model;
            candidate = 0;
            found = ambiguous = exact = false;
        }

        internal void Consider(uint id, string modelName)
        {
            if (modelName != model) return;
            ambiguous |= found;
            found = true;
            candidate = id;
            exact |= id == savedId;
        }

        internal uint Result => exact ? savedId : found && !ambiguous ? candidate : 0;
    }
}
