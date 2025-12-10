namespace FileVault.Test.Api.Models;

public class UploadResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FinalPath { get; set; }
    public long SizeBytes { get; set; }
    public string? Checksum { get; set; }
}
