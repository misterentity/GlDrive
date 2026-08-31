using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class UpdateMarkerKeyConcurrencyTests
{
    [Fact]
    public void Concurrent_cold_start_publishes_one_stable_key()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "gldrive-key-race-" + Guid.NewGuid().ToString("N"));
        var keyPath = Path.Combine(root, ".updating-key");
        Directory.CreateDirectory(root);

        try
        {
            const int callerCount = 32;
            using var start = new Barrier(callerCount + 1);
            var fingerprints = new ConcurrentBag<string>();
            var failures = new ConcurrentBag<Exception>();

            var callers = Enumerable.Range(0, callerCount).Select(_ => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    var key = UpdateMarkerHmac.GetOrCreateKeyAt(keyPath);
                    fingerprints.Add(Convert.ToHexString(SHA256.HashData(key)));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })).ToArray();

            foreach (var caller in callers) caller.Start();
            start.SignalAndWait();
            foreach (var caller in callers) Assert.True(caller.Join(TimeSpan.FromSeconds(10)));

            Assert.Empty(failures);
            Assert.Single(fingerprints.Distinct(StringComparer.Ordinal));
            Assert.DoesNotContain(Directory.EnumerateFiles(root),
                path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

            var persisted = UpdateMarkerHmac.GetOrCreateKeyAt(keyPath);
            Assert.Equal(fingerprints.First(),
                Convert.ToHexString(SHA256.HashData(persisted)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
