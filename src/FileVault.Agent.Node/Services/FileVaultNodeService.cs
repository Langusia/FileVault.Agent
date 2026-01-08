using System.Globalization;
using System.Security.Cryptography;
using AsyncKeyedLock;
using FileVault.Agent.Node.Configuration;
using FileVault.Agent.Node.Interfaces;
using FileVault.Agent.Node.Protos;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace FileVault.Agent.Node.Services;

public class FileVaultNodeService : FileVaultNode.FileVaultNodeBase
{
    private readonly ILogger<FileVaultNodeService> _logger;
    private readonly IPathBuilder _pathBuilder;
    private readonly IFileStorage _fileStorage;
    private readonly NodeAgentOptions _options;
    private readonly SemaphoreSlim _uploadLimiter;
    private readonly SemaphoreSlim _downloadLimiter;
    private readonly AsyncKeyedLocker<string> _keyedLock;

    public FileVaultNodeService(
        ILogger<FileVaultNodeService> logger,
        IPathBuilder pathBuilder,
        IFileStorage fileStorage,
        IOptions<NodeAgentOptions> options)
    {
        _logger = logger;
        _pathBuilder = pathBuilder;
        _fileStorage = fileStorage;
        _options = options.Value;
        _uploadLimiter = new SemaphoreSlim(_options.MaxConcurrentUploads);
        _downloadLimiter = new SemaphoreSlim(_options.MaxConcurrentDownloads);
        _keyedLock = new AsyncKeyedLocker<string>(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });
    }

    public override async Task<UploadResult> Upload(
        IAsyncStreamReader<FileChunk> requestStream,
        ServerCallContext context)
    {
        string? objectId = null;
        string? tempPath = null;
        IDisposable? keyLock = null;
        FileStream? tempFileStream = null;

        try
        {
            // Wait for upload slot
            await _uploadLimiter.WaitAsync(context.CancellationToken);

            // Read the first chunk to get metadata
            if (!await requestStream.MoveNext(context.CancellationToken))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "Stream contains no chunks"
                };
            }

            var firstChunk = requestStream.Current;
            objectId = firstChunk.ObjectId;

            // Validate objectId
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "ObjectId is required in first chunk"
                };
            }

            // Validate createdAtUtc
            if (string.IsNullOrWhiteSpace(firstChunk.CreatedAtUtc))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "CreatedAtUtc is required in first chunk"
                };
            }

            if (!DateTime.TryParseExact(
                firstChunk.CreatedAtUtc,
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "CreatedAtUtc must be in ISO-8601 format with Z suffix"
                };
            }

            // Extract and validate shardIndex (optional, only from first chunk)
            int? shardIndex = null;
            if (firstChunk.HasShardIndex)
            {
                shardIndex = firstChunk.ShardIndex;
                if (shardIndex < 0 || shardIndex > 255)
                {
                    return new UploadResult
                    {
                        Success = false,
                        ErrorMessage = "ShardIndex must be between 0 and 255 (inclusive)"
                    };
                }
            }

            _logger.LogInformation(
                "Starting streaming upload for objectId: {ObjectId}, shardIndex: {ShardIndex}",
                objectId, shardIndex?.ToString() ?? "none");

            // Acquire per-object lock (includes shardIndex for shard-specific locking)
            var lockKey = _pathBuilder.GetLockKey(objectId, shardIndex);
            keyLock = await _keyedLock.LockAsync(lockKey, context.CancellationToken);

            // Get temp path
            tempPath = _pathBuilder.GetTempPath(objectId, shardIndex);

            // Ensure temp directory exists
            var tempDir = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(tempDir) && !Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // Open temp file for writing
            tempFileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920, // 80KB buffer
                useAsync: true);

            // Initialize SHA-256 for incremental hashing
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;

            // Process first chunk data if not empty
            if (firstChunk.Data != null && firstChunk.Data.Length > 0)
            {
                var chunkData = firstChunk.Data.ToByteArray();
                await tempFileStream.WriteAsync(chunkData, context.CancellationToken);
                sha256.AppendData(chunkData);
                totalBytes += chunkData.Length;
            }

            // Stream remaining chunks
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var chunk = requestStream.Current;
                if (chunk.Data != null && chunk.Data.Length > 0)
                {
                    var chunkData = chunk.Data.ToByteArray();
                    await tempFileStream.WriteAsync(chunkData, context.CancellationToken);
                    sha256.AppendData(chunkData);
                    totalBytes += chunkData.Length;
                }
            }

            // Finalize hash
            var checksumBytes = sha256.GetHashAndReset();
            var checksum = Convert.ToHexString(checksumBytes).ToLowerInvariant();

            // Close temp file before moving
            await tempFileStream.DisposeAsync();
            tempFileStream = null;

            _logger.LogInformation(
                "Received {TotalBytes} bytes for objectId: {ObjectId}, shardIndex: {ShardIndex}, checksum: {Checksum}",
                totalBytes, objectId, shardIndex?.ToString() ?? "none", checksum);

            // Get final path (includes shardIndex if provided)
            var finalPath = _pathBuilder.GetFinalPath(objectId, shardIndex);

            // Check if file already exists - fail with AlreadyExists (deterministic paths, no versioning)
            if (await _fileStorage.ExistsAsync(finalPath, context.CancellationToken))
            {
                _logger.LogWarning(
                    "File already exists for objectId: {ObjectId}, shardIndex: {ShardIndex}",
                    objectId, shardIndex?.ToString() ?? "none");

                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = shardIndex.HasValue
                        ? $"Shard {shardIndex.Value} already exists for objectId {objectId}"
                        : $"Object {objectId} already exists"
                };
            }

            // Ensure directory exists
            await _fileStorage.EnsureDirectoryAsync(finalPath, context.CancellationToken);

            // Atomic move
            await _fileStorage.MoveAsync(tempPath, finalPath, context.CancellationToken);

            // Clear temp path reference since file was moved
            tempPath = null;

            var relativePath = Path.GetRelativePath(_options.BasePath, finalPath);

            _logger.LogInformation(
                "Upload completed for objectId: {ObjectId}, shardIndex: {ShardIndex}, path: {Path}, size: {Size}, checksum: {Checksum}",
                objectId, shardIndex?.ToString() ?? "none", relativePath, totalBytes, checksum);

            return new UploadResult
            {
                Success = true,
                FinalPath = relativePath,
                SizeBytes = totalBytes,
                Checksum = checksum
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Upload cancelled for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.Cancelled, "Upload cancelled"));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error during upload for objectId: {ObjectId}", objectId);

            // Check for disk space issues
            if (ex.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Insufficient disk space"));
            }

            throw new RpcException(new Status(StatusCode.Internal, $"IO error: {ex.Message}"));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during upload for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.Internal, $"Unexpected error: {ex.Message}"));
        }
        finally
        {
            // Close temp file stream if still open
            if (tempFileStream != null)
            {
                try
                {
                    await tempFileStream.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose temp file stream");
                }
            }

            // Clean up temp file if it still exists
            if (tempPath != null)
            {
                try
                {
                    await _fileStorage.DeleteAsync(tempPath);
                    _logger.LogDebug("Cleaned up temp file: {TempPath}", tempPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp file: {TempPath}", tempPath);
                }
            }

            // Release locks
            keyLock?.Dispose();
            _uploadLimiter.Release();
        }
    }

    public override async Task Download(
        DownloadRequest request,
        IServerStreamWriter<ChunkData> responseStream,
        ServerCallContext context)
    {
        string? objectId = request.ObjectId;

        try
        {
            // Wait for download slot
            await _downloadLimiter.WaitAsync(context.CancellationToken);

            // Extract and validate shardIndex (optional)
            int? shardIndex = null;
            if (request.HasShardIndex)
            {
                shardIndex = request.ShardIndex;
                if (shardIndex < 0 || shardIndex > 255)
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "ShardIndex must be between 0 and 255 (inclusive)"));
                }
            }

            // Determine final path
            string finalPath;
            if (!string.IsNullOrWhiteSpace(request.FinalPath))
            {
                finalPath = Path.Combine(_options.BasePath, request.FinalPath);
            }
            else if (!string.IsNullOrWhiteSpace(objectId))
            {
                finalPath = _pathBuilder.GetFinalPath(objectId, shardIndex);
            }
            else
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "Either ObjectId or FinalPath must be provided"));
            }

            // Check if file exists
            if (!await _fileStorage.ExistsAsync(finalPath, context.CancellationToken))
            {
                _logger.LogWarning("File not found for download: {FinalPath}, shardIndex: {ShardIndex}",
                    finalPath, shardIndex?.ToString() ?? "none");
                throw new RpcException(new Status(StatusCode.NotFound, "File not found"));
            }

            _logger.LogInformation("Starting download for objectId: {ObjectId}, shardIndex: {ShardIndex}, path: {Path}",
                objectId, shardIndex?.ToString() ?? "none", finalPath);

            // Stream file in chunks
            await using var fileStream = await _fileStorage.ReadAsync(finalPath, context.CancellationToken);
            var buffer = new byte[_options.ChunkSizeBytes];
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await fileStream.ReadAsync(buffer, context.CancellationToken)) > 0)
            {
                var chunkData = new ChunkData
                {
                    Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
                };

                await responseStream.WriteAsync(chunkData, context.CancellationToken);
                totalBytes += bytesRead;
            }

            _logger.LogInformation("Download completed for objectId: {ObjectId}, shardIndex: {ShardIndex}, bytes sent: {TotalBytes}",
                objectId, shardIndex?.ToString() ?? "none", totalBytes);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Download cancelled for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.Cancelled, "Download cancelled"));
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "File not found for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.NotFound, "File not found"));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during download for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.Internal, $"Download error: {ex.Message}"));
        }
        finally
        {
            _downloadLimiter.Release();
        }
    }

    public override async Task<DeleteResult> Delete(DeleteRequest request, ServerCallContext context)
    {
        string? objectId = request.ObjectId;

        try
        {
            // Extract and validate shardIndex (optional)
            int? shardIndex = null;
            if (request.HasShardIndex)
            {
                shardIndex = request.ShardIndex;
                if (shardIndex < 0 || shardIndex > 255)
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "ShardIndex must be between 0 and 255 (inclusive)"));
                }
            }

            // Determine final path
            string finalPath;
            if (!string.IsNullOrWhiteSpace(request.FinalPath))
            {
                finalPath = Path.Combine(_options.BasePath, request.FinalPath);
            }
            else if (!string.IsNullOrWhiteSpace(objectId))
            {
                finalPath = _pathBuilder.GetFinalPath(objectId, shardIndex);
            }
            else
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "Either ObjectId or FinalPath must be provided"));
            }

            _logger.LogInformation("Deleting file for objectId: {ObjectId}, shardIndex: {ShardIndex}, path: {Path}",
                objectId, shardIndex?.ToString() ?? "none", finalPath);

            var deleted = await _fileStorage.DeleteAsync(finalPath, context.CancellationToken);

            if (deleted)
            {
                _logger.LogInformation("Successfully deleted file for objectId: {ObjectId}, shardIndex: {ShardIndex}",
                    objectId, shardIndex?.ToString() ?? "none");
            }
            else
            {
                _logger.LogInformation("File not found for deletion, objectId: {ObjectId}, shardIndex: {ShardIndex}",
                    objectId, shardIndex?.ToString() ?? "none");
            }

            return new DeleteResult { Deleted = deleted };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during delete for objectId: {ObjectId}", objectId);
            throw new RpcException(new Status(StatusCode.Internal, $"Delete error: {ex.Message}"));
        }
    }

    public override Task<NodeStatus> GetHealth(HealthRequest request, ServerCallContext context)
    {
        try
        {
            var driveInfo = new DriveInfo(_options.BasePath);

            var status = new NodeStatus
            {
                NodeId = _options.NodeId,
                IsAlive = driveInfo.IsReady,
                DataPathFreeBytes = driveInfo.AvailableFreeSpace,
                DataPathTotalBytes = driveInfo.TotalSize
            };

            _logger.LogDebug(
                "Health check: NodeId={NodeId}, IsAlive={IsAlive}, Free={FreeBytes}, Total={TotalBytes}",
                status.NodeId, status.IsAlive, status.DataPathFreeBytes, status.DataPathTotalBytes);

            return Task.FromResult(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");

            return Task.FromResult(new NodeStatus
            {
                NodeId = _options.NodeId,
                IsAlive = false,
                DataPathFreeBytes = 0,
                DataPathTotalBytes = 0
            });
        }
    }

}
