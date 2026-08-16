using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FactoryLine.Domain;

/// <summary>
/// A simulated production line behind the <see cref="IEquipmentSource"/> seam.
/// Each equipment runs its own state machine on a visible schedule
/// (Idle → Running → Completed → Idle), raising <see cref="StateChanged"/>
/// on every transition. Used as the demo driver; OPC-UA is a second adapter of
/// the same contract. An optional <see cref="IEquipmentGate"/> lets the
/// mini-MES hold an equipment in WAIT_MATERIAL: a held equipment moves to
/// Waiting and stops advancing until the gate releases it.
/// </summary>
public sealed class LineSimulator : IEquipmentSource, IAsyncDisposable
{
    private readonly ILogger<LineSimulator> _logger;
    private readonly LineSimulatorOptions _options;
    private readonly IEquipmentGate? _gate;
    private readonly object _gateLock = new();
    private readonly List<EquipmentMachine> _machines;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _running;

    public LineSimulator(IOptions<LineSimulatorOptions> options, ILogger<LineSimulator> logger, IEquipmentGate? gate = null)
    {
        _options = options.Value;
        _logger = logger;
        _gate = gate;

        _machines = Enumerable.Range(1, _options.EquipmentCount)
            .Select(i => new EquipmentMachine($"EQ-{i:00}"))
            .ToList();
    }

    public bool IsRunning
    {
        get
        {
            lock (_gateLock)
            {
                return _running;
            }
        }
    }

    public event EventHandler<EquipmentStateChange>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gateLock)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            _running = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunLoopAsync(_cts.Token);
        }

        _logger.LogInformation("Line simulator started ({EquipmentCount} equipment)", _machines.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runTask;
        lock (_gateLock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _cts?.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _logger.LogInformation("Line simulator stopped");
    }

    public Task<IReadOnlyList<EquipmentStateSnapshot>> GetCurrentStatesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gateLock)
        {
            return Task.FromResult<IReadOnlyList<EquipmentStateSnapshot>>(
                _machines.Select(m => m.Snapshot).ToList());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        var tasks = _machines.Select(m => AdvanceAsync(m, token)).ToList();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AdvanceAsync(EquipmentMachine machine, CancellationToken token)
    {
        var tick = TimeSpan.FromMilliseconds(_options.TickMilliseconds);

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(tick, token).ConfigureAwait(false);

                EquipmentStateChange change;
                lock (_gateLock)
                {
                    var held = _gate?.IsHeld(machine.Id) ?? false;
                    change = held ? machine.Hold() : machine.Advance();
                }

                _logger.LogInformation("Equipment {EquipmentId} -> {State}", change.EquipmentId, change.State);
                StateChanged?.Invoke(this, change);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class EquipmentMachine
    {
        private EquipmentState _state = EquipmentState.Idle;

        public EquipmentMachine(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public EquipmentStateSnapshot Snapshot => new(Id, _state, DateTimeOffset.UtcNow);

        public EquipmentStateChange Advance()
        {
            _state = _state switch
            {
                EquipmentState.Idle => EquipmentState.Running,
                EquipmentState.Waiting => EquipmentState.Running,
                EquipmentState.Running => EquipmentState.Completed,
                EquipmentState.Completed => EquipmentState.Idle,
                _ => EquipmentState.Running,
            };

            return new EquipmentStateChange(Id, _state, DateTimeOffset.UtcNow);
        }

        public EquipmentStateChange Hold()
        {
            if (_state != EquipmentState.Waiting)
            {
                _state = EquipmentState.Waiting;
            }

            return new EquipmentStateChange(Id, _state, DateTimeOffset.UtcNow);
        }
    }
}
