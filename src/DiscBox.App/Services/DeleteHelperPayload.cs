namespace DiscBox.Services;

internal sealed class DeleteHelperRequest
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string DbPath { get; set; } = string.Empty;
    public bool Encrypt { get; set; }
    public string VirtualPath { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string ProgressPath { get; set; } = string.Empty;
}

internal sealed class DeleteHelperResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

internal sealed class DeleteHelperProgress
{
    public string CurrentPath { get; set; } = string.Empty;
    public long Done { get; set; }
    public long Total { get; set; }
    public int ChunkIndex { get; set; }
    public int ChunkCount { get; set; }
}
