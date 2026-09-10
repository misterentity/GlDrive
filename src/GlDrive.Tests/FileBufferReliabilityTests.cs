using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlDrive.Filesystem;
using Xunit;

namespace GlDrive.Tests;

public class FileBufferReliabilityTests
{
    [Fact]
    public void Partial_write_without_a_prior_read_preserves_untouched_bytes()
    {
        using var node = new FileNode("/file") { FileSize = 6 };
        var calls = 0;
        var stream = node.PrepareWriteStream(() => { calls++; return "abcdef"u8.ToArray(); });
        stream.Position = 2;
        stream.WriteByte((byte)'X');
        node.PrepareWriteStream(() => throw new Exception("Must not download twice"));
        Assert.Equal("abXdef", ReadBuffer(node));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Resize_preserves_existing_prefix()
    {
        using var node = new FileNode("/file") { FileSize = 6 };
        node.PrepareWriteStream(() => "abcdef"u8.ToArray()).SetLength(3);
        Assert.Equal("abc", ReadBuffer(node));
    }

    [Fact]
    public void Failed_initial_read_can_be_retried_without_publishing_an_empty_buffer()
    {
        using var node = new FileNode("/file") { FileSize = 6 };
        Assert.Throws<IOException>(() => node.PrepareWriteStream(() => throw new IOException("offline")));
        Assert.Null(node.WriteBuffer);
        Assert.Null(node.WriteBufferFile);
        node.PrepareWriteStream(() => "abcdef"u8.ToArray());
        Assert.Equal("abcdef", ReadBuffer(node));
    }

    [Fact]
    public void Existing_read_buffer_is_reused_without_network_fetch()
    {
        using var node = new FileNode("/file")
        {
            FileSize = 6, ReadBufferLoaded = true,
            ReadBuffer = new MemoryStream("abcdef"u8.ToArray())
        };
        node.ReadBuffer.Position = 3;
        node.PrepareWriteStream(() => throw new Exception("Already read"));
        Assert.Equal(3, node.ReadBuffer.Position);
        Assert.Equal("abcdef", ReadBuffer(node));
    }

    [Fact]
    public void Growing_write_spills_before_allocation_and_preserves_position()
    {
        using var node = new FileNode("/file") { SpillThresholdBytes = 8 };
        var stream = node.GetOrCreateWriteStream();
        stream.Write("abcdef"u8);
        stream.Position = 2;
        var spilled = node.GetOrCreateWriteStream(requiredLength: 100);
        Assert.IsType<FileStream>(spilled);
        Assert.Null(node.WriteBuffer);
        Assert.Equal(2, spilled.Position);
        spilled.WriteByte((byte)'X');
        Assert.Equal("abXdef", ReadBuffer(node));
    }

    [Fact]
    public void Read_after_local_write_uses_buffer_without_contacting_ftp()
    {
        var fs = new GlDriveFileSystem(null!, new DirectoryCache(), "/", "test");
        using var node = new FileNode("/file") { WriteBuffer = new MemoryStream("local"u8.ToArray()), FileSize = 5 };
        var ptr = Marshal.AllocHGlobal(5);
        try
        {
            Assert.Equal(0, fs.Read(node, node, ptr, 0, 5, out var count));
            Assert.Equal(5u, count);
            var bytes = new byte[5];
            Marshal.Copy(ptr, bytes, 0, 5);
            Assert.Equal("local", Encoding.UTF8.GetString(bytes));
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static string ReadBuffer(FileNode node)
    {
        using var reader = new StreamReader(node.GetWriteStreamForUpload()!, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
