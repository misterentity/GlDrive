using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace GlDrive.UI;

/// <summary>One renderer invocation owns its output and publishes evidence only after success.</summary>
internal sealed class RenderArtifactSession : IDisposable
{
    private readonly FileStream _claim;
    private readonly Assembly _assembly;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly List<Artifact> _files = [];
    private readonly object _gate = new();
    private bool _completed;
    private bool _disposed;
    private bool _failed;
    private bool _writing;
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string OutputDirectory { get; }

    internal RenderArtifactSession(string defaultRoot, string? outputDirectory = null, Assembly? assembly = null)
    {
        _assembly = assembly ?? typeof(App).Assembly;
        OutputDirectory = Path.GetFullPath(outputDirectory ?? Path.Combine(defaultRoot,
            $"run-{_startedUtc:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}-{RunId}"));
        if (Directory.Exists(OutputDirectory) && Directory.EnumerateFileSystemEntries(OutputDirectory).Any())
            throw new IOException("Render output must be a new or empty directory: " + OutputDirectory);
        Directory.CreateDirectory(OutputDirectory);
        _claim = new FileStream(Path.Combine(OutputDirectory, ".render-owner"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
    }

    internal void Write(string fileName, Action<Stream> write)
    {
        lock (_gate)
        {
            EnsureActive();
            try
            {
                if (_writing) throw new InvalidOperationException("A render write is already active.");
                if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(fileName) != fileName ||
                    fileName.EndsWith('.') || fileName.EndsWith(' ') ||
                    string.Equals(fileName, ".render-owner", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Expected a unique artifact filename.", nameof(fileName));
                _writing = true;
                var path = Path.Combine(OutputDirectory, fileName);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    write(stream);
                EnsureActive();
                using var input = File.OpenRead(path);
                _files.Add(new Artifact(fileName, input.Length, Convert.ToHexString(SHA256.HashData(input))));
            }
            catch
            {
                _failed = true;
                throw;
            }
            finally { _writing = false; }
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            EnsureActive();
            var heldFiles = new List<FileStream>();
            try
            {
                if (_writing) throw new InvalidOperationException("A render write is still active.");
                VerifyInventory();
                foreach (var file in _files)
                {
                    // Keep checked files unchanged through manifest publication on Windows.
                    var input = new FileStream(Path.Combine(OutputDirectory, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
                    heldFiles.Add(input);
                    if (input.Length != file.Size || Convert.ToHexString(SHA256.HashData(input)) != file.Sha256)
                        throw new IOException("A render artifact changed before completion: " + file.Name);
                }
                using var assemblyFile = File.OpenRead(_assembly.Location);
                var manifest = new
                {
                    RunId,
                    StartedUtc = _startedUtc,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    Status = "complete",
                    AssemblyName = _assembly.GetName().Name,
                    AssemblyVersion = _assembly.GetName().Version?.ToString(),
                    AssemblySha256 = Convert.ToHexString(SHA256.HashData(assemblyFile)),
                    Files = _files
                };
                var stagingName = ".manifest-" + RunId + ".tmp";
                var stagingPath = Path.Combine(OutputDirectory, stagingName);
                using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, manifest, new JsonSerializerOptions { WriteIndented = true });
                    output.Flush(flushToDisk: true);
                }
                VerifyInventory(stagingName);
                // Partial serialization never appears under the completed manifest name.
                File.Move(stagingPath, Path.Combine(OutputDirectory, "manifest.json"));
                _completed = true;
            }
            catch
            {
                _failed = true;
                throw;
            }
            finally { foreach (var input in heldFiles) input.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _claim.Dispose();
        }
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Render session is complete.");
        if (_failed) throw new InvalidOperationException("Render session failed; incomplete evidence is retained.");
    }

    private void VerifyInventory(string? stagingName = null)
    {
        var expected = _files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Add(".render-owner");
        if (stagingName != null) expected.Add(stagingName);
        foreach (var path in Directory.EnumerateFileSystemEntries(OutputDirectory))
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !expected.Remove(Path.GetFileName(path)))
                throw new IOException("Render output contains an unexpected entry.");
        }
        if (expected.Count != 0) throw new IOException("Render output is missing an owned artifact.");
    }

    private sealed record Artifact(string Name, long Size, string Sha256);
}
