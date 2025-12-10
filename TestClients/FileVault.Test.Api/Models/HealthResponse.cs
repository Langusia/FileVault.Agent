namespace FileVault.Test.Api.Models;

public class HealthResponse
{
    public string NodeId { get; set; } = string.Empty;
    public bool IsAlive { get; set; }
    public long DataPathFreeBytes { get; set; }
    public long DataPathTotalBytes { get; set; }
    public double DataPathFreeGB => Math.Round(DataPathFreeBytes / 1024.0 / 1024.0 / 1024.0, 2);
    public double DataPathTotalGB => Math.Round(DataPathTotalBytes / 1024.0 / 1024.0 / 1024.0, 2);
    public double UsagePercent => DataPathTotalBytes > 0
        ? Math.Round((1 - (double)DataPathFreeBytes / DataPathTotalBytes) * 100, 2)
        : 0;
}
