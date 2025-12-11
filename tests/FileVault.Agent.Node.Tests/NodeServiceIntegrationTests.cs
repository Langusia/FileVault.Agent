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
    public async Task Upload_SameObjectIdTwice_VersioningWorks()
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
        var firstPath = uploadResult1.FinalPath;

        // Act - Second Upload (same objectId)
        var uploadCall2 = _client.Upload();
        await uploadCall2.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(testData2)
        });
        await uploadCall2.RequestStream.CompleteAsync();
        var uploadResult2 = await uploadCall2.ResponseAsync;

        Assert.True(uploadResult2.Success);
        var secondPath = uploadResult2.FinalPath;

        // Assert - Different paths with versioning
        Assert.NotEqual(firstPath, secondPath);
        Assert.Contains("_1", secondPath);

        // Verify both files exist and have correct content
        var downloadCall1 = _client.Download(new DownloadRequest { FinalPath = firstPath });
        var data1 = new List<byte>();
        while (await downloadCall1.ResponseStream.MoveNext())
        {
            data1.AddRange(downloadCall1.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(testData1, data1.ToArray());

        var downloadCall2 = _client.Download(new DownloadRequest { FinalPath = secondPath });
        var data2 = new List<byte>();
        while (await downloadCall2.ResponseStream.MoveNext())
        {
            data2.AddRange(downloadCall2.ResponseStream.Current.Data.ToByteArray());
        }
        Assert.Equal(testData2, data2.ToArray());
    }

    private async Task<UploadResult> UploadDataAsync(string objectId, byte[] data)
    {
        var uploadCall = _client.Upload();
        await uploadCall.RequestStream.WriteAsync(new FileChunk
        {
            ObjectId = objectId,
            CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            Data = Google.Protobuf.ByteString.CopyFrom(data)
        });
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
