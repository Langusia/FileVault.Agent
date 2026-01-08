using System.ComponentModel.DataAnnotations;

namespace DocMaster.API.Models;

/// <summary>
/// Request to register or update a node
/// </summary>
public class RegisterNodeRequest
{
    [Required]
    [MaxLength(100)]
    public string NodeId { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string GrpcAddress { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Datacenter { get; set; }

    [MaxLength(50)]
    public string Tier { get; set; } = "Hot";
}

/// <summary>
/// Node information response
/// </summary>
public class NodeDto
{
    public string NodeId { get; set; } = string.Empty;
    public string GrpcAddress { get; set; } = string.Empty;
    public string? Datacenter { get; set; }
    public string Tier { get; set; } = "Hot";
    public string Status { get; set; } = "Unknown";
    public DateTime? LastHeartbeatUtc { get; set; }

    // Metrics
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    public double? CpuUsagePercent { get; set; }
    public long? WorkingSetBytes { get; set; }
    public long? UptimeSeconds { get; set; }

    // Health
    public int ConsecutiveFailures { get; set; }

    // Audit
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    // Computed properties
    public double? UsedPercentage => TotalBytes.HasValue && TotalBytes.Value > 0
        ? ((TotalBytes.Value - (FreeBytes ?? 0)) / (double)TotalBytes.Value) * 100
        : null;
}

/// <summary>
/// List of nodes response
/// </summary>
public class NodesListResponse
{
    public List<NodeDto> Nodes { get; set; } = new();
    public int TotalCount { get; set; }
}
