using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace FactoryLine.Domain;

/// <summary>
/// An <see cref="IEquipmentSource"/> implemented over OPC-UA: a UA client that
/// subscribes to the <see cref="SimulatorUaServer"/>'s equipment state
/// variables, exposes the same <see cref="StateChanged"/> contract as the
/// simulator, and drives the underlying line through the server's
/// <c>Start</c>/<c>Stop</c> methods.
/// </summary>
public sealed class OpcUaEquipmentSource : IEquipmentSource, IAsyncDisposable
{
    private readonly string _endpointUrl;
    private readonly IReadOnlyList<string> _equipmentIds;
    private readonly ILogger<OpcUaEquipmentSource> _logger;
    private readonly object _gate = new();
    private readonly List<EquipmentStateSnapshot> _states;
    private ISession? _session;
    private Subscription? _subscription;
    private bool _isRunning;
    private bool _disposed;

    public OpcUaEquipmentSource(string endpointUrl, IReadOnlyList<string> equipmentIds, ILogger<OpcUaEquipmentSource> logger)
    {
        _endpointUrl = endpointUrl;
        _equipmentIds = equipmentIds;
        _logger = logger;
        _states = equipmentIds.Select(id => new EquipmentStateSnapshot(id, EquipmentState.Idle, DateTimeOffset.UtcNow)).ToList();
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public event EventHandler<EquipmentStateChange>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _isRunning = true;
        }

        if (_subscription is null)
        {
            await SubscribeAsync(session, cancellationToken).ConfigureAwait(false);
        }

        var nsIndex = (ushort)session.NamespaceUris.GetIndex(SimulatorUaServer.NamespaceUri);
        await session.CallAsync(new NodeId("Simulator", nsIndex), new NodeId("Simulator/Start", nsIndex), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("OPC-UA adapter started the line via Simulator/Start");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _isRunning = false;
        }

        var nsIndex = (ushort)session.NamespaceUris.GetIndex(SimulatorUaServer.NamespaceUri);
        await session.CallAsync(new NodeId("Simulator", nsIndex), new NodeId("Simulator/Stop", nsIndex), cancellationToken).ConfigureAwait(false);

