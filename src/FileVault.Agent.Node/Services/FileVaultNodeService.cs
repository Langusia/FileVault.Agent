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
        UploadRequest request,
        ServerCallContext context)
    {
        string? objectId = null;
        string? tempPath = null;
        IDisposable? keyLock = null;

        try
        {
            // Wait for upload slot
            await _uploadLimiter.WaitAsync(context.CancellationToken);

            objectId = request.ObjectId;

            // Validate objectId
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "ObjectId is required"
                };
            }

            // Validate createdAtUtc
            if (string.IsNullOrWhiteSpace(request.CreatedAtUtc))
            {
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = "CreatedAtUtc is required"
                };
            }

            if (!DateTime.TryParseExact(
                request.CreatedAtUtc,
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

            _logger.LogInformation(
                "Starting upload for objectId: {ObjectId}, contentType: {ContentType}, originalFilename: {OriginalFilename}, size: {Size}",
                objectId, request.ContentType, request.OriginalFilename, request.Data.Length);

            // Acquire per-object lock
            var lockKey = _pathBuilder.GetLockKey(objectId);
            keyLock = await _keyedLock.LockAsync(lockKey, context.CancellationToken);

            // Get temp path
            tempPath = _pathBuilder.GetTempPath(objectId);

            // Get data from request
            var data = request.Data.ToByteArray();

            // Compute checksum
            var checksum = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

            // Write to temp file
            await File.WriteAllBytesAsync(tempPath, data, context.CancellationToken);

            // Get final path
            var finalPath = _pathBuilder.GetFinalPath(objectId);

            // Handle versioning if file exists
            if (await _fileStorage.ExistsAsync(finalPath, context.CancellationToken))
            {
                finalPath = GetVersionedPath(finalPath);
            }

            // Ensure directory exists
            await _fileStorage.EnsureDirectoryAsync(finalPath, context.CancellationToken);

            // Atomic move
            await _fileStorage.MoveAsync(tempPath, finalPath, context.CancellationToken);

            // Clear temp path reference since file was moved
            tempPath = null;

            var relativePath = Path.GetRelativePath(_options.BasePath, finalPath);

            _logger.LogInformation(
                "Upload completed for objectId: {ObjectId}, path: {Path}, size: {Size}, checksum: {Checksum}",
                objectId, relativePath, data.Length, checksum);

            return new UploadResult
            {
                Success = true,
                FinalPath = relativePath,
                SizeBytes = data.Length,
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

            // Determine final path
            string finalPath;
            if (!string.IsNullOrWhiteSpace(request.FinalPath))
            {
                finalPath = Path.Combine(_options.BasePath, request.FinalPath);
            }
            else if (!string.IsNullOrWhiteSpace(objectId))
            {
                finalPath = _pathBuilder.GetFinalPath(objectId);
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
                _logger.LogWarning("File not found for download: {FinalPath}", finalPath);
                throw new RpcException(new Status(StatusCode.NotFound, "File not found"));
            }

            _logger.LogInformation("Starting download for objectId: {ObjectId}, path: {Path}", objectId, finalPath);

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

            _logger.LogInformation("Download completed for objectId: {ObjectId}, bytes sent: {TotalBytes}", objectId, totalBytes);
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
            // Determine final path
            string finalPath;
            if (!string.IsNullOrWhiteSpace(request.FinalPath))
            {
                finalPath = Path.Combine(_options.BasePath, request.FinalPath);
            }
            else if (!string.IsNullOrWhiteSpace(objectId))
            {
                finalPath = _pathBuilder.GetFinalPath(objectId);
            }
            else
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "Either ObjectId or FinalPath must be provided"));
            }

            _logger.LogInformation("Deleting file for objectId: {ObjectId}, path: {Path}", objectId, finalPath);

            var deleted = await _fileStorage.DeleteAsync(finalPath, context.CancellationToken);

            if (deleted)
            {
                _logger.LogInformation("Successfully deleted file for objectId: {ObjectId}", objectId);
            }
            else
            {
                _logger.LogInformation("File not found for deletion, objectId: {ObjectId}", objectId);
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

    /// <summary>
    /// Gets a versioned path if the original path already exists
    /// </summary>
    private string GetVersionedPath(string originalPath)
    {
        if (!File.Exists(originalPath))
            return originalPath;

        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var filename = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);

        int version = 1;
        string versionedPath;
        do
        {
            versionedPath = Path.Combine(directory, $"{filename}_{version}{extension}");
            version++;
        } while (File.Exists(versionedPath));

        _logger.LogInformation("File exists, using versioned path: {VersionedPath}", versionedPath);
        return versionedPath;
    }
}
