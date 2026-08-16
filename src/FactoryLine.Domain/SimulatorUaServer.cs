using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace FactoryLine.Domain;

/// <summary>
/// An embedded OPC-UA server whose address space mirrors the
/// <see cref="LineSimulator"/>. Each equipment is an object exposing a
/// <c>State</c> Int32 variable, and a <c>Simulator</c> object exposes
/// <c>Start</c>/<c>Stop</c> methods and an <c>IsRunning</c> variable. When the
/// simulator's state machine advances, the node values are pushed so UA
/// subscriptions receive data changes.
/// </summary>
public sealed class SimulatorUaServer : IAsyncDisposable
{
    public const string NamespaceUri = "urn:factoryline:simulator";

    private readonly ILogger<SimulatorUaServer> _logger;
    private readonly LineSimulator _simulator;
    private DemoServer? _server;
    private string? _endpointUrl;

    public SimulatorUaServer(IOptions<LineSimulatorOptions> options, ILogger<LineSimulator> simulatorLogger, ILogger<SimulatorUaServer> logger)
    {
        _logger = logger;
        _simulator = new LineSimulator(options, simulatorLogger);
    }

    public string EndpointUrl => _endpointUrl ?? throw new InvalidOperationException("Server has not been started.");

    public IReadOnlyList<string> EquipmentIds => _simulator.GetCurrentStatesAsync().GetAwaiter().GetResult()
        .Select(s => s.EquipmentId)
        .ToList();

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        _endpointUrl = $"opc.tcp://localhost:{port}/FactoryLine";

        var telemetry = DefaultTelemetry.Create(_ => { });
        var application = new ApplicationInstance(telemetry)
        {
            ApplicationName = "FactoryLine OPC-UA Server",
            ApplicationType = ApplicationType.Server
        };

