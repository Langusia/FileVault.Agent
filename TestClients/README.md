# FileVault Test Clients

This folder contains test client applications for the FileVault Storage Node Agent.

## FileVault.Test.Api

A REST API with Swagger UI that provides easy testing of the gRPC Node Agent service.

### Architecture

```
Client (Browser/Swagger) → REST API (HTTP) → gRPC Node Agent → File Storage
                         (port 5001)        (port 5000)
```

### Running the Services

You need to run **both** services for testing:

#### 1. Start the Node Agent (gRPC Service)

```bash
cd src/FileVault.Agent.Node
dotnet run
```

The Node Agent will start on `http://localhost:5000` (HTTP/2 for gRPC).

#### 2. Start the Test API (REST Service)

```bash
cd TestClients/FileVault.Test.Api
dotnet run
```

The Test API will start on `http://localhost:5001` and automatically open Swagger UI.

### Using Swagger UI

Once both services are running, open your browser to:
```
http://localhost:5001
```

You'll see the Swagger UI with the following endpoints:

#### **POST /api/storage/upload**
Upload a file to the storage node.
- Click "Try it out"
- Choose a file using the file picker
- Optionally provide a custom `objectId`
- Click "Execute"
- Response includes: `finalPath`, `sizeBytes`, `checksum` (SHA-256)

#### **GET /api/storage/download/{objectId}**
Download a file from the storage node.
- Click "Try it out"
- Enter the `objectId` from the upload response
- Click "Execute"
- File will be downloaded to your browser

#### **DELETE /api/storage/delete/{objectId}**
Delete a file from the storage node.
- Click "Try it out"
- Enter the `objectId` to delete
- Click "Execute"
- Response indicates whether file was deleted

#### **GET /api/storage/health**
Get health status of the storage node.
- Click "Try it out"
- Click "Execute"
- Response includes: `nodeId`, `isAlive`, disk space info in bytes and GB

### Configuration

Edit `TestClients/FileVault.Test.Api/appsettings.json` to change settings:

```json
{
  "GrpcClient": {
    "Address": "http://localhost:5000"  // gRPC Node Agent address
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5001"  // REST API address
      }
    }
  }
}
```

### Example Testing Workflow

1. **Start both services** (Node Agent + Test API)
2. **Upload a file**:
   - Use Swagger UI to upload a test file
   - Note the `objectId` and `checksum` from the response
3. **Check health**:
   - Verify the node is alive and has disk space
4. **Download the file**:
   - Use the `objectId` from step 2 to download
   - Verify the file matches what you uploaded
5. **Delete the file**:
   - Use the same `objectId` to delete
   - Verify `deleted: true` in response
6. **Verify deletion**:
   - Try to download the same `objectId`
   - Should return 404 Not Found

### Features

- **Swagger UI**: Interactive API documentation
- **File Upload**: Multipart form-data file uploads
- **File Download**: Stream files back to browser
- **SHA-256 Checksums**: Verify file integrity
- **Error Handling**: Proper HTTP status codes
- **Logging**: Detailed logs of all operations

### API Response Examples

**Upload Success:**
```json
{
  "success": true,
  "errorMessage": null,
  "finalPath": "a1/b2/abc123...",
  "sizeBytes": 1024,
  "checksum": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}
```

**Health Check:**
```json
{
  "nodeId": "node-001",
  "isAlive": true,
  "dataPathFreeBytes": 500000000000,
  "dataPathTotalBytes": 1000000000000,
  "dataPathFreeGB": 465.66,
  "dataPathTotalGB": 931.32,
  "usagePercent": 50.00
}
```

### Troubleshooting

**Test API can't connect to Node Agent:**
- Verify Node Agent is running on port 5000
- Check `GrpcClient:Address` in appsettings.json
- Look for connection errors in logs

**Upload fails:**
- Check Node Agent logs for errors
- Verify `BasePath` in Node Agent config exists and is writable
- Check disk space

**Port already in use:**
- Change port in `Kestrel:Endpoints:Http:Url` (appsettings.json)
- Or kill the process using the port

### Development Tips

- Use Swagger's "Try it out" for quick testing
- Check both service logs for debugging
- Upload small test files first (< 1MB)
- Use the `objectId` parameter for predictable IDs
- SHA-256 checksums allow verification of data integrity

### Architecture Notes

The Test API is a thin HTTP wrapper around the gRPC service:
- No business logic - just protocol translation
- Streams are converted to/from HTTP
- All error handling mirrors gRPC status codes
- Suitable for manual testing and integration tests
