using System.ComponentModel.DataAnnotations;

namespace DocMaster.API.Entities;

/// <summary>
/// Represents a storage node in the FileVault cluster
/// </summary>
public class NodeEntity
{
    /// <summary>
    /// Unique identifier for the node
    /// </summary>
    [Key]
    [MaxLength(100)]
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// gRPC address for the node agent (e.g., http://10.0.0.12:5001)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string GrpcAddress { get; set; } = string.Empty;

    /// <summary>
    /// Physical datacenter location (optional)
    /// </summary>
    [MaxLength(100)]
    public string? Datacenter { get; set; }

    /// <summary>
    /// Storage tier (Hot, Warm, Cold, Archive)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Tier { get; set; } = "Hot";

    /// <summary>
    /// Current node status (Unknown, Online, Offline, Draining)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// Last successful heartbeat timestamp
    /// </summary>
    public DateTime? LastHeartbeatUtc { get; set; }

    // Metrics
    /// <summary>
    /// Free bytes available on node storage
    /// </summary>
    public long? FreeBytes { get; set; }

    /// <summary>
    /// Total bytes of node storage capacity
    /// </summary>
    public long? TotalBytes { get; set; }

    /// <summary>
    /// CPU usage percentage (0-100)
    /// </summary>
    public double? CpuUsagePercent { get; set; }

    /// <summary>
    /// Working set memory in bytes
    /// </summary>
    public long? WorkingSetBytes { get; set; }

    /// <summary>
    /// Node uptime in seconds
    /// </summary>
    public long? UptimeSeconds { get; set; }

    // Health tracking
    /// <summary>
    /// Number of consecutive health check failures
    /// </summary>
    public int ConsecutiveFailures { get; set; } = 0;

    // Audit fields
    /// <summary>
    /// Timestamp when node was first registered
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp of last update
    /// </summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
