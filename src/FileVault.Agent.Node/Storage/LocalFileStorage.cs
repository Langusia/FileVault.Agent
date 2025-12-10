using FileVault.Agent.Node.Interfaces;

namespace FileVault.Agent.Node.Storage;

/// <summary>
/// Local filesystem implementation of IFileStorage
/// </summary>
public class LocalFileStorage : IFileStorage
{
    public async Task WriteAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        await using var fileStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920, // 80KB buffer
            useAsync: true);

        await stream.CopyToAsync(fileStream, cancellationToken);
        await fileStream.FlushAsync(cancellationToken);
    }

    public Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920, // 80KB buffer
            useAsync: true);

        return Task.FromResult<Stream>(fileStream);
    }

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(path));
    }

    public Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        var fileInfo = new FileInfo(path);
        return Task.FromResult(fileInfo.Length);
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        // File.Move is atomic when source and destination are on the same filesystem
        File.Move(sourcePath, destinationPath, overwrite: false);
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return Task.CompletedTask;
    }
}
