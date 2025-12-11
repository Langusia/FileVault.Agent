namespace FileVault.Test.Api.Configuration;

public class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>
    /// Size of chunks to use when streaming uploads to gRPC (default 1MB)
    /// </summary>
    public int ChunkSizeBytes { get; set; } = 1024 * 1024;
}
