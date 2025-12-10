# FileVault.Agent

Storage Node Agent for FileVault - A high-performance, gRPC-based file storage service.

## Overview

The FileVault Storage Node Agent is a focused ASP.NET Core gRPC service that provides atomic file storage operations on a single node. It handles:

- **Upload**: Client-streaming upload with atomic temp-file + rename semantics
- **Download**: Server-streaming download with configurable chunk sizes
- **Delete**: Simple file deletion operations
- **Health**: Node health and disk space reporting

## Architecture

### Key Features

- **Deterministic Path Layout**: SHA-256 based sharding for even distribution
- **Atomic Operations**: Temp-file + rename pattern ensures consistency
- **Concurrency Control**: Per-object locking prevents corruption
- **Incremental Checksums**: SHA-256 computed during upload without buffering entire file
- **File Versioning**: Automatic versioning (file.pdf → file_1.pdf) for duplicate uploads
- **Configurable Sharding**: Adjustable shard depth and width via configuration

### Components

```
FileVault.Agent.Node/
├── Protos/
│   └── filevault_node.proto          # gRPC service definition
├── Services/
│   └── FileVaultNodeService.cs       # Main gRPC service implementation
├── Storage/
│   ├── PathBuilder.cs                # SHA-256 based path mapping
│   └── LocalFileStorage.cs           # Filesystem abstraction
├── Interfaces/
│   ├── IPathBuilder.cs
│   └── IFileStorage.cs
├── Configuration/
│   └── NodeAgentOptions.cs           # Strongly-typed configuration
└── Program.cs                         # Application entry point
```

## Configuration

Configure the node agent via `appsettings.json`:

```json
{
  "NodeAgent": {
    "NodeId": "node-001",
    "NodeName": "Primary Storage Node",
    "BasePath": "/mnt/192.168.1.10/2025",
    "TempDirName": "tmp",
    "MaxConcurrentUploads": 16,
    "MaxConcurrentDownloads": 32,
    "ChunkSizeBytes": 262144,
    "ShardSymbolCount": 2,
    "ShardLevelCount": 2
  }
}
```

### Configuration Options

- **NodeId**: Unique identifier for this node
- **NodeName**: Human-readable node name
- **BasePath**: Storage root path (must exist and be writable)
- **TempDirName**: Temporary directory name (default: "tmp")
- **MaxConcurrentUploads**: Max parallel uploads (default: 16)
- **MaxConcurrentDownloads**: Max parallel downloads (default: 32)
- **ChunkSizeBytes**: Streaming chunk size (default: 256KB)
- **ShardSymbolCount**: Hex characters per shard level (default: 2)
- **ShardLevelCount**: Number of shard directory levels (default: 2)

## Path Layout

The agent uses SHA-256 based deterministic sharding:

```
BasePath: /mnt/192.168.1.10/2025
ObjectId: abc123...
SHA256(ObjectId): a1b2c3d4...

With ShardSymbolCount=2, ShardLevelCount=2:
→ /mnt/192.168.1.10/2025/a1/b2/abc123...
```

The BasePath already includes the IP address and year as a mounted volume. The year is NOT part of the sharding logic.

## Upload Flow

1. Client streams `UploadRequest` (metadata) followed by `ChunkData` messages
2. Agent acquires per-object lock
3. Writes to temp file: `{BasePath}/tmp/{objectId}_{timestamp}.uploading`
4. Computes SHA-256 incrementally during write
5. On completion, performs atomic rename to final path
6. If final path exists, applies versioning: `file.pdf` → `file_1.pdf`
7. Returns `UploadResult` with path, size, and checksum

## gRPC Endpoints

### Upload
```protobuf
rpc Upload(stream UploadMessage) returns (UploadResult);
```

Streams file data with metadata in first message.

### Download
```protobuf
rpc Download(DownloadRequest) returns (stream ChunkData);
```

Streams file content in configurable chunk sizes.

### Delete
```protobuf
rpc Delete(DeleteRequest) returns (DeleteResult);
```

Deletes file from storage.

### GetHealth
```protobuf
rpc GetHealth(HealthRequest) returns (NodeStatus);
```

Returns node health and disk space information.

## Running the Service

### Prerequisites

- .NET 8.0 SDK or later
- Writable filesystem path for storage

### Development

```bash
cd src/FileVault.Agent.Node
dotnet run
```

The service listens on port 5000 (HTTP/2) by default.

### Production

```bash
dotnet publish -c Release
cd bin/Release/net8.0/publish
./FileVault.Agent.Node
```

## Testing

Run integration tests:

```bash
cd tests/FileVault.Agent.Node.Tests
dotnet test
```

### Test Coverage

- Upload → Download → verify content and checksum
- Upload → Delete → verify file removed
- Concurrent uploads with same objectId → verify versioning
- Upload with existing file → verify versioning
- Health endpoint validation
- Error handling (invalid dates, missing files, etc.)

## Concurrency & Thread Safety

- **SemaphoreSlim**: Limits total concurrent uploads/downloads
- **AsyncKeyedLock**: Per-objectId locking prevents corruption
- **Async I/O**: All file operations use async streams
- **No thread creation**: Relies on ASP.NET Core thread pool

## Security Considerations

- **Internal gRPC only**: Not exposed to public internet
- **No authentication**: Handled by upstream API service
- **Path validation**: Prevents directory traversal
- **Atomic operations**: Prevents partial writes

## What This Service Does NOT Do

- Talk to databases
- Handle buckets or storage tiers
- Manage cluster topology or replication
- Perform temp file cleanup (handled by separate workers)
- Provide HTTP/REST endpoints
- Handle authentication/authorization

## Dependencies

- **Grpc.AspNetCore** (2.60.0): gRPC framework
- **AsyncKeyedLock** (6.4.2): Per-key locking

## License

Copyright © 2025 FileVault
