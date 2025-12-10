namespace FileVault.Agent.Node.Interfaces;

/// <summary>
/// Abstraction for file storage operations
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Writes a stream to a file path
    /// </summary>
    Task WriteAsync(string path, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a file as a stream
    /// </summary>
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file if it exists
    /// </summary>
    Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the size of a file in bytes
    /// </summary>
    Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a file atomically (requires same filesystem)
    /// </summary>
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the directory for a file path exists
    /// </summary>
    Task EnsureDirectoryAsync(string filePath, CancellationToken cancellationToken = default);
}
