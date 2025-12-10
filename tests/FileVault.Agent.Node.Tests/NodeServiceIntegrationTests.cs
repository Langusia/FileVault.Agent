using System.Security.Cryptography;
using System.Text;
using FileVault.Agent.Node.Protos;
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

        // Act - Upload
        var uploadResult = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ContentType = "text/plain",
            OriginalFilename = "test.txt",
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });

        // Assert - Upload
        Assert.True(uploadResult.Success);
        Assert.Equal(testData.Length, uploadResult.SizeBytes);
        Assert.Equal(expectedChecksum, uploadResult.Checksum);
        Assert.False(string.IsNullOrEmpty(uploadResult.FinalPath));

        // Act - Download
        var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId });
        var downloadedData = new List<byte>();

        await foreach (var chunk in downloadCall.ResponseStream.ReadAllAsync())
        {
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
        var uploadResult = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData)
        });

        Assert.True(uploadResult.Success);

        // Act - Delete
        var deleteResult = await _client.DeleteAsync(new DeleteRequest { ObjectId = objectId });

        // Assert
        Assert.True(deleteResult.Deleted);

        // Verify file is gone
        var downloadException = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = objectId });
            await foreach (var _ in downloadCall.ResponseStream.ReadAllAsync()) { }
        });

        Assert.Equal(Grpc.Core.StatusCode.NotFound, downloadException.StatusCode);
    }

    [Fact]
    public async Task Upload_SameObjectIdTwice_VersioningWorks()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData1 = "First version"u8.ToArray();
        var testData2 = "Second version with different content"u8.ToArray();

        // Act - First Upload
        var uploadResult1 = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData1)
        });

        Assert.True(uploadResult1.Success);
        var firstPath = uploadResult1.FinalPath;

        // Act - Second Upload (same objectId)
        var uploadResult2 = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData2)
        });

        Assert.True(uploadResult2.Success);
        var secondPath = uploadResult2.FinalPath;

        // Assert - Different paths with versioning
        Assert.NotEqual(firstPath, secondPath);
        Assert.Contains("_1", secondPath);

        // Verify both files exist and have correct content
        var downloadCall1 = _client.Download(new DownloadRequest { FinalPath = firstPath });
        var data1 = new List<byte>();
        await foreach (var chunk in downloadCall1.ResponseStream.ReadAllAsync())
        {
            data1.AddRange(chunk.Data.ToByteArray());
        }
        Assert.Equal(testData1, data1.ToArray());

        var downloadCall2 = _client.Download(new DownloadRequest { FinalPath = secondPath });
        var data2 = new List<byte>();
        await foreach (var chunk in downloadCall2.ResponseStream.ReadAllAsync())
        {
            data2.AddRange(chunk.Data.ToByteArray());
        }
        Assert.Equal(testData2, data2.ToArray());
    }

    [Fact]
    public async Task ConcurrentUploads_SameObjectId_NoCorruption()
    {
        // Arrange
        var objectId = Guid.NewGuid().ToString();
        var testData1 = Encoding.UTF8.GetBytes(new string('A', 1000));
        var testData2 = Encoding.UTF8.GetBytes(new string('B', 1000));

        // Act - Upload concurrently
        var uploadTask1 = UploadDataAsync(objectId, testData1);
        var uploadTask2 = UploadDataAsync(objectId, testData2);

        var results = await Task.WhenAll(uploadTask1, uploadTask2);

        // Assert - Both uploads succeeded
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);

        // Different paths due to versioning
        Assert.NotEqual(results[0].FinalPath, results[1].FinalPath);

        // Checksums match expected
        Assert.Equal(ComputeSha256(testData1), results[0].Checksum);
        Assert.Equal(ComputeSha256(testData2), results[1].Checksum);
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

        // Act
        var uploadResult = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = "invalid-date-format",
            Data = Google.Protobuf.ByteString.CopyFrom("test"u8.ToArray())
        });

        // Assert
        Assert.False(uploadResult.Success);
        Assert.Contains("ISO-8601", uploadResult.ErrorMessage);
    }

    [Fact]
    public async Task Upload_EmptyObjectId_ReturnsError()
    {
        // Act
        var uploadResult = await _client.UploadAsync(new UploadRequest
        {
            ObjectId = "",
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom("test"u8.ToArray())
        });

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
        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            var downloadCall = _client.Download(new DownloadRequest { ObjectId = nonExistentObjectId });
            await foreach (var _ in downloadCall.ResponseStream.ReadAllAsync()) { }
        });

        Assert.Equal(Grpc.Core.StatusCode.NotFound, exception.StatusCode);
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

    private async Task<UploadResult> UploadDataAsync(string objectId, byte[] data)
    {
        return await _client.UploadAsync(new UploadRequest
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(data)
        });
    }

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
