namespace FileVault.Agent.Node.Configuration;

public class NodeAgentOptions
{
    public const string SectionName = "NodeAgent";

    /// <summary>
    /// Unique identifier for this node
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for this node
    /// </summary>
    public string NodeName { get; set; } = string.Empty;

    /// <summary>
    /// Base path for storage (e.g., "/mnt/192.168.1.10/2025")
    /// Already includes IP address and year volume
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Name of temporary directory for uploads (default: "tmp")
    /// </summary>
    public string TempDirName { get; set; } = "tmp";

    /// <summary>
    /// Maximum number of concurrent uploads allowed
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 16;

    /// <summary>
    /// Maximum number of concurrent downloads allowed
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 32;

    /// <summary>
    /// Size of chunks for streaming operations in bytes (default: 256KB)
    /// </summary>
    public int ChunkSizeBytes { get; set; } = 262144;

    /// <summary>
    /// Number of hex characters per shard level (default: 2)
    /// </summary>
    public int ShardSymbolCount { get; set; } = 2;

    /// <summary>
    /// Number of shard directory levels (default: 2)
    /// </summary>
    public int ShardLevelCount { get; set; } = 2;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NodeId))
            throw new InvalidOperationException("NodeId is required");

        if (string.IsNullOrWhiteSpace(NodeName))
            throw new InvalidOperationException("NodeName is required");

        if (string.IsNullOrWhiteSpace(BasePath))
            throw new InvalidOperationException("BasePath is required");

        if (!Directory.Exists(BasePath))
            throw new InvalidOperationException($"BasePath does not exist: {BasePath}");

        if (MaxConcurrentUploads <= 0)
            throw new InvalidOperationException("MaxConcurrentUploads must be positive");

        if (MaxConcurrentDownloads <= 0)
            throw new InvalidOperationException("MaxConcurrentDownloads must be positive");

        if (ChunkSizeBytes <= 0)
            throw new InvalidOperationException("ChunkSizeBytes must be positive");

        if (ShardSymbolCount <= 0)
            throw new InvalidOperationException("ShardSymbolCount must be positive");

        if (ShardLevelCount <= 0)
            throw new InvalidOperationException("ShardLevelCount must be positive");

        // Ensure temp directory exists
        var tempPath = Path.Combine(BasePath, TempDirName);
        Directory.CreateDirectory(tempPath);

        // Verify temp directory is on same filesystem as BasePath
        var basePathInfo = new DirectoryInfo(BasePath);
        var tempPathInfo = new DirectoryInfo(tempPath);

        // On Unix systems, check if both paths are on the same device
        // This is a best-effort check - File.Move will fail if not on same filesystem
        if (!OperatingSystem.IsWindows())
        {
            // The actual atomic move will verify this at runtime
            // We just ensure the directory exists here
        }
    }
}
