using System.Diagnostics;

namespace ThirdPersonActionCamera;

internal sealed class AltCursorHold
{
    internal const double HoldSeconds = .5;
    private long? started;
    private bool cancelled;

    internal bool Active { get; private set; }

    internal bool Update(long now, bool altDown, bool otherKeyDown, bool eligible)
    {
        if (!altDown)
        {
            started = null;
            cancelled = false;
            return Active = false;
        }

        if (otherKeyDown || !eligible) Cancel();
        if (cancelled) return false;

        started ??= now;
        return Active = now - started.Value >= Stopwatch.Frequency * HoldSeconds;
    }

    internal void Cancel()
    {
        started = null;
        cancelled = true;
        Active = false;
    }
}
