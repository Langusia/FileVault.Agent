using FileVault.Agent.Node.Protos;
using FileVault.Test.Api.Models;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;

namespace FileVault.Test.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private readonly FileVaultNode.FileVaultNodeClient _grpcClient;
    private readonly ILogger<StorageController> _logger;

    public StorageController(
        FileVaultNode.FileVaultNodeClient grpcClient,
        ILogger<StorageController> logger)
    {
        _grpcClient = grpcClient;
        _logger = logger;
    }

    /// <summary>
    /// Upload a file to the storage node
    /// </summary>
    /// <param name="file">The file to upload</param>
    /// <param name="objectId">Optional object ID (auto-generated if not provided)</param>
    /// <returns>Upload result with checksum and final path</returns>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadResponse>> Upload(
        IFormFile file,
        [FromForm] string? objectId = null)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new UploadResponse
                {
                    Success = false,
                    ErrorMessage = "No file provided or file is empty"
                });
            }

            // Generate objectId if not provided
            var uploadObjectId = string.IsNullOrWhiteSpace(objectId)
                ? Guid.NewGuid().ToString()
                : objectId;

            _logger.LogInformation(
                "Uploading file: {FileName}, Size: {Size} bytes, ObjectId: {ObjectId}",
                file.FileName, file.Length, uploadObjectId);

            // Read file into memory
            byte[] fileData;
            using (var memoryStream = new MemoryStream())
            {
                await file.CopyToAsync(memoryStream);
                fileData = memoryStream.ToArray();
            }

            // Call gRPC service
            var grpcRequest = new UploadRequest
            {
                ObjectId = uploadObjectId,
                CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ContentType = file.ContentType,
                OriginalFilename = file.FileName,
                Data = Google.Protobuf.ByteString.CopyFrom(fileData)
            };

            var result = await _grpcClient.UploadAsync(grpcRequest);

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
                    "Upload successful: ObjectId={ObjectId}, Path={Path}, Checksum={Checksum}",
                    uploadObjectId, result.FinalPath, result.Checksum);
            }
            else
            {
                _logger.LogWarning(
                    "Upload failed: ObjectId={ObjectId}, Error={Error}",
                    uploadObjectId, result.ErrorMessage);
            }

            return Ok(response);
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
