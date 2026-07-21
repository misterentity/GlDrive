using System;
using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Player;
using GlDrive.Services;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public class ReviewRegressionTests
{
    [Theory]
    [InlineData("GlDrive-v3.10.24-win-x64.zip", 3, 10, 24)]
    [InlineData("gldrive-v4.0.0-win-x64.zip", 4, 0, 0)]
    public void Update_asset_versions_are_parsed_from_the_signed_name(
        string assetName, int major, int minor, int build)
    {
        var version = Assert.IsType<Version>(UpdateChecker.ParseUpdateAssetVersion(assetName));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("GlDrive-win-x64.zip")]
    [InlineData("GlDrive-v3.10.24-linux-x64.zip")]
    public void Invalid_update_asset_names_are_rejected(string assetName)
    {
        Assert.Null(UpdateChecker.ParseUpdateAssetVersion(assetName));
    }

    [Theory]
    [InlineData("bytes=10-19", 100, 10, 19, 10)]
    [InlineData("bytes=10-", 100, 10, 99, 90)]
    [InlineData("bytes=-10", 100, 90, 99, 10)]
    [InlineData("bytes=0-999", 100, 0, 99, 100)]
    public void Media_ranges_are_validated_and_bounded(
        string header, long size, long expectedStart, long expectedEnd, long expectedLength)
    {
        Assert.True(MediaStreamServer.TryResolveRange(header, size, out var range));
        Assert.True(range.IsPartial);
        Assert.Equal(expectedStart, range.Offset);
        Assert.Equal(expectedEnd, range.End);
        Assert.Equal(expectedLength, range.Length);
    }

    [Theory]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=20-10", 100)]
    [InlineData("bytes=abc-def", 100)]
    [InlineData("bytes=0-1,4-5", 100)]
    public void Media_ranges_reject_invalid_requests(string header, long size)
    {
        Assert.False(MediaStreamServer.TryResolveRange(header, size, out _));
    }

    [Fact]
    public void Update_authorization_is_bound_to_staged_file_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-auth-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(root, "staging");
        var marker = Path.Combine(root, "auth");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "GlDrive.exe"), "original");

        try
        {
            UpdateMarkerHmac.WriteAuthorization(marker, 4242, staging, install);
            Assert.True(UpdateMarkerHmac.IsValidAuthorization(marker, 4242, staging, install));

            File.WriteAllText(Path.Combine(staging, "GlDrive.exe"), "tampered");
            Assert.False(UpdateMarkerHmac.IsValidAuthorization(marker, 4242, staging, install));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Certificate_managers_share_mutations_for_the_same_store()
    {
        var fileName = "trusted-certs-test-" + Guid.NewGuid().ToString("N") + ".json";
        var path = Path.Combine(ConfigManager.AppDataPath, fileName);
        try
        {
            var first = new CertificateManager(fileName);
            var second = new CertificateManager(fileName);
            first.TrustCertificate("example.test:21", "ABC123");

            Assert.True(second.GetTrustedCertificates().ContainsKey("example.test:21"));
            second.RemoveTrustedCertificate("example.test:21");
            Assert.False(first.GetTrustedCertificates().ContainsKey("example.test:21"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Interrupted_extraction_returns_to_the_queue()
    {
        var serverId = "recovery-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ConfigManager.AppDataPath, $"downloads-{serverId}.json");
        var writer = new DownloadStore(serverId);
        var reader = new DownloadStore(serverId);
        try
        {
            writer.Add(new DownloadItem { Status = DownloadStatus.Extracting });
            writer.Flush();

            reader.Load();
            Assert.Equal(DownloadStatus.Queued, Assert.Single(reader.Items).Status);
        }
        finally
        {
            reader.Flush();
            File.Delete(path);
        }
    }
}
