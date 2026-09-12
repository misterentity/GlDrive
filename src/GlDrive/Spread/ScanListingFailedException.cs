using System;

namespace GlDrive.Spread;

/// <summary>
/// A directory LIST that failed AFTER a pooled connection was borrowed for it.
///
/// The scan's borrow deadline and the deadlines inside the LIST (data TCP connect, data
/// TLS accept) both surface as <see cref="OperationCanceledException"/>, but they mean
/// opposite things: a borrow timeout never obtained a connection, while a LIST deadline
/// spent one — the connection is poisoned and the pool discards it. Until v3.10.116 the
/// caller and <see cref="ScanFailureClassifier"/> read both as "main pool exhausted",
/// and a poisoned discard could appear with no line naming its cause.
/// </summary>
internal sealed class ScanListingFailedException : Exception
{
    public string Path { get; }

    public ScanListingFailedException(string path, Exception inner)
        : base($"listing {path} failed after borrow: {inner.GetType().Name}: {inner.Message}", inner)
    {
        Path = path;
    }
}
