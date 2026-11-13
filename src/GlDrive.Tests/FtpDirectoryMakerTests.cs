using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class FtpDirectoryMakerTests
{
    /// <summary>A glftpd-like directory tree that answers CWD/MKD relative to its own CWD.</summary>
    private sealed class FakeSite
    {
        public readonly HashSet<string> Dirs = new(StringComparer.Ordinal) { "/" };
        public readonly Dictionary<string, FtpReply> MkdReplies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, FtpReply> CwdReplies = new(StringComparer.Ordinal);
        public readonly List<string> Commands = [];
        private string _cwd = "/";

        public FakeSite(params string[] dirs) { foreach (var d in dirs) Dirs.Add(d); }

        public Task<FtpReply> Execute(string command, CancellationToken ct)
        {
            Commands.Add(command);
            var space = command.IndexOf(' ');
            var verb = command[..space];
            var arg = command[(space + 1)..];
            var target = arg.StartsWith('/') ? "/" + arg.Trim('/') : (_cwd == "/" ? "" : _cwd) + "/" + arg;
            if (target.Length > 1) target = target.TrimEnd('/');
            switch (verb)
            {
                case "CWD":
                    if (CwdReplies.TryGetValue(target, out var cwdReply)) return Task.FromResult(cwdReply);
                    if (!Dirs.Contains(target)) return Reply("550", "No such file or directory.");
                    _cwd = target;
                    return Reply("250", "CWD command successful.");
                case "MKD":
                    if (MkdReplies.TryGetValue(target, out var scripted))
                    {
                        if (scripted.Success || (scripted.Message ?? "").Contains("exist", StringComparison.OrdinalIgnoreCase))
                            Dirs.Add(target);
                        return Task.FromResult(scripted);
                    }
                    if (Dirs.Contains(target)) return Reply("550", "File exists");
                    Dirs.Add(target);
                    return Reply("257", $"\"{target}\" created.");
                default:
                    return Reply("500", "Unknown command");
            }
        }

        private static Task<FtpReply> Reply(string code, string message) => Task.FromResult(new FtpReply { Code = code, Message = message });
    }

    private static Task<(bool Ok, string Code, string Message)> Ensure(FakeSite site, string path, List<string>? created = null)
        => FtpDirectoryMaker.EnsurePath(site.Execute, path, CancellationToken.None, created == null ? null : created.Add);

    [Fact]
    public async Task An_existing_directory_costs_one_CWD_and_creates_nothing()
    {
        var site = new FakeSite("/tv", "/tv/Show.S01E01-GRP");
        var created = new List<string>();

        var result = await Ensure(site, "/tv/Show.S01E01-GRP", created);

        Assert.Equal((true, "250", "exists (CWD)"), result);
        Assert.Equal(["CWD /tv/Show.S01E01-GRP"], site.Commands);
        Assert.Empty(created);
    }

    [Fact]
    public async Task A_257_create_is_reported_and_uses_a_relative_MKD()
    {
        var site = new FakeSite("/tv");
        var created = new List<string>();

        var result = await Ensure(site, "/tv/Show.S01E01-GRP", created);

        Assert.Equal((true, "257", "created"), result);
        Assert.Equal(["/tv/Show.S01E01-GRP"], created);
        Assert.Equal(["CWD /tv/Show.S01E01-GRP", "CWD /", "CWD tv", "CWD Show.S01E01-GRP", "MKD Show.S01E01-GRP", "CWD Show.S01E01-GRP"],
            site.Commands);
        Assert.DoesNotContain(site.Commands, c => c.StartsWith("MKD /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_550_missing_parent_walks_and_creates_each_component()
    {
        var site = new FakeSite("/mp3");
        var created = new List<string>();

        var result = await Ensure(site, "/mp3/1104/Artist-Album-WEB-2026-GRP/CD1", created);

        Assert.True(result.Ok);
        Assert.Equal(["/mp3/1104", "/mp3/1104/Artist-Album-WEB-2026-GRP", "/mp3/1104/Artist-Album-WEB-2026-GRP/CD1"], created);
        Assert.Contains("/mp3/1104/Artist-Album-WEB-2026-GRP/CD1", site.Dirs);
    }

    [Theory]
    [InlineData("521", "\"/tv/Show\" directory already exists")]
    [InlineData("550", "File exists")]
    public async Task A_concurrent_creator_is_navigable_but_never_counted_as_ours(string code, string message)
    {
        var site = new FakeSite("/tv");
        site.MkdReplies["/tv/Show"] = new FtpReply { Code = code, Message = message };
        var created = new List<string>();

        var result = await Ensure(site, "/tv/Show", created);

        Assert.Equal((true, "250", "exists"), result);
        Assert.Empty(created);
        Assert.Equal("CWD Show", site.Commands[^1]);
    }

    [Theory]
    [InlineData("553", "Permission denied")]
    [InlineData("550", "MKD Denied by dirscript.")]
    [InlineData("550", "Error: Not allowed to make directories here.")]
    public async Task A_denial_stops_the_walk_and_returns_the_server_reply(string code, string message)
    {
        var site = new FakeSite("/tv");
        site.MkdReplies["/tv/Show"] = new FtpReply { Code = code, Message = message };
        var created = new List<string>();

        var result = await Ensure(site, "/tv/Show/Sample", created);

        Assert.Equal((false, code, message), result);
        Assert.Empty(created);
        Assert.Equal("MKD Show", site.Commands[^1]);
    }

    [Fact]
    public async Task Paths_are_sanitized_before_any_command_is_sent()
    {
        var site = new FakeSite("/tv");

        await Ensure(site, "/tv/Show\r\nDELE /tv/other");

        Assert.All(site.Commands, c => Assert.DoesNotContain('\r', c));
        Assert.All(site.Commands, c => Assert.DoesNotContain('\n', c));
        Assert.DoesNotContain(site.Commands, c => c.StartsWith("DELE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_root_is_not_a_creatable_path()
    {
        var site = new FakeSite();
        site.Dirs.Remove("/");
        Assert.Equal((false, "ERR", "empty path"), await Ensure(site, "/"));
    }

    [Fact]
    public async Task Request_parent_must_still_exist_before_each_create_attempt()
    {
        const string parent = "/requests/REQUEST-Release";
        var site = new FakeSite("/requests", parent);
        var created = new List<string>();
        var first = await FtpDirectoryMaker.EnsurePath(site.Execute, parent + "/Release/CD1",
            CancellationToken.None, created.Add, existingParent: parent);
        Assert.True(first.Ok);
        Assert.Equal([parent + "/Release", parent + "/Release/CD1"], created);
        Assert.Equal("CWD " + parent, site.Commands[0]);
        Assert.DoesNotContain("MKD REQUEST-Release", site.Commands);

        // Another filler closes the request after discovery/earlier file delivery.
        site.Dirs.RemoveWhere(path => path.StartsWith(parent, StringComparison.Ordinal));
        site.Commands.Clear();
        created.Clear();
        var next = await FtpDirectoryMaker.EnsurePath(site.Execute, parent + "/Release/CD2",
            CancellationToken.None, created.Add, existingParent: parent);
        Assert.Equal((false, "550", "No such file or directory."), next);
        Assert.Equal(["CWD " + parent], site.Commands);
        Assert.Empty(created);
        Assert.DoesNotContain(parent, site.Dirs);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("150")]
    [InlineData("350")]
    [InlineData("550")]
    [InlineData("553")]
    [InlineData("5xx")]
    [InlineData("")]
    public async Task Required_parent_rejection_preserves_reply_without_fallback_or_MKD(string code)
    {
        const string parent = "/requests/REQUEST-Release";
        var site = new FakeSite("/requests", parent);
        site.CwdReplies[parent] = new() { Code = code, Message = "fixture parent rejection" };
        var result = await FtpDirectoryMaker.EnsurePath(site.Execute, parent + "/Release",
            CancellationToken.None, existingParent: parent);
        Assert.Equal((false, code, "fixture parent rejection"), result);
        Assert.Equal(["CWD " + parent], site.Commands);
    }

    [Fact]
    public async Task The_existing_root_can_bound_creation_without_being_recreated()
    {
        var site = new FakeSite();
        var result = await FtpDirectoryMaker.EnsurePath(site.Execute, "/Release/CD1", CancellationToken.None,
            existingParent: "/");
        Assert.True(result.Ok);
        Assert.Equal("CWD /", site.Commands[0]);
        Assert.Equal(["MKD Release", "MKD CD1"], site.Commands.Where(c => c.StartsWith("MKD ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("/requests/REQUEST-Release2/Release")]
    [InlineData("/requests/REQUEST-Release/../Closed/Release")]
    [InlineData("/requests/REQUEST-Release//Release")]
    [InlineData("/requests/REQUEST-Release/Release\r\nMKD x")]
    [InlineData("/requests/REQUEST-Release")]
    public async Task Protected_parent_rejects_non_descendant_paths_before_any_command(string fullPath)
    {
        var site = new FakeSite();
        var result = await FtpDirectoryMaker.EnsurePath(site.Execute, fullPath, CancellationToken.None,
            existingParent: "/requests/REQUEST-Release");
        Assert.False(result.Ok);
        Assert.Equal("ERR", result.Code);
        Assert.Empty(site.Commands);
    }

    [Theory]
    [InlineData("257", "\"/x\" created.", true)]
    [InlineData("250", "ok", true)]
    [InlineData("550", "File exists", true)]
    [InlineData("521", "Directory already exists", true)]
    [InlineData("550", "No such file or directory", false)]
    [InlineData("553", "Permission denied", false)]
    [InlineData("550", "MKD Denied by dirscript.", false)]
    public void IsMkdSuccess_accepts_2xx_and_already_exists_text(string code, string message, bool expected)
        => Assert.Equal(expected, FtpDirectoryMaker.IsMkdSuccess(new FtpReply { Code = code, Message = message }));
}
