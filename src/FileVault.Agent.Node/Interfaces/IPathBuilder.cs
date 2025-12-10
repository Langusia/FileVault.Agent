namespace FileVault.Agent.Node.Interfaces;

/// <summary>
/// Provides deterministic path mapping for object storage
/// </summary>
public interface IPathBuilder
{
    /// <summary>
    /// Get the deterministic final storage path for an object
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <returns>Full path to the final storage location</returns>
    string GetFinalPath(string objectId);

    /// <summary>
    /// Get a temporary path for uploading an object
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <returns>Full path to a temporary file location</returns>
    string GetTempPath(string objectId);

    /// <summary>
    /// Get the lock key for an object to prevent concurrent modifications
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <returns>Lock key string</returns>
    string GetLockKey(string objectId);

    /// <summary>
    /// Get the relative path from basePath for an object
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <returns>Relative path from basePath</returns>
    string GetRelativePath(string objectId);
}
