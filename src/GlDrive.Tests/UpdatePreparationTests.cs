using System.IO;
using System.Net;
using System.Net.Http;
using GlDrive.Services;
using GlDrive.Services.Recovery;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpdatePreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-update-preparation-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri AssetUrl = new("https://example.invalid/update.zip");

    public UpdatePreparationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Timed_out_segmented_preparation_releases_recovery_admission_without_fallback()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Canceled preparation must not start fallback"));
        using var http = new HttpClient(handler);
        using var deadline = UpdateChecker.CreatePreparationCancellation(CancellationToken.None, TimeSpan.FromSeconds(1));
        var admission = new UpdateRecoveryAdmission();
        using (var install = admission.TryBeginInstall())
        {
            Assert.NotNull(install);
            var download = UpdateChecker.DownloadAssetAsync(http, AssetUrl, Path.Combine(_root, "update.zip"),
                (_, _, token) => Task.Delay(Timeout.Infinite, token), deadline.Token);
            Assert.False(admission.TryStopForRestart());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(admission.TryStopForRestart());
        Assert.Equal(0, handler.Requests);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Timed_out_fallback_body_is_canceled_and_closed_instead_of_holding_safe_mode()
    {
        using var body = new StalledStream();
        using var handler = new Handler(_ => new StreamContent(body));
        using var http = new HttpClient(handler);
        using var deadline = UpdateChecker.CreatePreparationCancellation(CancellationToken.None, TimeSpan.FromSeconds(1));
        var admission = new UpdateRecoveryAdmission();
        using (var install = admission.TryBeginInstall())
        {
            Assert.NotNull(install);
            var download = UpdateChecker.DownloadAssetAsync(http, AssetUrl, Path.Combine(_root, "update.zip"),
                (_, _, _) => Task.FromException(new IOException("synthetic range failure")), deadline.Token);
            await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(admission.TryStopForRestart());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(body.WasDisposed);
        Assert.True(admission.TryStopForRestart());
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signature_and_checksum_response_bodies_share_the_preparation_deadline(bool stallSignature)
    {
        using var body = new StalledStream();
        using var handler = new Handler(request => request == 1 && stallSignature
            ? new StringContent("synthetic checksum content") : new StreamContent(body));
        using var http = new HttpClient(handler);
        using var deadline = UpdateChecker.CreatePreparationCancellation(CancellationToken.None, TimeSpan.FromSeconds(1));
        var read = UpdateChecker.ReadSignedChecksumsAsync(http, "https://example.invalid/checksums.sha256",
            "https://example.invalid/checksums.sha256.sig", deadline.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(stallSignature ? 2 : 1, handler.Requests);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public void Only_safe_mode_sets_a_deadline_and_cancellation_ends_before_the_installer_handoff()
    {
        var updater = Source("Services/UpdateChecker.cs");
        var prepare = LifecycleSourceGuard.BlockAfter(updater, "public async Task DownloadAndInstallAsync");
        Assert.Contains("CreatePreparationCancellation(cancellationToken, PreparationTimeout)", prepare);
        Assert.Contains("SegmentedHttpDownloader.DownloadAsync(url, path, null, token)", prepare);
        Assert.Contains("VerifyDownloadHash(tempZip, release, preparationToken)", prepare);
        var marker = prepare.IndexOf("var attemptPath = UpdateStatePath", StringComparison.Ordinal);
        var handoff = prepare.IndexOf("LaunchUpdater(", StringComparison.Ordinal);
        Assert.True(marker > 0 && handoff > marker);
        Assert.DoesNotContain("ThrowIfCancellationRequested", prepare[marker..]);
        Assert.Contains("preparationToken.ThrowIfCancellationRequested();", prepare[..marker]);
        var verify = LifecycleSourceGuard.BlockAfter(updater, "private async Task<VerifiedUpdatePackage?> VerifyDownloadHash");
        Assert.Contains("ReadSignedChecksumsAsync(_http,", verify);
        Assert.Contains("SHA256.HashDataAsync(fs, cancellationToken)", verify);
        Assert.Contains("when (!cancellationToken.IsCancellationRequested)", verify);
        Assert.Contains("PreparationTimeout = TimeSpan.FromMinutes(10)", Source("UI/SafeModeWindow.xaml.cs"));
        Assert.DoesNotContain("PreparationTimeout", Source("UI/TrayViewModel.cs"));
    }

    private static string Source(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GlDrive.sln"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GlDrive", relative));
    }

    private sealed class Handler(Func<int, HttpContent> content) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(++Requests), RequestMessage = request });
    }

    private sealed class StalledStream : Stream
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
