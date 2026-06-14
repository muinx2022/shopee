namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Chạy lượt: giữ tối đa N profile runner đồng thời; khi một profile xong thì tự bật profile kế tiếp
/// (theo th? t? danh s�ch, chua t?ng ch?y trong lu?t n�y).
/// </summary>
internal sealed class BatchRunnerScheduler
{
    private readonly HashSet<string> _dispatchedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeSlotIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _active;
    private int _fillBusy;

    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    public int DispatchedCount
    {
        get { lock (_gate) return _dispatchedIds.Count; }
    }

    public int ActiveSlotCount
    {
        get { lock (_gate) return _activeSlotIds.Count; }
    }

    public void Start()
    {
        lock (_gate)
        {
            _active = true;
            _dispatchedIds.Clear();
            _activeSlotIds.Clear();
        }
    }

    /// <summary>��nh d?u profile dang ch?y runner s?n (tr�nh dispatch tr�ng).</summary>
    public void SeedActiveSlot(string instanceId)
    {
        lock (_gate)
        {
            if (!_active) return;
            _dispatchedIds.Add(instanceId);
            _activeSlotIds.Add(instanceId);
        }
    }

    public void ReleaseSlot(string instanceId)
    {
        lock (_gate)
        {
            if (!_active) return;
            _activeSlotIds.Remove(instanceId);
        }
        TryFillSlots();
    }

    public void ReleaseSlotWithoutFill(string instanceId)
    {
        lock (_gate)
        {
            if (!_active) return;
            _activeSlotIds.Remove(instanceId);
        }
    }

    public bool ReserveSlot(string instanceId)
    {
        lock (_gate)
        {
            if (!_active) return false;
            _dispatchedIds.Add(instanceId);
            _activeSlotIds.Add(instanceId);
            return true;
        }
    }

    public HashSet<string> GetDispatchedSnapshot()
    {
        lock (_gate)
            return new HashSet<string>(_dispatchedIds, StringComparer.Ordinal);
    }

    public void Stop()
    {
        lock (_gate) _active = false;
    }

    public void OnRunnerLoopEnded(string instanceId)
    {
        lock (_gate)
        {
            if (!_active) return;
            _activeSlotIds.Remove(instanceId);
        }

        TryFillSlots();
    }

    public void TryFillSlots()
    {
        if (Interlocked.CompareExchange(ref _fillBusy, 1, 0) != 0)
            return;

        try
        {
            FillSlotsCore();
        }
        finally
        {
            Interlocked.Exchange(ref _fillBusy, 0);
        }
    }

    private void FillSlotsCore()
    {
        string? nextId;
        lock (_gate)
        {
            if (!_active) return;

            var maxConcurrent = Math.Max(1, _getMaxConcurrent());
            if (_activeSlotIds.Count >= maxConcurrent) return;
            if (_canDispatchMore is not null && !_canDispatchMore()) return;

            nextId = _findNextUndispatched?.Invoke(_dispatchedIds);
            if (nextId is null)
            {
                if (_activeSlotIds.Count == 0)
                {
                    _active = false;
                    _onBatchComplete?.Invoke(_dispatchedIds.Count);
                }
                return;
            }

            _dispatchedIds.Add(nextId);
            _activeSlotIds.Add(nextId);
        }

        _ = DispatchAsync(nextId);
    }

    private async Task DispatchAsync(string instanceId)
    {
        try
        {
            await (_dispatchRunner?.Invoke(instanceId) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onDispatchError?.Invoke(instanceId, ex.Message);
        }
        finally
        {
            // M?t slot xong kh?i d?ng ? th? b?t slot k? (tu?n t?, tr�nh m? 2 profile c�ng l�c).
            TryFillSlots();
        }
    }

    private Func<int>? _getMaxConcurrent;
    private Func<bool>? _canDispatchMore;
    private Func<HashSet<string>, string?>? _findNextUndispatched;
    private Func<string, Task>? _dispatchRunner;
    private Action<int>? _onBatchComplete;
    private Action<string, string>? _onDispatchError;

    public void Configure(
        Func<int> getMaxConcurrent,
        Func<bool> canDispatchMore,
        Func<HashSet<string>, string?> findNextUndispatched,
        Func<string, Task> dispatchRunner,
        Action<int> onBatchComplete,
        Action<string, string> onDispatchError)
    {
        _getMaxConcurrent = getMaxConcurrent;
        _canDispatchMore = canDispatchMore;
        _findNextUndispatched = findNextUndispatched;
        _dispatchRunner = dispatchRunner;
        _onBatchComplete = onBatchComplete;
        _onDispatchError = onDispatchError;
    }
}
