using System.IO;

namespace GlDrive.Ftp;

/// <summary>
/// A CPSV data-channel setup step (TCP connect to the backend, or the TLS handshake the
/// server initiates) did not finish within its own deadline. A transport failure, not a
/// cancellation: the data command may already be in flight, so the control channel still
/// owes a reply and the connection must not be reused.
/// </summary>
public sealed class DataChannelTimeoutException : IOException
{
    public DataChannelTimeoutException(string phase, TimeSpan deadline)
        : base($"{phase} not completed within {deadline.TotalSeconds:0.#}s")
    {
        Phase = phase;
        Deadline = deadline;
    }

    public string Phase { get; }
    public TimeSpan Deadline { get; }
}
