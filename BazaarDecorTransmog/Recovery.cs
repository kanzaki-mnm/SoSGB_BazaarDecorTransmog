namespace BazaarDecorTransmog;

internal static class Recovery
{
    // Attempt every independent restoration, but do not let a caller continue
    // applying a new appearance as though an incomplete restoration succeeded.
    internal static void Run(params Action[] steps) => Run((IEnumerable<Action>)steps);

    internal static void Run(IEnumerable<Action> steps)
    {
        List<Exception> errors = null;
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception ex) { (errors ??= new()).Add(ex); }
        }
        if (errors != null) throw new AggregateException("Appearance restoration was incomplete.", errors);
    }
}
