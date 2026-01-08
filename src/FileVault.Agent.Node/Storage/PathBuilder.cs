using System.Security.Cryptography;
using System.Text;
using FileVault.Agent.Node.Configuration;
using FileVault.Agent.Node.Interfaces;
using Microsoft.Extensions.Options;

namespace FileVault.Agent.Node.Storage;

/// <summary>
/// Builds deterministic file paths using SHA-256 based sharding
/// </summary>
public class PathBuilder : IPathBuilder
{
    private readonly NodeAgentOptions _options;
    private readonly string _tempDirectory;

    public PathBuilder(IOptions<NodeAgentOptions> options)
    {
        _options = options.Value;
        _tempDirectory = Path.Combine(_options.BasePath, _options.TempDirName);
    }

    public string GetFinalPath(string objectId, int? shardIndex = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        ValidateShardIndex(shardIndex);

        var shardPath = ComputeShardPath(objectId);
        var basePath = Path.Combine(_options.BasePath, shardPath, objectId);

        // If shardIndex is provided, store in shards subdirectory
        if (shardIndex.HasValue)
        {
            return Path.Combine(basePath, "shards", shardIndex.Value.ToString());
        }

        return basePath;
    }

    public string GetTempPath(string objectId, int? shardIndex = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        ValidateShardIndex(shardIndex);

        var timestamp = DateTime.UtcNow.Ticks;
        var tempFileName = shardIndex.HasValue
            ? $"{objectId}.p{shardIndex.Value}_{timestamp}.uploading"
            : $"{objectId}_{timestamp}.uploading";

        return Path.Combine(_tempDirectory, tempFileName);
    }

    public string GetLockKey(string objectId, int? shardIndex = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        ValidateShardIndex(shardIndex);

        // Include shardIndex in lock key to allow concurrent uploads of different shards
        return shardIndex.HasValue ? $"{objectId}:{shardIndex.Value}" : objectId;
    }

    public string GetRelativePath(string objectId, int? shardIndex = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        ValidateShardIndex(shardIndex);

        var shardPath = ComputeShardPath(objectId);
        var basePath = Path.Combine(shardPath, objectId);

        // If shardIndex is provided, include shards subdirectory
        if (shardIndex.HasValue)
        {
            return Path.Combine(basePath, "shards", shardIndex.Value.ToString());
        }

        return basePath;
    }

    /// <summary>
    /// Computes the shard directory path for an objectId using SHA-256
    /// </summary>
    private string ComputeShardPath(string objectId)
    {
        // Compute SHA-256 hash of objectId
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(objectId));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Extract shard prefixes based on configuration
        var shardParts = new List<string>();
        int position = 0;

        for (int level = 0; level < _options.ShardLevelCount; level++)
        {
            if (position + _options.ShardSymbolCount > hashHex.Length)
                break;

            var shardPart = hashHex.Substring(position, _options.ShardSymbolCount);
            shardParts.Add(shardPart);
            position += _options.ShardSymbolCount;
        }

        // Combine shard parts into path
        return Path.Combine(shardParts.ToArray());
    }

    /// <summary>
    /// Validates that shardIndex is within the valid range (0-255)
    /// </summary>
    private static void ValidateShardIndex(int? shardIndex)
    {
        if (shardIndex.HasValue && (shardIndex.Value < 0 || shardIndex.Value > 255))
        {
            throw new ArgumentOutOfRangeException(nameof(shardIndex),
                "ShardIndex must be between 0 and 255 (inclusive)");
        }
    }
}
