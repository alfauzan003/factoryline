# Fallback hosting: FactoryLine after the paid VPS year ends

This repo currently runs on a paid VPS. When that year ends, everything in this
repo stays portable — the app is a plain .NET 8 container that talks to SQL
Server over a connection string. The cheapest reliable fallback is **Azure App
Service (free F1 tier) + Azure SQL (free offer)**. This document is the
practical migration path, in plain English.

## What you are migrating

| Piece          | Today (VPS)                        | Fallback                          |
| -------------- | ---------------------------------- | --------------------------------- |
| App            | Docker container / `dotnet run`    | Azure App Service (F1, free)      |
| Database       | SQL Server in `docker-compose.yml` | Azure SQL Database (free offer)   |
| HTTPS + port   | reverse proxy / whatever you set   | handled by App Service            |

Nothing in the application code needs to change. The app already tolerates a
database that is down at boot (it logs a warning and runs the dashboard without
persistence), and the `Dockerfile` in the repo root already listens on port 80.

## Step 1 — create the database (Azure SQL free offer)

1. In the Azure portal: **Create a resource → SQL Database**. Pick the **free
   offer** (serverless tier, 100k vCore-seconds/month, auto-pause enabled).
2. Choose a server (e.g. `factoryline-sql`, region near you), and note the
   **admin user + password** you create.
3. Set **Auto-pause delay** to something short (e.g. 15–30 min) so idle time
   costs nothing.
4. In **Networking**, allow Azure services to access the server, or add the
   App Service outbound IPs as firewall rules.

Copy the connection string from **Connection strings** in the portal — it looks
like:

```
Server=tcp:factoryline-sql.database.windows.net,1433;Initial Catalog=FactoryLine;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Default
```

Replace the `Authentication=...` part with `User ID=<admin>;Password=<password>`
and append `;TrustServerCertificate=True` if you connect from local tools. The
app creates its schema on first boot (`EnsureCreated`), so there is nothing to
run manually.

## Step 2 — deploy the app (Azure App Service F1)

Two equivalent routes:

### Route A — deploy the container (easiest)

```bash
az webapp create \
  --resource-group <rg> \
  --plan <free-plan> \
  --name factoryline-app \
  --container-image <your-registry>/factoryline:latest
```

Push the image to any registry (Docker Hub, GHCR, ACR) first. App Service pulls
it and runs it; the Dockerfile's `ASPNETCORE_URLS=http://+:80` and `EXPOSE 80`
match what App Service expects.

### Route B — deploy the published files

Publish locally and zip-deploy:

```bash
dotnet publish src/FactoryLine -c Release -o publish
az webapp deploy --resource-group <rg> --name factoryline-app --src-path publish
```

Same result, no registry needed. (With Route B the container runtime of App
Service still serves it — .NET 8 is a supported runtime.)

## Step 3 — point the app at Azure SQL

App Service reads configuration from **Environment variables** in the portal
(Settings → Configuration). Set:

| Name                                  | Value                              |
| ------------------------------------- | ---------------------------------- |
| `ConnectionStrings__FactoryLineDb`    | the Azure SQL connection string    |
| `Equipment__Source`                   | `Simulator` (keep, or `OpcUa`)     |

The double underscore (`__`) is the standard .NET way to express a nested
config key (`ConnectionStrings:FactoryLineDb`) in environment variables — the
app reads it with zero code changes.

## Step 4 — verify and understand the cold start

- Open `https://factoryline-app.azurewebsites.net` and press **Start line**.
- The dashboard works exactly like local: live equipment states, work order
  form, lifecycle table.
- **Expected:** the first request after the database auto-paused takes
  **~30–60 seconds** (serverless cold start). Subsequent requests are normal
  speed. This is a property of the free offer, not a bug.

## Free-tier limits you should know about (F1)

- **60 CPU minutes per day** of web app compute.
- Storage, bandwidth and custom-domain restrictions apply.
- No custom SSL certificate on F1 for custom domains — the default
  `*.azurewebsites.net` HTTPS works fine.
- The SQL free offer auto-pauses when idle; Azure SQL shared capacity has
  its own quotas.

For a demo/portfolio this is plenty. If a recruiter hits a cold start, the
page just takes a moment on the very first load.

## Containers stay portable

Nothing here locks you into Azure:

- The `Dockerfile` builds a stock `mcr.microsoft.com/dotnet/aspnet:8.0` image —
  run it on any Docker host, any cloud, or a new VPS.
- All configuration is environment variables / connection strings.
- If the free Azure year conditions change, the same image moves to Render,
  Fly.io, Railway, or another VPS in minutes.

## Rollback / if the free tier is not enough

The cheapest upgrade path is keeping the same architecture:

1. App Service **Basic/Standard** tier (always-on, custom domains, HTTPS).
2. Azure SQL **serverless** with auto-pause turned off (still pay-per-use).
3. Or: back to a small VPS with Docker — `docker compose up -d` from this repo
   is the entire local equivalent, and the compose file already exists.

## Checklist

- [ ] Azure SQL created (free offer), connection string copied
- [ ] App Service F1 created, image or publish files deployed
- [ ] `ConnectionStrings__FactoryLineDb` env var set
- [ ] Dashboard loads, Start line works, work orders persist
- [ ] First-request cold start (~30–60 s) confirmed as expected
