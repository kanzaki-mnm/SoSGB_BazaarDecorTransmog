namespace BazaarDecorTransmog;

// Retire earlier requests and consume before side effects can re-enter callbacks.
internal sealed class UiNotificationGate
{
    private long version;
    internal bool Pending { get; private set; }
    internal long Begin() { Pending = true; return ++version; }
    internal bool Consume(long ticket)
    {
        if (!Pending || ticket != version) return false;
        Pending = false;
        return true;
    }
    internal void Invalidate() { Pending = false; version++; }
    internal bool RecoverClosed(long ticket, bool targetEnded, bool managerReleased)
    {
        // Visibility alone is insufficient while official history/mask cleanup runs.
        return targetEnded && managerReleased && Consume(ticket);
    }
}
