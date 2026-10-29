using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlDrive.Services.Statistics;
using Xunit;

namespace GlDrive.Tests;

public sealed class BoundedTelemetryReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string Row(string value = "ok") => JsonSerializer.Serialize(new { ts = Now.ToString("O"), value });

    [Fact]
    public void Raw_and_gzip_rotation_twins_are_counted_once()
    {
        using var files = new TelemetryFixture();
        files.Raw("races", Row()); files.Gzip("races", Row("compressed"));
        var reader = files.Reader(); var values = new List<string>();
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), row => values.Add(row.GetProperty("value").GetString()!));
        Assert.Equal(["ok"], values); Assert.Equal(1, reader.Coverage.FilesRead);
    }

    [Fact]
    public void Invalid_and_oversized_rows_do_not_discard_later_valid_rows()
    {
        using var files = new TelemetryFixture();
        files.Raw("races", "{bad}\n" + Row(new string('x', 500)) + "\n" + Row());
        var reader = files.Reader(new(MaxLineBytes: 128)); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        Assert.Equal(1, count); Assert.Equal(1, reader.Coverage.InvalidRows);
        Assert.Equal(1, reader.Coverage.OversizedRows); Assert.True(reader.Coverage.Partial);
    }

    [Fact]
    public void Row_cap_is_exact_even_for_many_lines_in_one_read_block()
    {
        using var files = new TelemetryFixture();
        files.Raw("races", string.Join('\n', Enumerable.Repeat(Row(), 20)));
        var reader = files.Reader(new(MaxRows: 1)); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        Assert.Equal(1, count); Assert.Equal(1, reader.Coverage.RowsRead);
        Assert.Contains("row-limit", reader.Coverage.Limits);
    }

    [Fact]
    public void Decompressed_bytes_are_bounded_and_shared_by_all_streams()
    {
        using var files = new TelemetryFixture();
        files.Gzip("races", string.Join('\n', Enumerable.Repeat(Row(new string('a', 200)), 500)));
        files.Raw("downloads", Row());
        var reader = files.Reader(new(MaxBytes: 1024)); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        var before = count;
        reader.ReadStream("downloads", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        Assert.Equal(before, count); Assert.Equal(1024, reader.Coverage.BytesRead);
        Assert.Contains("byte-limit", reader.Coverage.Limits);
    }

    [Fact]
    public void A_corrupt_raw_file_does_not_fall_back_and_duplicate_its_valid_prefix()
    {
        using var files = new TelemetryFixture(); files.Raw("races", Row() + "\n{broken"); files.Gzip("races", Row());
        var reader = files.Reader(); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        Assert.Equal(1, count); Assert.True(reader.Coverage.Partial);
    }

    [Fact]
    public void Callback_time_counts_towards_the_global_deadline()
    {
        using var files = new TelemetryFixture(); files.Raw("races", Row() + "\n" + Row());
        var reader = files.Reader(new(MaxMilliseconds: 100)); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => { count++; Thread.Sleep(120); });
        Assert.InRange(count, 0, 1); Assert.Contains("time-limit", reader.Coverage.Limits);
    }

    [Fact]
    public void Cancelled_reader_does_not_open_any_file()
    {
        using var files = new TelemetryFixture(); files.Raw("races", Row()); var reader = files.Reader();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadStream("races", Now.AddDays(-1), Now, _ => { }, new CancellationToken(true)));
        Assert.Equal(0, reader.Coverage.FilesRead);
    }

    [Theory]
    [InlineData("../races")]
    [InlineData("races\\other")]
    [InlineData("")]
    public void Reader_rejects_non_stream_paths(string prefix)
    {
        using var files = new TelemetryFixture();
        Assert.Throws<ArgumentException>(() => files.Reader().ReadStream(prefix, Now.AddDays(-1), Now, _ => { }));
    }

    [Fact]
    public void Reader_applies_timestamp_window_and_rejects_invalid_required_timestamp()
    {
        using var files = new TelemetryFixture();
        files.Raw("races", Row() + "\n{\"ts\":4}\n" + JsonSerializer.Serialize(new { ts = Now.AddDays(-2).ToString("O") }));
        var reader = files.Reader(); var count = 0;
        reader.ReadStream("races", Now.AddDays(-1), Now.AddMinutes(1), _ => count++);
        Assert.Equal(1, count); Assert.Equal(1, reader.Coverage.InvalidRows);
    }

    internal sealed class TelemetryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gldrive-stats-" + Guid.NewGuid().ToString("N"));
        public TelemetryFixture() => Directory.CreateDirectory(Path.Combine(Root, "ai-data"));
        private string PathFor(string prefix) => Path.Combine(Root, "ai-data", $"{prefix}-{Now.LocalDateTime:yyyyMMdd}.jsonl");
        public void Raw(string prefix, string text) => File.WriteAllText(PathFor(prefix), text, new UTF8Encoding(false));
        public void Gzip(string prefix, string text)
        {
            using var output = File.Create(PathFor(prefix) + ".gz");
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        public BoundedTelemetryReader Reader(TelemetryReadLimits? limits = null) => new(Path.Combine(Root, "ai-data"), limits ?? new());
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
