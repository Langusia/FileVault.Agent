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
    /// <param name="shardIndex">Optional shard index (0-255). If null, whole-object mode.</param>
    /// <returns>Full path to the final storage location</returns>
    string GetFinalPath(string objectId, int? shardIndex = null);

    /// <summary>
    /// Get a temporary path for uploading an object
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <param name="shardIndex">Optional shard index (0-255). If null, whole-object mode.</param>
    /// <returns>Full path to a temporary file location</returns>
    string GetTempPath(string objectId, int? shardIndex = null);

    /// <summary>
    /// Get the lock key for an object to prevent concurrent modifications
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <param name="shardIndex">Optional shard index (0-255). If null, whole-object mode.</param>
    /// <returns>Lock key string</returns>
    string GetLockKey(string objectId, int? shardIndex = null);

    /// <summary>
    /// Get the relative path from basePath for an object
    /// </summary>
    /// <param name="objectId">The object identifier</param>
    /// <param name="shardIndex">Optional shard index (0-255). If null, whole-object mode.</param>
    /// <returns>Relative path from basePath</returns>
    string GetRelativePath(string objectId, int? shardIndex = null);
}
