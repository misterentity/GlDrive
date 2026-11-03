using System.IO;
using System.Net;

namespace GlDrive.Ftp;

/// <summary>
/// The CPSV data peer never sent a TLS ClientHello: it stays TLS server (ioFTPD), so a
/// CPSV data channel to it can never complete. A transport failure like any other stalled
/// data channel; <see cref="SwitchedToPasv"/> says whether this observation moved the site
/// to standard PASV data connections (see CpsvDataHelper.NegotiateDataTls).
/// </summary>
public sealed class CpsvPeerIsTlsServerException : IOException
{
    public CpsvPeerIsTlsServerException(string? controlHost, IPAddress? dataPeer, bool switchedToPasv)
        : base($"CPSV data peer {dataPeer?.ToString() ?? "?"} sent no TLS hello (it stays TLS server)" +
               (switchedToPasv ? $"; {controlHost} now uses standard PASV" : ""))
    {
        ControlHost = controlHost;
        SwitchedToPasv = switchedToPasv;
    }

    public string? ControlHost { get; }
    public bool SwitchedToPasv { get; }
}