        await TearDownSubscriptionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("OPC-UA adapter stopped the line via Simulator/Stop");
    }

    public async Task<IReadOnlyList<EquipmentStateSnapshot>> GetCurrentStatesAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var nsIndex = (ushort)session.NamespaceUris.GetIndex(SimulatorUaServer.NamespaceUri);

        var snapshots = new List<EquipmentStateSnapshot>();
        foreach (var equipmentId in _equipmentIds)
        {
            var nodeId = new NodeId($"Equipment/{equipmentId}/State", nsIndex);
            var value = await session.ReadValueAsync(nodeId, cancellationToken).ConfigureAwait(false);
            var state = value.Value is int i && Enum.IsDefined(typeof(EquipmentState), i)
                ? (EquipmentState)i
                : EquipmentState.Idle;
            snapshots.Add(new EquipmentStateSnapshot(equipmentId, state, DateTimeOffset.UtcNow));
        }

        return snapshots;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_subscription is not null)
        {
            try
            {
                await _subscription.DeleteAsync(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete OPC-UA subscription on dispose.");
            }
        }

        if (_session is not null)
        {
            try
            {
                await _session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close OPC-UA session on dispose.");
            }
            _session.Dispose();
            _session = null;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null && _session.Connected)
        {
            return;
        }

        lock (_gate)
        {
            if (_session is not null)
            {
                _session.Dispose();
                _session = null;
            }
            if (_subscription is not null)
            {
                _subscription = null;
            }
        }

        var telemetry = DefaultTelemetry.Create(_ => { });
        var application = new ApplicationInstance(telemetry)
        {
            ApplicationName = "FactoryLine OPC-UA Adapter",
            ApplicationType = ApplicationType.Client
        };

        var pkiRoot = Path.Combine(Path.GetTempPath(), "factoryline-ua-client-pki");
        var certificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=FactoryLine OPC-UA Adapter, O=FactoryLine, DC=localhost",
            CertificateStoreType.Directory,
            pkiRoot);

        var configuration = await application
            .Build("urn:localhost:FactoryLineAdapter", "uri:factoryline:adapter")
            .SetMaxByteStringLength(4 * 1024 * 1024)
            .SetMaxArrayLength(1024 * 1024)
            .AsClient()
            .AddSecurityConfiguration(certificates, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(cancellationToken);

        await application.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false);

        var endpointDescription = new EndpointDescription
        {
            EndpointUrl = _endpointUrl,
            Server = new ApplicationDescription
            {
                ApplicationUri = "urn:localhost:FactoryLineServer",
                ApplicationName = "FactoryLine OPC-UA Server",
                ApplicationType = ApplicationType.Server
            },
            SecurityMode = MessageSecurityMode.None,
            SecurityPolicyUri = SecurityPolicies.None,
            UserIdentityTokens = new UserTokenPolicyCollection
            {
                new UserTokenPolicy { TokenType = UserTokenType.Anonymous }
            }
        };

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(configuration));

        var sessionFactory = new DefaultSessionFactory(telemetry);
        var session = await sessionFactory.CreateAsync(
            configuration,
            endpoint,
            false,
            false,
            configuration.ApplicationName,
            60_000,
            new UserIdentity(),
            null).ConfigureAwait(false);

        session.KeepAlive += OnKeepAlive;

        _session = session;
        _logger.LogInformation("OPC-UA adapter connected to {EndpointUrl}", _endpointUrl);

        await SubscribeAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ISession> GetSessionAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return _session!;
    }

    private async Task SubscribeAsync(ISession session, CancellationToken cancellationToken)
    {
        var nsIndex = (ushort)session.NamespaceUris.GetIndex(SimulatorUaServer.NamespaceUri);

        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = "FactoryLine equipment states",
            PublishingEnabled = true,
            PublishingInterval = 100,
            LifetimeCount = 1000,
            KeepAliveCount = 10
        };
        session.AddSubscription(subscription);

        foreach (var equipmentId in _equipmentIds)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                StartNodeId = new NodeId($"Equipment/{equipmentId}/State", nsIndex),
                AttributeId = Attributes.Value,
                DisplayName = equipmentId,
                SamplingInterval = 50,
                QueueSize = 10,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting
            };
            var capturedEquipmentId = equipmentId;
            item.Notification += (_, e) => OnNotification(capturedEquipmentId, e);
            subscription.AddItem(item);
        }

        await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
        await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

        _subscription = subscription;
    }

    private void OnNotification(string equipmentId, MonitoredItemNotificationEventArgs e)
    {
        if (e.NotificationValue is not MonitoredItemNotification notification || notification.Value is not DataValue value)
        {
            return;
        }

        if (value.Value is not int i || !Enum.IsDefined(typeof(EquipmentState), i))
        {
            return;
        }

        var state = (EquipmentState)i;
        var timestamp = value.SourceTimestamp.ToUniversalTime();

        lock (_gate)
        {
            if (!_isRunning)
            {
                return;
            }

            var index = _states.FindIndex(s => s.EquipmentId == equipmentId);
            if (index >= 0)
            {
                _states[index] = new EquipmentStateSnapshot(equipmentId, state, timestamp);
            }
            else
            {
                _states.Add(new EquipmentStateSnapshot(equipmentId, state, timestamp));
            }
        }

        StateChanged?.Invoke(this, new EquipmentStateChange(equipmentId, state, timestamp));
    }

    private async Task TearDownSubscriptionAsync(CancellationToken cancellationToken)
    {
        Subscription? subscription;
        lock (_gate)
        {
            subscription = _subscription;
            _subscription = null;
        }

        if (subscription is null)
        {
            return;
        }

        try
        {
            await subscription.DeleteAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete OPC-UA subscription on stop.");
        }
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        _logger.LogDebug("OPC-UA adapter keep-alive: {Status}", e.Status);
    }
}
