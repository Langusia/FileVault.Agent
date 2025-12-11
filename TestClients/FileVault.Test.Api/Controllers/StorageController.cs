using System.Buffers;
using FileVault.Agent.Node.Protos;
using FileVault.Test.Api.Configuration;
using FileVault.Test.Api.Models;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FileVault.Test.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private readonly FileVaultNode.FileVaultNodeClient _grpcClient;
    private readonly ILogger<StorageController> _logger;
    private readonly UploadOptions _uploadOptions;

    public StorageController(
        FileVaultNode.FileVaultNodeClient grpcClient,
        ILogger<StorageController> logger,
        IOptions<UploadOptions> uploadOptions)
    {
        _grpcClient = grpcClient;
        _logger = logger;
        _uploadOptions = uploadOptions.Value;
    }

    /// <summary>
    /// Upload a file to the storage node using streaming
    /// </summary>
    /// <param name="objectId">Optional object ID (auto-generated if not provided)</param>
    /// <returns>Upload result with checksum and final path</returns>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<UploadResponse>> Upload(
        [FromQuery] string? objectId = null)
    {
        byte[]? buffer = null;

        try
        {
            // Validate that we have a request body
            if (Request.ContentLength == 0)
            {
                return BadRequest(new UploadResponse
                {
                    Success = false,
                    ErrorMessage = "Request body is empty"
                });
            }

            // Generate objectId if not provided
            var uploadObjectId = string.IsNullOrWhiteSpace(objectId)
                ? Guid.NewGuid().ToString("N")
                : objectId;

            var createdAtUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Starting streaming upload for ObjectId: {ObjectId}, ContentLength: {ContentLength}",
                uploadObjectId, Request.ContentLength ?? -1);

            // Open streaming gRPC call
            var call = _grpcClient.Upload(cancellationToken: HttpContext.RequestAborted);

            // Rent buffer from ArrayPool for efficient memory usage
            var bufferSize = _uploadOptions.ChunkSizeBytes;
            buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            bool firstChunk = true;
            long totalBytesRead = 0;

            try
            {
                int bytesRead;
                while ((bytesRead = await Request.Body.ReadAsync(
                    buffer.AsMemory(0, bufferSize),
                    HttpContext.RequestAborted)) > 0)
                {
                    var chunk = new FileChunk
                    {
                        Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
                    };

                    // Include metadata in first chunk
                    if (firstChunk)
                    {
                        chunk.ObjectId = uploadObjectId;
                        chunk.CreatedAtUtc = createdAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                        firstChunk = false;

                        _logger.LogDebug(
                            "Sending first chunk with metadata: ObjectId={ObjectId}, Bytes={Bytes}",
                            uploadObjectId, bytesRead);
                    }

                    await call.RequestStream.WriteAsync(chunk);
                    totalBytesRead += bytesRead;
                }

                // Handle case where no data was read (empty body)
                if (firstChunk)
                {
                    // Send at least one chunk with metadata
                    var emptyChunk = new FileChunk
                    {
                        ObjectId = uploadObjectId,
                        CreatedAtUtc = createdAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                        Data = Google.Protobuf.ByteString.Empty
                    };
                    await call.RequestStream.WriteAsync(emptyChunk);
                }

                // Complete the stream
                await call.RequestStream.CompleteAsync();

                _logger.LogInformation(
                    "Completed streaming {TotalBytes} bytes for ObjectId: {ObjectId}",
                    totalBytesRead, uploadObjectId);

                // Get result
                var result = await call.ResponseAsync;

                var response = new UploadResponse
                {
                    Success = result.Success,
                    ErrorMessage = result.ErrorMessage,
                    FinalPath = result.FinalPath,
                    SizeBytes = result.SizeBytes,
                    Checksum = result.Checksum
                };

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Upload successful: ObjectId={ObjectId}, Path={Path}, Size={Size}, Checksum={Checksum}",
                        uploadObjectId, result.FinalPath, result.SizeBytes, result.Checksum);
                }
                else
                {
                    _logger.LogWarning(
                        "Upload failed: ObjectId={ObjectId}, Error={Error}",
                        uploadObjectId, result.ErrorMessage);
                }

                return result.Success ? Ok(response) : BadRequest(response);
            }
            finally
            {
                // Return buffer to pool
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = null;
                }
            }
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during upload");
            return StatusCode((int)ex.StatusCode, new UploadResponse
            {
                Success = false,
                ErrorMessage = $"gRPC error: {ex.Status.Detail}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during upload");
            return StatusCode(500, new UploadResponse
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            });
        }
        finally
        {
            // Ensure buffer is returned in case of exception
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Download a file from the storage node
    /// </summary>
    /// <param name="objectId">The object ID to download</param>
    /// <param name="finalPath">Optional final path if known</param>
    /// <returns>File stream</returns>
    [HttpGet("download/{objectId}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        string objectId,
        [FromQuery] string? finalPath = null)
    {
        try
        {
            _logger.LogInformation("Downloading file: ObjectId={ObjectId}", objectId);

            var grpcRequest = new DownloadRequest
            {
                ObjectId = objectId
            };

            if (!string.IsNullOrWhiteSpace(finalPath))
            {
                grpcRequest.FinalPath = finalPath;
            }

            var call = _grpcClient.Download(grpcRequest);

            // Stream chunks into memory
            var memoryStream = new MemoryStream();
            await foreach (var chunk in call.ResponseStream.ReadAllAsync())
            {
                var data = chunk.Data.ToByteArray();
                await memoryStream.WriteAsync(data);
            }

            memoryStream.Position = 0;

            _logger.LogInformation(
                "Download successful: ObjectId={ObjectId}, Size={Size} bytes",
                objectId, memoryStream.Length);

            return File(memoryStream, "application/octet-stream", objectId);
        }
        catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            _logger.LogWarning("File not found: ObjectId={ObjectId}", objectId);
            return NotFound(new { message = "File not found" });
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during download");
            return StatusCode((int)ex.StatusCode, new { message = ex.Status.Detail });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during download");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a file from the storage node
    /// </summary>
    /// <param name="objectId">The object ID to delete</param>
    /// <param name="finalPath">Optional final path if known</param>
    /// <returns>Delete result</returns>
    [HttpDelete("delete/{objectId}")]
    [ProducesResponseType(typeof(DeleteResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeleteResponse>> Delete(
        string objectId,
        [FromQuery] string? finalPath = null)
    {
        try
        {
            _logger.LogInformation("Deleting file: ObjectId={ObjectId}", objectId);

            var grpcRequest = new DeleteRequest
            {
                ObjectId = objectId
            };

            if (!string.IsNullOrWhiteSpace(finalPath))
            {
                grpcRequest.FinalPath = finalPath;
            }

            var result = await _grpcClient.DeleteAsync(grpcRequest);

            _logger.LogInformation(
                "Delete result: ObjectId={ObjectId}, Deleted={Deleted}",
                objectId, result.Deleted);

            return Ok(new DeleteResponse { Deleted = result.Deleted });
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during delete");
            return StatusCode((int)ex.StatusCode, new DeleteResponse { Deleted = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during delete");
            return StatusCode(500, new DeleteResponse { Deleted = false });
        }
    }

    /// <summary>
    /// Get health status of the storage node
    /// </summary>
    /// <returns>Node health information</returns>
    [HttpGet("health")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<HealthResponse>> GetHealth()
    {
        try
        {
            var result = await _grpcClient.GetHealthAsync(new HealthRequest());

            var response = new HealthResponse
            {
                NodeId = result.NodeId,
                IsAlive = result.IsAlive,
                DataPathFreeBytes = result.DataPathFreeBytes,
                DataPathTotalBytes = result.DataPathTotalBytes
            };

            _logger.LogDebug(
                "Health check: NodeId={NodeId}, IsAlive={IsAlive}, Free={FreeGB}GB, Total={TotalGB}GB",
                response.NodeId, response.IsAlive, response.DataPathFreeGB, response.DataPathTotalGB);

            return Ok(response);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during health check");
            return StatusCode((int)ex.StatusCode, new HealthResponse
            {
                NodeId = "unknown",
                IsAlive = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during health check");
            return StatusCode(500, new HealthResponse
            {
                NodeId = "unknown",
                IsAlive = false
            });
        }
    }
}
