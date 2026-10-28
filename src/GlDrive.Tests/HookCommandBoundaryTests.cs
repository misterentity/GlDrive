using System.IO;
using System.Text;
using GlDrive.Config;
using GlDrive.Services.Hooks;
using Xunit;

namespace GlDrive.Tests;

public class HookCommandBoundaryTests
{
    [Theory]
    [InlineData("Film.2026.1080p.WEB-GROUP")]
    [InlineData("\"; Write-Output injected; $(whoami) & | %PATH%")]
    [InlineData("line one\nline two\\trailing\\")]
    public void Event_values_are_data_and_never_arguments(string release)
    {
        var command = new HookCommandConfig
        {
            Id = "test", Events = ["download.complete"],
            Executable = @"C:\Trusted Tools\notify.exe", Arguments = ["--format", "literal \"quoted\" value", ""]
        };
        var data = new HookEvent("download.complete", release, "Movies", "1080p", "Completed", @"C:\Staging\Film");
        var start = HookCommand.Build(command, data);

        Assert.False(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Equal(command.Arguments, start.ArgumentList);
        Assert.Equal(release, start.Environment["GLDRIVE_RELEASE"]);
        Assert.Equal(data.LocalPath, start.Environment["GLDRIVE_LOCAL_PATH"]);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public void Environment_does_not_inherit_an_unrelated_secret()
    {
        const string key = "GLDRIVE_TEST_HOOK_PRIVATE_TOKEN";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "synthetic-token");
            var command = new HookCommandConfig { Id = "test", Events = ["nuke"], Executable = @"C:\Tools\notify.exe" };
            var start = HookCommand.Build(command, new("nuke", "Example", "", "", "Nuked"));
            Assert.False(start.Environment.ContainsKey(key));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    [Theory]
    [InlineData(@"C:\Staging\tool.exe", false)]
    [InlineData(@"C:\Staging\child\tool.exe", false)]
    [InlineData(@"C:\Staging-other\tool.exe", true)]
    [InlineData(@"C:\Tools\tool.exe", true)]
    [InlineData(@"C:\Tools\..\Staging\tool.exe", false)]
    public void Code_location_checks_use_directory_boundaries(string path, bool accepted)
    {
        Assert.Equal(accepted, HookCommand.IsOutsideRoots(path, [@"C:\Staging"]));
    }

    [Theory]
    [InlineData(@"D:\", @"D:\Tools\hook.exe", false)]
    [InlineData(@"D:\", @"C:\Tools\hook.exe", true)]
    [InlineData(@"\\storage\media\", @"\\storage\media\Scripts\hook.ps1", false)]
    [InlineData(@"\\storage\media", @"\\storage\media\Scripts\hook.ps1", false)]
    [InlineData(@"\\storage\media\", @"\\storage\media-other\hook.exe", true)]
    public void Volume_and_share_roots_exclude_all_descendant_code(string root, string path, bool accepted)
    {
        Assert.Equal(accepted, HookCommand.IsOutsideRoots(path, [root]));
    }

    [Fact]
    public void Library_movie_and_tv_roots_are_content_boundaries()
    {
        var config = new AppConfig { Library = new() { MovieRoot = @"D:\Movies", TvRoot = @"D:\TV" } };
        var roots = HookRunner.ContentRoots(config).ToArray();
        Assert.False(HookCommand.IsOutsideRoots(@"D:\Movies\Tools\hook.exe", roots));
        Assert.False(HookCommand.IsOutsideRoots(@"D:\TV\Scripts\hook.ps1", roots));
        Assert.True(HookCommand.IsOutsideRoots(@"C:\Trusted\hook.exe", roots));
    }

    public static IEnumerable<object[]> ReparseTags()
    {
        for (uint index = 0; index < 16; index++) yield return [0x9000001Au | index << 12, true];
        foreach (uint tag in new uint[] { 0xA000000Cu, 0xA0000003u, 0x8000001Bu, 0x9000001Bu, 0xB000001Au, 0 })
            yield return [tag, false];
    }

    [Theory]
    [MemberData(nameof(ReparseTags))]
    public void Only_cloud_reparse_family_is_permitted(uint tag, bool permitted)
    {
        var reason = HookCommand.UnsafeReparseReason(@"C:\Cloud\Scripts\hook.ps1",
            _ => (FileAttributes.ReparsePoint, tag));
        Assert.Equal(permitted, reason == null);
        if (!permitted) Assert.Contains($"0x{tag:X8}", reason);
    }

    [Fact]
    public void Cloud_script_does_not_bypass_a_redirecting_ancestor()
    {
        var visited = new List<string>();
        var reason = HookCommand.UnsafeReparseReason(@"C:\Cloud\Scripts\hook.ps1", path =>
        {
            visited.Add(path);
            return (FileAttributes.ReparsePoint, path == @"C:\Cloud" ? 0xA0000003u : 0x9000101Au);
        });
        Assert.Contains(@"C:\Cloud", visited);
        Assert.Contains("0xA0000003", reason);
    }

    [Fact]
    public void Native_metadata_reader_accepts_regular_temporary_script()
    {
        var path = Path.Combine(Path.GetTempPath(), "hook-metadata-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(path, "Write-Output 'synthetic'");
            Assert.Null(HookCommand.UnsafeReparseReason(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Capture_drains_large_output_and_retains_only_the_tail(int limit)
    {
        var text = new string('x', 100_000) + "THE-END";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var result = await HookCommand.CaptureAsync(reader, limit, CancellationToken.None);
        Assert.Equal(text[^limit..], result);
        Assert.Equal(0, await reader.ReadAsync(new char[1]));
    }
}
