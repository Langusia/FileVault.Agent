using System.Security.Cryptography;
using System.Text;
using FileVault.Agent.Node.Protos;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FileVault.Agent.Node.Tests;

public class NodeServiceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly GrpcChannel _channel;
    private readonly FileVaultNode.FileVaultNodeClient _client;
    private readonly string _testBasePath;

    public NodeServiceIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Create a unique test directory for this test run
        _testBasePath = Path.Combine(Path.GetTempPath(), $"filevault-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testBasePath);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NodeAgent:BasePath", _testBasePath);
            builder.UseSetting("NodeAgent:NodeId", "test-node-001");
            builder.UseSetting("NodeAgent:NodeName", "Test Node");
        });

        var httpClient = _factory.CreateDefaultClient();
        _channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        _client = new FileVaultNode.FileVaultNodeClient(_channel);
    }

    [Fact]
    public async Task Upload_Download_CompareContent()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Hello, FileVault! This is a test file with some content."u8.ToArray();
        var expectedChecksum = ComputeSha256(testData);

        // Act - Upload (client-streaming API)
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert - Upload
        Assert.True(uploadResult.Success);
        Assert.Equal(testData.Length, uploadResult.SizeBytes);
        Assert.Equal(expectedChecksum, uploadResult.Checksum);
        Assert.False(string.IsNullOrEmpty(uploadResult.FinalPath));

        // Act - Download
        var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId });
        var downloadedData = new List<byte>();

        while (await downloadCall.ResponseStream.MoveNext())
        {
            var chunk = downloadCall.ResponseStream.Current;
            downloadedData.AddRange(chunk.Data.ToByteArray());
        }

        // Assert - Download
        Assert.Equal(testData, downloadedData.ToArray());
        Assert.Equal(expectedChecksum, ComputeSha256(downloadedData.ToArray()));
    }

    [Fact]
    public async Task Upload_Delete_FileRemoved()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Test data for deletion"u8.ToArray();

        // Act - Upload
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        Assert.True(uploadResult.Success);

        // Act - Delete
        var deleteResult = await _client.DeleteAsync(new DeleteRequest { ObjectId = objectId });

        // Assert
        Assert.True(deleteResult.Deleted);

        // Verify file is gone
        var downloadException = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId });
            while (await downloadCall.ResponseStream.MoveNext()) { }
        });

        Assert.Equal(StatusCode.NotFound, downloadException.Status.StatusCode);
    }

    [Fact]
    public async Task Upload_SameObjectIdTwice_ReturnsAlreadyExists()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData1 = "First version"u8.ToArray();
        var testData2 = "Second version with different content"u8.ToArray();

        // Act - First Upload
        var uploadCall1 = _client.Upload();
        await uploadCall1.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData1)
        });
        await uploadCall1.RequestStream.CompleteAsync();
        var uploadResult1 = await uploadCall1.ResponseAsync;

        Assert.True(uploadResult1.Success);

        // Act - Second Upload (same objectId) - should fail with AlreadyExists
        var uploadCall2 = _client.Upload();
        await uploadCall2.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData2)
        });
        await uploadCall2.RequestStream.CompleteAsync();
        var uploadResult2 = await uploadCall2.ResponseAsync;

        // Assert - Second upload should fail
        Assert.False(uploadResult2.Success);
        Assert.Contains("already exists", uploadResult2.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Verify first file still exists with original content
        var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId });
        var data = new List<byte>();
        while (await downloadCall.ResponseStream.MoveNext())
        {
            data.AddRange(downloadCall.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(testData1, data.ToArray());
    }

    private async Task<UploadResult> UploadDataAsync(string objectId, byte[] data, int? shardIndex = null)
    {
        var uploadCall = _client.Upload();
        var chunk = new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(data)
        };
        if (shardIndex.HasValue)
        {
            chunk.ShardIndex = shardIndex.Value;
        }
        await uploadCall.RequestStream.WriteAsync(chunk);
        await uploadCall.RequestStream.CompleteAsync();
        return await uploadCall.ResponseAsync;
    }

    [Fact]
    public async Task GetHealth_ReturnsValidStatus()
    {
        // Act
        var status = await _client.GetHealthAsync(new HealthRequest());

        // Assert
        Assert.Equal("test-node-001", status.NodeId);
        Assert.True(status.IsAlive);
        Assert.True(status.DataPathTotalBytes > 0);
        Assert.True(status.DataPathFreeBytes > 0);
        Assert.True(status.DataPathFreeBytes <= status.DataPathTotalBytes);
    }

    [Fact]
    public async Task Upload_InvalidCreatedAtUtc_ReturnsError()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();

        // Act - Upload with invalid CreatedAtUtc using streaming API
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = "invalid-date-format",
            Data = Google.Protobuf.ByteString.CopyFrom("test"u8.ToArray())
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert
        Assert.False(uploadResult.Success);
        Assert.Contains("ISO-8601", uploadResult.ErrorMessage);
    }

    [Fact]
    public async Task Upload_EmptyObjectId_ReturnsError()
    {
        // Act - Upload with empty ObjectId using streaming API
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = "",
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom("test"u8.ToArray())
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert
        Assert.False(uploadResult.Success);
        Assert.Contains("ObjectId", uploadResult.ErrorMessage);
    }

    [Fact]
    public async Task Download_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var nonExistentObjectId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = nonExistentObjectId });
            while (await downloadCall.ResponseStream.MoveNext()) { }
        });

        Assert.Equal(StatusCode.NotFound, exception.Status.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var nonExistentObjectId = Guid.NewGuid().ToString();

        // Act
        var deleteResult = await _client.DeleteAsync(new DeleteRequest { ObjectId = nonExistentObjectId });

        // Assert
        Assert.False(deleteResult.Deleted);
    }

    #region Shard Tests

    [Fact]
    public async Task Upload_Download_Shard0_MatchesContent()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Shard 0 data content"u8.ToArray();
        var expectedChecksum = ComputeSha256(testData);

        // Act - Upload shard 0
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert - Upload
        Assert.True(uploadResult.Success);
        Assert.Equal(testData.Length, uploadResult.SizeBytes);
        Assert.Equal(expectedChecksum, uploadResult.Checksum);

        // Act - Download shard 0
        var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 0 });
        var downloadedData = new List<byte>();
        while (await downloadCall.ResponseStream.MoveNext())
        {
            downloadedData.AddRange(downloadCall.ResponseStream.Current.Data.ToByteArray());
        }

        // Assert - Download
        Assert.Equal(testData, downloadedData.ToArray());
    }

    [Fact]
    public async Task Upload_Download_Shard1_MatchesContent()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Shard 1 data content"u8.ToArray();
        var expectedChecksum = ComputeSha256(testData);

        // Act - Upload shard 1
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 1,
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert - Upload
        Assert.True(uploadResult.Success);
        Assert.Equal(expectedChecksum, uploadResult.Checksum);

        // Act - Download shard 1
        var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 1 });
        var downloadedData = new List<byte>();
        while (await downloadCall.ResponseStream.MoveNext())
        {
            downloadedData.AddRange(downloadCall.ResponseStream.Current.Data.ToByteArray());
        }

        // Assert - Download
        Assert.Equal(testData, downloadedData.ToArray());
    }

    [Fact]
    public async Task Upload_MultipleShardsForSameObjectId_StoresSeparately()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var shard0Data = "Shard 0 unique content"u8.ToArray();
        var shard1Data = "Shard 1 unique content"u8.ToArray();

        // Act - Upload shard 0
        var uploadCall0 = _client.Upload();
        await uploadCall0.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(shard0Data)
        });
        await uploadCall0.RequestStream.CompleteAsync();
        var uploadResult0 = await uploadCall0.ResponseAsync;

        // Act - Upload shard 1
        var uploadCall1 = _client.Upload();
        await uploadCall1.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 1,
            Data = Google.Protobuf.ByteString.CopyFrom(shard1Data)
        });
        await uploadCall1.RequestStream.CompleteAsync();
        var uploadResult1 = await uploadCall1.ResponseAsync;

        // Assert - Both uploads succeeded
        Assert.True(uploadResult0.Success);
        Assert.True(uploadResult1.Success);
        Assert.NotEqual(uploadResult0.FinalPath, uploadResult1.FinalPath);

        // Verify shard 0 content
        var downloadCall0 = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 0 });
        var downloaded0 = new List<byte>();
        while (await downloadCall0.ResponseStream.MoveNext())
        {
            downloaded0.AddRange(downloadCall0.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(shard0Data, downloaded0.ToArray());

        // Verify shard 1 content
        var downloadCall1 = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 1 });
        var downloaded1 = new List<byte>();
        while (await downloadCall1.ResponseStream.MoveNext())
        {
            downloaded1.AddRange(downloadCall1.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(shard1Data, downloaded1.ToArray());
    }

    [Fact]
    public async Task Upload_WholeObjectAndShard_NoCollision()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var wholeObjectData = "Whole object data"u8.ToArray();
        var shard0Data = "Shard 0 data"u8.ToArray();

        // Act - Upload whole object (no shardIndex)
        var uploadCallWhole = _client.Upload();
        await uploadCallWhole.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(wholeObjectData)
        });
        await uploadCallWhole.RequestStream.CompleteAsync();
        var uploadResultWhole = await uploadCallWhole.ResponseAsync;

        // Act - Upload shard 0
        var uploadCallShard = _client.Upload();
        await uploadCallShard.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(shard0Data)
        });
        await uploadCallShard.RequestStream.CompleteAsync();
        var uploadResultShard = await uploadCallShard.ResponseAsync;

        // Assert - Both uploads succeeded
        Assert.True(uploadResultWhole.Success);
        Assert.True(uploadResultShard.Success);
        Assert.NotEqual(uploadResultWhole.FinalPath, uploadResultShard.FinalPath);

        // Verify whole object content
        var downloadCallWhole = _client.Download(new DownloadRequest { ObjectId = objectId });
        var downloadedWhole = new List<byte>();
        while (await downloadCallWhole.ResponseStream.MoveNext())
        {
            downloadedWhole.AddRange(downloadCallWhole.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(wholeObjectData, downloadedWhole.ToArray());

        // Verify shard 0 content
        var downloadCallShard = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 0 });
        var downloadedShard = new List<byte>();
        while (await downloadCallShard.ResponseStream.MoveNext())
        {
            downloadedShard.AddRange(downloadCallShard.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(shard0Data, downloadedShard.ToArray());
    }

    [Fact]
    public async Task Upload_SameShardTwice_ReturnsAlreadyExists()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData1 = "First shard upload"u8.ToArray();
        var testData2 = "Second shard upload"u8.ToArray();

        // Act - First upload of shard 0
        var uploadCall1 = _client.Upload();
        await uploadCall1.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(testData1)
        });
        await uploadCall1.RequestStream.CompleteAsync();
        var uploadResult1 = await uploadCall1.ResponseAsync;

        Assert.True(uploadResult1.Success);

        // Act - Second upload of shard 0 (should fail)
        var uploadCall2 = _client.Upload();
        await uploadCall2.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(testData2)
        });
        await uploadCall2.RequestStream.CompleteAsync();
        var uploadResult2 = await uploadCall2.ResponseAsync;

        // Assert - Second upload should fail
        Assert.False(uploadResult2.Success);
        Assert.Contains("already exists", uploadResult2.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shard 0", uploadResult2.ErrorMessage);
    }

    [Fact]
    public async Task Upload_InvalidShardIndex_ReturnsError()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Test data"u8.ToArray();

        // Act - Upload with invalid shardIndex (negative)
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = -1,
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert
        Assert.False(uploadResult.Success);
        Assert.Contains("ShardIndex", uploadResult.ErrorMessage);
        Assert.Contains("0 and 255", uploadResult.ErrorMessage);
    }

    [Fact]
    public async Task Upload_ShardIndexTooLarge_ReturnsError()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData = "Test data"u8.ToArray();

        // Act - Upload with invalid shardIndex (> 255)
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 256,
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });
        await uploadCall.RequestStream.CompleteAsync();
        var uploadResult = await uploadCall.ResponseAsync;

        // Assert
        Assert.False(uploadResult.Success);
        Assert.Contains("ShardIndex", uploadResult.ErrorMessage);
        Assert.Contains("0 and 255", uploadResult.ErrorMessage);
    }

    [Fact]
    public async Task Download_NonExistentShard_ReturnsNotFound()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 0 });
            while (await downloadCall.ResponseStream.MoveNext()) { }
        });

        Assert.Equal(StatusCode.NotFound, exception.Status.StatusCode);
    }

    [Fact]
    public async Task Download_InvalidShardIndex_ThrowsInvalidArgument()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 256 });
            while (await downloadCall.ResponseStream.MoveNext()) { }
        });

        Assert.Equal(StatusCode.InvalidArgument, exception.Status.StatusCode);
        Assert.Contains("ShardIndex", exception.Status.Detail);
    }

    [Fact]
    public async Task Delete_Shard_DeletesCorrectFile()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var shard0Data = "Shard 0 data"u8.ToArray();
        var shard1Data = "Shard 1 data"u8.ToArray();

        // Upload two shards
        var uploadCall0 = _client.Upload();
        await uploadCall0.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 0,
            Data = Google.Protobuf.ByteString.CopyFrom(shard0Data)
        });
        await uploadCall0.RequestStream.CompleteAsync();
        await uploadCall0.ResponseAsync;

        var uploadCall1 = _client.Upload();
        await uploadCall1.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ShardIndex = 1,
            Data = Google.Protobuf.ByteString.CopyFrom(shard1Data)
        });
        await uploadCall1.RequestStream.CompleteAsync();
        await uploadCall1.ResponseAsync;

        // Act - Delete shard 0
        var deleteResult = await _client.DeleteAsync(new DeleteRequest { ObjectId = objectId, ShardIndex = 0 });

        // Assert
        Assert.True(deleteResult.Deleted);

        // Verify shard 0 is gone
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 0 });
            while (await downloadCall.ResponseStream.MoveNext()) { }
        });
        Assert.Equal(StatusCode.NotFound, exception.Status.StatusCode);

        // Verify shard 1 still exists
        var downloadCall1 = _client.Download(new DownloadRequest { ObjectId = objectId, ShardIndex = 1 });
        var downloaded1 = new List<byte>();
        while (await downloadCall1.ResponseStream.MoveNext())
        {
            downloaded1.AddRange(downloadCall1.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(shard1Data, downloaded1.ToArray());
    }

    [Fact]
    public async Task Delete_NonExistentShard_ReturnsFalse()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();

        // Act
        var deleteResult = await _client.DeleteAsync(new DeleteRequest { ObjectId = objectId, ShardIndex = 0 });

        // Assert
        Assert.False(deleteResult.Deleted);
    }

    [Fact]
    public async Task Delete_InvalidShardIndex_ThrowsInvalidArgument()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await _client.DeleteAsync(new DeleteRequest { ObjectId = objectId, ShardIndex = 300 });
        });

        Assert.Equal(StatusCode.InvalidArgument, exception.Status.StatusCode);
        Assert.Contains("ShardIndex", exception.Status.Detail);
    }

    #endregion

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _channel.Dispose();
        _factory.Dispose();

        // Clean up test directory
        if (Directory.Exists(_testBasePath))
        {
            try
            {
                Directory.Delete(_testBasePath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
