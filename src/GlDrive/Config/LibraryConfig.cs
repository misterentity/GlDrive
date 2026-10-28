namespace GlDrive.Config;

/// <summary>Explicit imports of completed, staged media. No automatic download or library mutation.</summary>
public sealed class LibraryConfig
{
    public bool Enabled { get; set; }
    // First enable also forces this true in the shared configuration save path.
    public bool DryRun { get; set; } = true;
    public string StagingRoot { get; set; } = "";
    public string MovieRoot { get; set; } = "";
    public string TvRoot { get; set; } = "";
    public bool ScanPlexAfterImport { get; set; }
    // Large media uses size verification with exclusive write/delete protection;
    // small files additionally use SHA-256 without an expensive full-media reread.
    public long HashVerifyMaxBytes { get; set; } = 64L * 1024 * 1024;
}
