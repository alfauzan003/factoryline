using System.Net;
using System.Net.Sockets;
using FactoryLine.Domain;

namespace FactoryLine.Workers;

/// <summary>
/// Hosts the embedded OPC-UA server for the lifetime of the app. The endpoint
/// URL is only known after the server binds, so the equipment source is
/// resolved lazily once the server is listening.
/// </summary>
public sealed class SimulatorUaServerHostedService : IHostedService
{
    private readonly SimulatorUaServer _server;
    private readonly ILogger<SimulatorUaServerHostedService> _logger;

    public SimulatorUaServerHostedService(SimulatorUaServer server, ILogger<SimulatorUaServerHostedService> logger)
    {
        _server = server;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        await _server.StartAsync(port, cancellationToken);
        _logger.LogInformation("Embedded OPC-UA server started at {EndpointUrl}", _server.EndpointUrl);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _server.StopAsync(cancellationToken);

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
