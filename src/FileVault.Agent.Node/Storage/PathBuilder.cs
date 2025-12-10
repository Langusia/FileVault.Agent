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

    public string GetFinalPath(string objectId, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        var shardPath = ComputeShardPath(objectId);
        var filename = BuildFilename(objectId, extension);
        return Path.Combine(_options.BasePath, shardPath, filename);
    }

    public string GetTempPath(string objectId, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        var timestamp = DateTime.UtcNow.Ticks;
        var filename = BuildFilename(objectId, extension);
        var tempFileName = $"{filename}_{timestamp}.uploading";
        return Path.Combine(_tempDirectory, tempFileName);
    }

    public string GetLockKey(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        return objectId;
    }

    public string GetRelativePath(string objectId, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("ObjectId cannot be null or whitespace", nameof(objectId));

        var shardPath = ComputeShardPath(objectId);
        var filename = BuildFilename(objectId, extension);
        return Path.Combine(shardPath, filename);
    }

    /// <summary>
    /// Builds a filename from objectId and optional extension
    /// </summary>
    private string BuildFilename(string objectId, string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return objectId;

        // Ensure extension starts with a dot
        var ext = extension.StartsWith('.') ? extension : $".{extension}";
        return $"{objectId}{ext}";
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
}
