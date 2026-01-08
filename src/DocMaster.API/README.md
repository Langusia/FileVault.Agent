# DocMaster.API

DocMaster.API is the cluster management and metadata service for the FileVault distributed storage system. It manages storage nodes, tracks their health status, and provides APIs for cluster operations.

## Features

- **Node Registration & Management**: Register and manage storage nodes in the cluster
- **Health Monitoring**: Automatic background polling of node health via gRPC
- **Metrics Tracking**: Track storage capacity, CPU usage, and other node metrics
- **RESTful API**: Clean REST API for cluster management operations
- **Auto-Migration**: Database schema automatically created/updated on startup

## Prerequisites

- .NET 8.0 SDK or later
- PostgreSQL 12+ (recommended) or SQLite for local development

## Configuration

### Database Connection

Configure the database connection in `appsettings.json` or `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DocMasterDb": "Host=localhost;Port=5432;Database=docmaster;Username=postgres;Password=postgres"
  }
}
```

### Health Polling Settings

Configure health polling behavior:

```json
{
  "NodeHealthPolling": {
    "IntervalSeconds": 10,
    "MaxConsecutiveFailures": 3
  }
}
```

- `IntervalSeconds`: How often to poll nodes for health (default: 10 seconds)
- `MaxConsecutiveFailures`: Number of failures before marking node offline (default: 3)

## Running the Application

### Using .NET CLI

```bash
cd src/DocMaster.API
dotnet run
```

The API will be available at:
- HTTP: http://localhost:5000
- HTTPS: https://localhost:5001
- Swagger UI: http://localhost:5000 (Development mode only)

### Using Visual Studio / Rider

1. Open `FileVault.Agent.sln`
2. Set `DocMaster.API` as the startup project
3. Press F5 to run

## API Endpoints

### Node Management

#### Register/Update Node
```http
POST /api/nodes
Content-Type: application/json

{
  "nodeId": "node-1",
  "grpcAddress": "http://10.0.0.12:5001",
  "datacenter": "dc1",
  "tier": "Hot"
}
```

#### Get All Nodes
```http
GET /api/nodes
```

#### Get Specific Node
```http
GET /api/nodes/{nodeId}
```

#### Delete Node
```http
DELETE /api/nodes/{nodeId}
```

## Database

### Schema Auto-Creation

The database schema is automatically created or updated when the application starts. No manual migration steps are required.

The startup process:
1. Checks for pending migrations
2. Applies migrations automatically
3. Verifies database connection
4. Logs migration status

### Manual Migrations (Optional)

If you need to create migrations manually:

```bash
# Add a new migration
dotnet ef migrations add MigrationName --project src/DocMaster.API

# Update database
dotnet ef database update --project src/DocMaster.API

# Remove last migration
dotnet ef migrations remove --project src/DocMaster.API
```

## Architecture

### Folder Structure

```
DocMaster.API/
├── Controllers/          # API controllers
│   └── NodesController.cs
├── Data/                # Database context
│   └── DocMasterDbContext.cs
├── Entities/            # Database entities
│   └── NodeEntity.cs
├── Models/              # DTOs and request/response models
│   └── NodeDtos.cs
├── BackgroundJobs/      # Background services
│   └── NodeHealthPollingHostedService.cs
├── Migrations/          # EF Core migrations
└── Program.cs          # Application entry point
```

### Health Monitoring

The `NodeHealthPollingHostedService` runs continuously in the background:

1. Polls all registered nodes every N seconds
2. Calls the gRPC `GetHealth` endpoint on each node
3. Updates node status based on response:
   - **Success**: Status = Online, update metrics, reset failure count
   - **Failure**: Increment failure count, mark Offline after threshold
4. Stores metrics: free/total bytes, CPU usage, uptime, etc.

### Node Statuses

- **Unknown**: Initial state when node is registered
- **Online**: Node is healthy and responding to health checks
- **Offline**: Node failed health checks (>= max consecutive failures)
- **Draining**: (Future) Node is being drained before removal

## Development

### Adding New Entities

1. Create entity class in `Entities/` folder
2. Add `DbSet<>` to `DocMasterDbContext`
3. Configure entity in `OnModelCreating`
4. Create migration: `dotnet ef migrations add AddEntityName`

### Environment Variables

Override configuration using environment variables:

```bash
export ConnectionStrings__DocMasterDb="Host=myhost;Port=5432;Database=docmaster;Username=user;Password=pass"
export NodeHealthPolling__IntervalSeconds=30
```

## Troubleshooting

### Database Connection Issues

If you see connection errors on startup:

1. Verify PostgreSQL is running: `pg_isready -h localhost -p 5432`
2. Check connection string in `appsettings.json`
3. Verify database user permissions
4. Check firewall settings

### Migration Errors

If migrations fail:

1. Check database connection
2. Verify user has schema modification permissions
3. Check logs for detailed error messages
4. Try manual migration: `dotnet ef database update`

## Production Deployment

### Recommended Settings

1. **Connection Pooling**: Enable in connection string
   ```
   Host=...;Pooling=true;MinPoolSize=5;MaxPoolSize=100
   ```

2. **Health Check Interval**: Increase for production
   ```json
   "NodeHealthPolling": {
     "IntervalSeconds": 30
   }
   ```

3. **Logging**: Configure appropriate log levels
   ```json
   "Logging": {
     "LogLevel": {
       "Default": "Information",
       "Microsoft.EntityFrameworkCore": "Warning"
     }
   }
   ```

## License

Part of the FileVault distributed storage system.