        var pkiRoot = Path.Combine(Path.GetTempPath(), "factoryline-ua-server-pki");
        var certificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=FactoryLine OPC-UA Server, O=FactoryLine, DC=localhost",
            CertificateStoreType.Directory,
            pkiRoot);

        var configuration = await application
            .Build("urn:localhost:FactoryLineServer", "uri:factoryline:server")
            .SetMaxByteStringLength(4 * 1024 * 1024)
            .SetMaxArrayLength(1024 * 1024)
            .AsServer([_endpointUrl])
            .AddUnsecurePolicyNone()
            .AddSecurityConfiguration(certificates, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(cancellationToken);

        await application.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false);

        _server = new DemoServer(_simulator, _logger);
        await application.StartAsync(_server).ConfigureAwait(false);

        _logger.LogInformation("OPC-UA server listening on {EndpointUrl}", _endpointUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            await _server.StopAsync(cancellationToken).ConfigureAwait(false);
            _server.Dispose();
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _simulator.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DemoServer : StandardServer
    {
        private readonly LineSimulator _simulator;
        private readonly ILogger<SimulatorUaServer> _logger;
        private SimulatorNodeManager? _nodeManager;

        public DemoServer(LineSimulator simulator, ILogger<SimulatorUaServer> logger)
        {
            _simulator = simulator;
            _logger = logger;
        }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration)
        {
            _nodeManager = new SimulatorNodeManager(server, configuration, _simulator, _logger);
            return new MasterNodeManager(server, configuration, null, _nodeManager);
        }

        protected override ServerProperties LoadServerProperties()
        {
            return new ServerProperties
            {
                ManufacturerName = "FactoryLine",
                ProductName = "FactoryLine OPC-UA Server",
                ProductUri = "uri:factoryline:server",
                SoftwareVersion = "1.0",
                BuildNumber = "1",
                BuildDate = DateTime.UtcNow
            };
        }
    }

    private sealed class SimulatorNodeManager : CustomNodeManager2
    {
        private const string EquipmentPath = "Equipment";

        private readonly LineSimulator _simulator;
        private readonly Dictionary<string, BaseDataVariableState> _stateVariables = new();
        private BaseDataVariableState? _isRunningVariable;

        public SimulatorNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration,
            LineSimulator simulator,
            ILogger<SimulatorUaServer> logger)
            : base(server, configuration, server.Telemetry.CreateLogger<SimulatorNodeManager>(), NamespaceUri)
        {
            _simulator = simulator;
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                var folder = new FolderState(null)
                {
                    SymbolicName = "Equipment",
                    ReferenceTypeId = ReferenceTypes.Organizes,
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    NodeId = new NodeId(EquipmentPath, NamespaceIndex),
                    BrowseName = new QualifiedName(EquipmentPath, NamespaceIndex),
                    DisplayName = new LocalizedText("en", "Equipment"),
                    WriteMask = AttributeWriteMask.None,
                    UserWriteMask = AttributeWriteMask.None,
                    EventNotifier = EventNotifiers.None
                };
                folder.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, folder.NodeId));
                AddRootNotifier(folder);

                var initialStates = _simulator.GetCurrentStatesAsync().GetAwaiter().GetResult();
                foreach (var snapshot in initialStates)
                {
                    var equipment = CreateEquipmentObject(snapshot);
                    folder.AddChild(equipment);
                }

                var simulatorObject = CreateSimulatorObject();
                folder.AddChild(simulatorObject);

                AddPredefinedNode(SystemContext, folder);

                _simulator.StateChanged += OnSimulatorStateChanged;
                OnSimulatorStateChanged(_simulator, new EquipmentStateChange("EQ-01", EquipmentState.Idle, DateTimeOffset.UtcNow));
            }
        }

        private BaseObjectState CreateEquipmentObject(EquipmentStateSnapshot snapshot)
        {
            var equipment = new BaseObjectState(null)
            {
                SymbolicName = snapshot.EquipmentId,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                NodeId = new NodeId($"Equipment/{snapshot.EquipmentId}", NamespaceIndex),
                BrowseName = new QualifiedName(snapshot.EquipmentId, NamespaceIndex),
                DisplayName = new LocalizedText("en", snapshot.EquipmentId),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };

            var state = new BaseDataVariableState(equipment)
            {
                SymbolicName = "State",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId($"Equipment/{snapshot.EquipmentId}/State", NamespaceIndex),
                BrowseName = new QualifiedName("State", NamespaceIndex),
                DisplayName = new LocalizedText("en", "State"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                DataType = DataTypeIds.Int32,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Historizing = false
            };
            state.Value = (int)snapshot.State;
            state.StatusCode = StatusCodes.Good;
            equipment.AddChild(state);
            _stateVariables[snapshot.EquipmentId] = state;

            return equipment;
        }

        private BaseObjectState CreateSimulatorObject()
        {
            var simulatorObject = new BaseObjectState(null)
            {
                SymbolicName = "Simulator",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                NodeId = new NodeId("Simulator", NamespaceIndex),
                BrowseName = new QualifiedName("Simulator", NamespaceIndex),
                DisplayName = new LocalizedText("en", "Simulator"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };

            var isRunning = new BaseDataVariableState(simulatorObject)
            {
                SymbolicName = "IsRunning",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId("Simulator/IsRunning", NamespaceIndex),
                BrowseName = new QualifiedName("IsRunning", NamespaceIndex),
                DisplayName = new LocalizedText("en", "IsRunning"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                DataType = DataTypeIds.Boolean,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Historizing = false
            };
            isRunning.Value = _simulator.IsRunning;
            isRunning.StatusCode = StatusCodes.Good;
            simulatorObject.AddChild(isRunning);
            _isRunningVariable = isRunning;

            var start = new MethodState(simulatorObject)
            {
                SymbolicName = "Start",
                ReferenceTypeId = ReferenceTypes.HasComponent,
                NodeId = new NodeId("Simulator/Start", NamespaceIndex),
                BrowseName = new QualifiedName("Start", NamespaceIndex),
                DisplayName = new LocalizedText("en", "Start"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };
            start.OnCallMethod = new GenericMethodCalledEventHandler(OnStartCall);
            simulatorObject.AddChild(start);

            var stop = new MethodState(simulatorObject)
            {
                SymbolicName = "Stop",
                ReferenceTypeId = ReferenceTypes.HasComponent,
                NodeId = new NodeId("Simulator/Stop", NamespaceIndex),
                BrowseName = new QualifiedName("Stop", NamespaceIndex),
                DisplayName = new LocalizedText("en", "Stop"),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };
            stop.OnCallMethod = new GenericMethodCalledEventHandler(OnStopCall);
            simulatorObject.AddChild(stop);

            return simulatorObject;
        }

        private ServiceResult OnStartCall(
            ISystemContext context,
            MethodState method,
            IList<object> inputArguments,
            IList<object> outputArguments)
        {
            _simulator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            PushIsRunning();
            return ServiceResult.Good;
        }

        private ServiceResult OnStopCall(
            ISystemContext context,
            MethodState method,
            IList<object> inputArguments,
            IList<object> outputArguments)
        {
            _simulator.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            PushIsRunning();
            return ServiceResult.Good;
        }

        private void OnSimulatorStateChanged(object? sender, EquipmentStateChange change)
        {
            lock (Lock)
            {
                if (_stateVariables.TryGetValue(change.EquipmentId, out var variable))
                {
                    variable.Value = (int)change.State;
                    variable.Timestamp = DateTime.UtcNow;
                    variable.ClearChangeMasks(SystemContext, false);
                }
                PushIsRunning();
            }
        }

        private void PushIsRunning()
        {
            if (_isRunningVariable is not null)
            {
                _isRunningVariable.Value = _simulator.IsRunning;
                _isRunningVariable.Timestamp = DateTime.UtcNow;
                _isRunningVariable.ClearChangeMasks(SystemContext, false);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _simulator.StateChanged -= OnSimulatorStateChanged;
            }
            base.Dispose(disposing);
        }
    }
}
