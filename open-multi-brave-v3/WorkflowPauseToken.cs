namespace OpenMultiBraveLauncherV3;

internal sealed class WorkflowPauseToken
{
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public void Pause() => Volatile.Write(ref _paused, 1);

    public void Resume() => Volatile.Write(ref _paused, 0);

    public async Task WaitWhileRunningAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitWhileRunningAsync(cancellationToken).ConfigureAwait(false);

            var slice = remaining < TimeSpan.FromMilliseconds(400)
                ? remaining
                : TimeSpan.FromMilliseconds(400);
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
            remaining -= slice;
        }
    }

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        DelayAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
}
