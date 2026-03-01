using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public class FtpSearchService
{
    private readonly FtpOperations _ftp;
    private readonly NotificationConfig _notifConfig;

    public FtpSearchService(FtpOperations ftp, NotificationConfig notifConfig)
    {
        _ftp = ftp;
        _notifConfig = notifConfig;
    }

    public async Task<List<SearchResult>> Search(string keyword, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var watchPath = _notifConfig.WatchPath.TrimEnd('/');

        try
        {
            var categories = await _ftp.ListDirectory(watchPath, ct);
            var categoryDirs = categories.Where(i => i.Type == FtpObjectType.Directory).ToList();

            foreach (var cat in categoryDirs)
            {
                ct.ThrowIfCancellationRequested();

                var catPath = $"{watchPath}/{cat.Name}";
                FtpListItem[] releases;
                try
                {
                    releases = await _ftp.ListDirectory(catPath, ct);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to list {Category}, skipping", catPath);
                    continue;
                }

                foreach (var release in releases.Where(i => i.Type == FtpObjectType.Directory))
                {
                    if (release.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SearchResult
                        {
                            ReleaseName = release.Name,
                            Category = cat.Name,
                            RemotePath = release.FullName,
                            Size = release.Size,
                            Modified = release.Modified
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "FTP search failed for: {Keyword}", keyword);
        }

        return results;
    }

    public async Task<List<FtpListItem>> GetReleaseFiles(string remotePath, CancellationToken ct = default)
    {
        var items = await _ftp.ListDirectory(remotePath, ct);
        return items.Where(i => i.Type == FtpObjectType.File).ToList();
    }
}
