# CoreSync

**CoreSync** is a .NET library for bidirectional data synchronization between databases. It supports SQLite, SQL Server, and PostgreSQL, letting you keep multiple database instances in sync — whether they're on the same machine or communicating over HTTP.

[![Build status](https://ci.appveyor.com/api/projects/status/8cloij4060cbnvfp?svg=true)](https://ci.appveyor.com/project/adospace/coresync)
[![NuGet](https://img.shields.io/nuget/v/CoreSync.svg)](https://www.nuget.org/packages/CoreSync/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Key Features

- **Bidirectional sync** between any supported database combination (e.g. SQLite ↔ SQL Server, PostgreSQL ↔ SQLite, etc.)
- **Automatic change tracking** — detects inserts, updates, and deletes with version-based anchors
- **Conflict resolution** — built-in strategies (Skip or ForceWrite) with per-item customization
- **Filtered sync** — synchronize a subset of data using parameterized queries
- **HTTP transport** — sync over the network with ASP.NET Core server endpoints and a resilient HTTP client (with Polly retries and MessagePack binary format)
- **Multiple providers**: SQLite, SQL Server (custom change tracking), SQL Server CT (native Change Tracking), PostgreSQL

## How It Works

CoreSync uses a **version-based change tracking** approach:

1. Each database gets a unique **Store ID** (GUID)
2. When you call `ApplyProvisionAsync()`, CoreSync creates internal tracking tables that monitor inserts, updates, and deletes
3. **SyncAgent** orchestrates the sync: it pulls changes from one provider, applies them to the other, and vice versa
4. Each side saves the last-known version of the other store, so the next sync only transfers what changed

```
┌──────────────┐                        ┌──────────────┐
│  Local DB    │                        │  Remote DB   │
│  (SQLite)    │◄──── SyncAgent ──────► │ (SQL Server) │
│              │   1. Upload changes    │              │
│              │   2. Download changes  │              │
│              │   3. Save anchors      │              │
└──────────────┘                        └──────────────┘
```

## Supported Database Providers

| Package | Database | Change Tracking |
|---|---|---|
| `CoreSync.Sqlite` | SQLite | Custom trigger-based |
| `CoreSync.SqlServer` | SQL Server | Custom trigger-based |
| `CoreSync.SqlServerCT` | SQL Server | Native Change Tracking |
| `CoreSync.PostgreSQL` | PostgreSQL | Custom trigger-based |

**HTTP packages** for remote sync over the network:

| Package | Description |
|---|---|
| `CoreSync.Http.Server` | ASP.NET Core endpoints for hosting a sync server |
| `CoreSync.Http.Client` | HTTP client with resilience (Polly) and binary format (MessagePack) |

## Getting Started

### Installation

Install the NuGet packages for your scenario. For example, to sync between SQLite (local) and SQL Server (remote):

```bash
dotnet add package CoreSync.Sqlite
dotnet add package CoreSync.SqlServer
```

### Basic Example: SQLite ↔ SQL Server Sync

```csharp
using CoreSync.Sqlite;
using CoreSync.SqlServer;

// 1. Configure the local SQLite provider
var localConfig = new SqliteSyncConfigurationBuilder("Data Source=local.db")
    .Table("Users")
    .Table("Posts")
    .Table("Comments")
    .Build();

var localProvider = new SqliteSyncProvider(localConfig);

// 2. Configure the remote SQL Server provider
var remoteConfig = new SqlSyncConfigurationBuilder("Server=.;Database=MyApp;Trusted_Connection=true")
    .Table("Users")
    .Table("Posts")
    .Table("Comments")
    .Build();

var remoteProvider = new SqlSyncProvider(remoteConfig);

// 3. Apply provision (creates change tracking infrastructure)
await localProvider.ApplyProvisionAsync();
await remoteProvider.ApplyProvisionAsync();

// 4. Synchronize
var syncAgent = new SyncAgent(localProvider, remoteProvider);
await syncAgent.SynchronizeAsync();
```

That's it — all changes made on either side will be transferred to the other.

### PostgreSQL startup provisioning on a caller-owned session

When startup uses a session-level PostgreSQL advisory lock, call the PostgreSQL-specific
overload on the **same open `NpgsqlConnection`** that acquired the lock. The
`ISyncProvider.ApplyProvisionAsync(CancellationToken)` method is unchanged and
continues to open its own connections; it does not provide this guarantee.

```csharp
using CoreSync.PostgreSQL;
using Npgsql;

var provider = new PostgreSQLSyncProvider(
    new PostgreSQLSyncConfigurationBuilder(connectionString).Table("items").Build());

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync(cancellationToken);
await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
{
    command.Parameters.Add(new NpgsqlParameter { Value = 41024L });
    await command.ExecuteNonQueryAsync(cancellationToken);
}

await provider.ApplyProvisionAsync(connection, cancellationToken);
// Keep the connection and its lock open until startup provisioning has finished.
```

The overload never opens, closes, replaces, or disposes the supplied connection.
It runs initialization and all trigger DDL on its backend; losing that session
aborts the remaining writes rather than retrying on a different backend. Do not
close/reopen or use the connection concurrently while provisioning. Without an
active transaction, statements commit individually and a failed attempt can
leave partial provisioning. If a transaction is already active on the connection,
pass it explicitly: Npgsql otherwise enlists the commands in it anyway. To
make provisioning atomic, begin a transaction on the
locked connection and call
`ApplyProvisionAsync(connection, transaction, cancellationToken)`; the caller
must commit or roll back and keep the lock until that decision completes. The
provider does not manage the caller's transaction. Both variants require a
session-affine connection to PostgreSQL; transaction/statement-pooling proxies
cannot preserve a session-level advisory lock across statements. Other provider
operations still use the configured connection string.

The PostgreSQL session tests require a disposable database with permission to
create event triggers. Set `CORE-SYNC_POSTGRESQL_ISOLATED_CONNECTION_STRING`
before running:

```bash
dotnet test src/CoreSync.Tests/CoreSync.Tests.csproj --filter 'FullyQualifiedName~PostgreSQLProvisioningSessionTests'
```

Tests create unique schemas and leave their audit objects in that isolated
database for inspection.

#### Local PostgreSQL provider package

Ordinary source builds and tests use the local CoreSync project. The unpublished
`CoreSync.PostgreSQL` `0.1.129-local3` package instead compiles against and
depends **exactly** on stable `CoreSync` `0.1.129`; it never requires a local
CoreSync package. Pack only after committing the source, from a clean checkout:

```bash
dotnet restore src/CoreSync.PostgreSQL/CoreSync.PostgreSQL.csproj -p:PackWithStableCoreSync=true
dotnet pack src/CoreSync.PostgreSQL/CoreSync.PostgreSQL.csproj -c Release --no-restore -p:PackWithStableCoreSync=true --output src/CoreSync.PostgreSQL/bin/Release/local3
```

Restore again without `PackWithStableCoreSync` before returning to ordinary
source builds. The local3 DLL uses assembly/file version `0.1.129.3`; its
informational version and the package's fork repository metadata identify the
committed source revision.

### Conflict Resolution

When both sides modify the same record, you control what happens:

```csharp
await syncAgent.SynchronizeAsync(
    conflictResolutionOnRemoteStore: ConflictResolution.Skip,       // ignore conflicts on remote
    conflictResolutionOnLocalStore: ConflictResolution.ForceWrite   // overwrite local with remote
);
```

Or use a per-item delegate for fine-grained control:

```csharp
await syncAgent.SynchronizeAsync(
    conflictResolutionOnRemoteFunc: (item) =>
    {
        if (item.TableName == "Posts")
            return ConflictResolution.ForceWrite;
        return ConflictResolution.Skip;
    }
);
```

### Filtered Sync

Synchronize only a subset of data using filter parameters:

```csharp
var remoteConfig = new SqlSyncConfigurationBuilder(connectionString)
    .Table("Users",
        selectIncrementalQuery: "SELECT * FROM Users WHERE Email = @userId")
    .Table("Posts",
        selectIncrementalQuery: "SELECT p.* FROM Posts p JOIN Users u ON p.Author = u.Email WHERE u.Email = @userId")
    .Build();

await syncAgent.SynchronizeAsync(
    remoteSyncFilterParameters: [new SyncFilterParameter("@userId", "user@test.com")]
);
```

### Sync Over HTTP

**Server** (ASP.NET Core):

```csharp
// Program.cs
builder.Services.AddSingleton<ISyncProvider>(sp =>
{
    var config = new SqlSyncConfigurationBuilder(connectionString)
        .Table("Users")
        .Table("Posts")
        .Build();
    return new SqlSyncProvider(config);
});

var app = builder.Build();
app.UseCoreSyncHttpServer("api/sync-agent");
app.Run();
```

**Client**:

```csharp
var httpClient = new SyncProviderHttpClient(options =>
{
    options.HttpClientName = "SyncClient";
    options.SyncControllerRoute = "api/sync-agent";
    options.UseBinaryFormat = true;  // MessagePack for efficiency
    options.BulkItemSize = 100;      // pagination size
});

var localProvider = new SqliteSyncProvider(localConfig);
var syncAgent = new SyncAgent(localProvider, httpClient);
await syncAgent.SynchronizeAsync();
```

### Provider Modes

Providers can operate in different modes depending on their role:

```csharp
// Server-side: only sends data
var remoteProvider = new SqlSyncProvider(config, ProviderMode.Remote);

// Client-side: only receives data
var localProvider = new SqliteSyncProvider(config, ProviderMode.Local);

// Default: bidirectional
var provider = new SqlSyncProvider(config, ProviderMode.Bidirectional);
```

### SQL Server Native Change Tracking

If you prefer SQL Server's built-in Change Tracking over custom triggers:

```csharp
var config = new SqlServerCTSyncConfigurationBuilder(connectionString)
    .ChangeRetentionDays(7)    // how long change history is kept
    .AutoCleanup(true)
    .Table("Users")
    .Table("Posts")
    .Build();

var provider = new SqlServerCTProvider(config);
await provider.ApplyProvisionAsync();
```

### Managing Change Tracking

```csharp
// Disable tracking for a specific table
await provider.DisableChangeTrackingForTable("Users");

// Re-enable it
await provider.EnableChangeTrackingForTable("Users");

// Clean up old tracking records
var syncVersion = await provider.ApplyRetentionPolicyAsync(minVersion: 4);

// Remove all provisioning (drop tracking tables)
await provider.RemoveProvisionAsync();
```

## Architecture

```
CoreSync (core interfaces and SyncAgent)
├── CoreSync.Sqlite           (SQLite provider)
├── CoreSync.SqlServer         (SQL Server provider - custom CT)
├── CoreSync.SqlServerCT       (SQL Server provider - native CT)
├── CoreSync.PostgreSQL        (PostgreSQL provider)
├── CoreSync.Http              (shared HTTP types)
├── CoreSync.Http.Server       (ASP.NET Core sync endpoints)
└── CoreSync.Http.Client       (resilient HTTP sync client)
```

### Core Abstractions

| Type | Description |
|---|---|
| `ISyncProvider` | Interface for database sync providers — handles provisioning, change detection, and change application |
| `SyncAgent` | Orchestrates bidirectional sync between two providers |
| `SyncChangeSet` | A set of changes (inserts/updates/deletes) with source and target anchors |
| `SyncItem` | A single record change with table name, change type, and column values |
| `SyncAnchor` | Version marker for tracking sync progress between stores |
| `SyncConfiguration` | Base class for provider-specific configuration (tables, connection strings) |

### Sync Flow

```
SyncAgent.SynchronizeAsync()
│
├── 1. Local → Remote
│   ├── localProvider.GetChangesAsync(remoteStoreId, UploadOnly)
│   ├── remoteProvider.ApplyChangesAsync(localChanges, conflictFunc)
│   └── remoteProvider.SaveVersionForStoreAsync(localStoreId, version)
│
└── 2. Remote → Local
    ├── remoteProvider.GetChangesAsync(localStoreId, DownloadOnly)
    ├── localProvider.ApplyChangesAsync(remoteChanges, conflictFunc)
    └── localProvider.SaveVersionForStoreAsync(remoteStoreId, version)
```

## Target Frameworks

- **Core library and providers**: .NET Standard 2.0 (compatible with .NET Framework 4.6.1+ and .NET Core 2.0+)
- **HTTP packages**: .NET 8.0

## Sample App

A full sample application using .NET MAUI with MauiReactor is available at:
https://github.com/adospace/mauireactor-core-sync

## Resources

- [Data Synchronization Primer](https://codeburst.io/data-synchronization-primer-88ad04e1747b) — introductory article explaining the concepts behind CoreSync

## License

[MIT](LICENSE) — Copyright (c) 2018 adospace
