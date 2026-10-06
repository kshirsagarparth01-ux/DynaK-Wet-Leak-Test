namespace DynaK.Service.Services;

public sealed class StationRuntimeControl : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startSignal = new(0, 1);
    private StationRunSession? _session;
    private bool _disposed;

    public bool IsStartRequested
    {
        get
        {
            lock (_gate)
            {
                return _session?.IsStartRequested == true;
            }
        }
    }

    public bool RequestStart()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                return false;
            }

            _session = new StationRunSession();
            _startSignal.Release();
            return true;
        }
    }

    public async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        Task stopped;
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            _session.RequestStop();
            stopped = _session.Stopped;
        }

        await stopped.WaitAsync(cancellationToken);
    }

    public async Task<StationRunSession> WaitForStartAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _startSignal.WaitAsync(cancellationToken);
            lock (_gate)
            {
                if (_session?.IsStartRequested == true)
                {
                    return _session;
                }
            }
        }
    }

    public void CompleteStop(StationRunSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        session.CompleteStop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.RequestStop();
        }

        _startSignal.Dispose();
    }
}

public sealed class StationRunSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken StoppingToken => _stop.Token;
    public Task Stopped => _stopped.Task;
    public bool IsStartRequested => !_stop.IsCancellationRequested;

    public void RequestStop() => _stop.Cancel();

    public void CompleteStop()
    {
        _stopped.TrySetResult();
        Dispose();
    }

    public void Dispose() => _stop.Dispose();
}
