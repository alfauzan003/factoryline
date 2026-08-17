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

### Full picture (mini-MES + material flow)

```
                         INBOUND MATERIAL FLOW (LogiFlow)
              POST /api/arrivals  {movementId, destinationPoint, materialId, arrivedAt}
                                        |
                                        v
+------------------------------------------------------------------------------+
|                           FactoryLine (ASP.NET Core)                          |
|                                                                              |
|  +------------------+      +----------------------+      +----------------+  |
|  |  IEquipmentSource|      |  EquipmentBridge-    | ---> |  SQL Server    |  |
|  |  (seam)          | ---> |  Worker              |      |  (EF Core)     |  |
|  +--------+---------+      |  subscribe ->        |      |  WorkOrders,   |  |
|           |                |  normalize ->        |      |  movements     |  |
|  +--------+---------+      |  persist             |      +-------+--------+  |
|  |  LineSimulator   |      +----------+-----------+              |           |
|  |  (demo driver)   |                 |                          |           |
|  +--------+---------+                 | broadcast                |           |
|           |                           v                          |           |
|  +--------+---------+      +----------------------+              |           |
|  |  OpcUaEquipment- |      |  SignalR hub         | <------------+           |
|  |  Source (OPC-UA  |      |  /equipmenthub       |   (state + WO history)   |
|  |  adapter path)   |      +----------+-----------+                          |
|  +------------------+                 |                                     |
|                                       v                                     |
|                        +----------------------+                             |
|                        |  Blazor Server       |                             |
|                        |  dashboard           |                             |
|                        +----------------------+                             |
|                                                                             |
|  mini-MES: WorkOrderMonitorWorker -> MiniMesService -> EquipmentGate         |
|  (creates WOs, holds equipment until the material arrives,                   |
|   completes WOs and emits the next movement request)                         |
+------------------------------------------------------------------------------+
                                        |
                                        v
                         OUTBOUND MATERIAL FLOW (LogiFlow)
              GET /api/movements/pending  ->  next movement request (to DISPATCH)
```

The bridge, the dashboard and the mini-MES all talk through the `IEquipmentSource`
seam — the line simulator is the demo driver, and the OPC-UA adapter
(`OpcUaEquipmentSource`, optionally backed by the embedded `SimulatorUaServer`) is
a second implementation of the same contract, switched on with
`Equipment:Source=OpcUa`.

The mini-MES runs as a background worker (`WorkOrderMonitorWorker`) on top of
`MiniMesService` and the in-memory `EquipmentGate`: work orders are created in
`WAIT_MATERIAL`, the equipment is held by the gate, and an inbound arrival
callback (`POST /api/arrivals`) from LogiFlow releases the gate and auto-starts
the work order. When the equipment completes, the work order moves to
`COMPLETED` and a next-movement request is emitted, which LogiFlow picks up via
`GET /api/movements/pending`.

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

## Demo script (recruiter walkthrough)

The whole flow takes about a minute, all local.

1. **Start SQL Server**
   ```bash
   docker compose up -d
   ```

2. **Run the app**
   ```bash
   dotnet run --project src/FactoryLine
   ```
   Dashboard: http://localhost:5051

3. **Start the line** — press **Start line** on the dashboard. Equipment states
   (Idle → Running → Completed → Idle) advance live, no page refresh needed.

4. **Create a work order** — use the dashboard form: a product code
   (e.g. `WIDGET-42`), a required material (e.g. `MAT-001`) and an equipment
   (e.g. `EQ-01`). The work order appears in the lifecycle table as
   `WAIT_MATERIAL` and the equipment is held by the gate.

5. **Release the gate** — simulate the LogiFlow arrival callback for that
   material and equipment:
   ```bash
   curl -X POST http://localhost:5051/api/arrivals \
     -H "Content-Type: application/json" \
     -d '{"movementId":"a1b2c3d4-1111-2222-3333-444455556666","destinationPoint":"EQ-01","materialId":"MAT-001","arrivedAt":"2026-08-16T09:30:00Z"}'
   ```
   `destinationPoint` must match the work order's equipment and `materialId` its
   required material — the oldest waiting work order with that pair is released.
   Re-sending the same `movementId` is idempotent (a duplicate is ignored).

6. **Watch it run** — the equipment auto-starts (work order → `RUN`), and when
   it completes the work order moves to `COMPLETED`.

7. **Next movement request** — the completed work order emits the next movement
   (e.g. to `DISPATCH`):
   ```bash
   curl http://localhost:5051/api/movements/pending
   ```
   The pending request carries its own `movementId`, ready for LogiFlow to pick
   up.

## Deployment notes

### Azure SQL cold-start

When the app runs against **Azure SQL** on the free offer, the database is
serverless and **auto-pauses after idle**. The first request after a pause can
take **~30–60 seconds** while the instance cold-starts — the dashboard may show
no persisted data or slow queries for that first call. This is expected, not a
bug; subsequent requests return to normal latency.

The app itself is container-friendly: the `Dockerfile` in the repo root builds a
self-contained .NET 8 image, and `FALLBACK_HOSTING.md` describes the migration
path when the paid VPS year ends.

## Tests

```bash
dotnet test FactoryLine.sln
```

Integration tests drive the simulator fixture through the real app host
(WebApplicationFactory) and assert that state changes are persisted to the database
and broadcast over the SignalR hub — external behavior only.

## CI

[![CI](https://github.com/alfauzan003/factoryline/actions/workflows/ci.yml/badge.svg)](https://github.com/alfauzan003/factoryline/actions/workflows/ci.yml)
