namespace PinPoint.Helpers;

/// <summary>Runs an action once when disposed — for "do X until the end of this block" state.</summary>
public sealed class Scope(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}
