# FactoryLine

Production line simulator → OPC-UA → equipment bridge → mini-MES with a live dashboard.
Backend engineering story from the manufacturing world, in C# / .NET 8.

## Tech stack

- C# / .NET 8
- ASP.NET Core + Blazor Server (real-time dashboard over SignalR)
- SQL Server (EF Core)
- OPC-UA (OPC Foundation stack, planned adapter)
- xUnit + WebApplicationFactory (integration tests)

## Architecture

```
+-------------------+      +----------------------+      +----------------+
|  EquipmentSource  | ---> |  equipment bridge    | ---> |  SQL Server    |
|  (LineSimulator)  |      |  subscribe→normalize→|      |  (EF Core)     |
|  per-equipment    |      |  persist             |      +----------------+
|  state machines   |      +----------+-----------+             |
+-------------------+                 |                         |
                                     | broadcast                 |
                                     v                         |
                              +--------------+                  |
                              |  SignalR hub  | <---------------+
                              |  /equipmenthub|
                              +------+-------+
                                     |
                              +------v-------+
                              |  Blazor Server|
                              |  dashboard    |
                              +--------------+
```

The `EquipmentSource` seam (`src/FactoryLine.Domain/IEquipmentSource.cs`) is the one
interface the bridge and dashboard depend on. The line simulator is the demo driver;
an OPC-UA adapter will be a second implementation of the same contract.

## How to run

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and
[Docker](https://www.docker.com/) (for the local SQL Server).

```bash
# 1. Start the local SQL Server dev database
docker compose up -d

# 2. Run the app (dashboard at http://localhost:5000, or the port printed on startup)
dotnet run --project src/FactoryLine
```

Open the dashboard and press **Start line**. Equipment states (Idle → Running →
Completed → Idle) advance on a visible schedule and appear live without a page
refresh. If SQL Server isn't running, the dashboard still shows live states — only
persistence is skipped (a warning is logged at startup).

### Configuration

Settings live in `src/FactoryLine/appsettings.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings:FactoryLineDb` | localhost:1433 | SQL Server connection |
| `LineSimulator:EquipmentCount` | `3` | number of simulated equipment |
| `LineSimulator:TickMilliseconds` | `5000` | seconds between state transitions |

## Tests

```bash
dotnet test FactoryLine.sln
```

Integration tests drive the simulator fixture through the real app host
(WebApplicationFactory) and assert that state changes are persisted to the database
and broadcast over the SignalR hub — external behavior only.

## CI

[![CI](https://github.com/alfauzan003/factoryline/actions/workflows/ci.yml/badge.svg)](https://github.com/alfauzan003/factoryline/actions/workflows/ci.yml)
