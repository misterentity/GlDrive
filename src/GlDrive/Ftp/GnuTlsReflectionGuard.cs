using System.Reflection;
using FluentFTP;
using FluentFTP.GnuTLS;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>
/// Startup self-check for the load-bearing private-member reflection that
/// <see cref="FtpConnectionPool.IsGnuTlsHealthy"/> and
/// <see cref="FtpConnectionPool.NeutralizeGnuTls"/> rely on to avoid native
/// GnuTLS crashes (gnutls_deinit/gnutls_bye on a corrupt session SEGVs the
/// process, bypassing every managed handler).
///
/// Those helpers reach into FluentFTP / FluentFTP.GnuTLS internals by string
/// name (m_customStream, m_socket, BaseStream, IsSessionUsable + its backing
/// field, _session/session). A package bump that renames any of these would
/// silently turn the crash-avoidance into a no-op — no compile error, no test
/// failure — and the native crashes we fought for months come straight back.
///
/// This guard resolves the SAME members once at startup and reports any that
/// vanished, so a broken upgrade fails loud instead of silent. If you change
/// the reflection in FtpConnectionpool, update <see cref="Resolve"/> to match —
/// they MUST stay in sync (that is the whole point).
///
/// Packages are also exact-version pinned in GlDrive.csproj; bumping them is a
/// deliberate act that must re-run this check (see the upgrade checklist there).
/// </summary>
internal static class GnuTlsReflectionGuard
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Result of resolving the reflected member map.</summary>
    internal sealed record ResolveResult(IReadOnlyList<string> Missing)
    {
        public bool Ok => Missing.Count == 0;
    }

    /// <summary>
    /// Resolve every reflected type and member the GnuTLS crash-avoidance helpers
    /// depend on. Returns the list of human-readable identifiers that could NOT be
    /// resolved (empty = healthy). Never throws.
    /// </summary>
    internal static ResolveResult Resolve()
    {
        var missing = new List<string>();

        try
        {
            // --- Types ---
            // FtpSocketStream: resolve by simple name from the FluentFTP assembly
            // (namespace has moved across versions; the helpers use instance
            // GetType(), so we match by name to stay version-agnostic).
            var ftpAsm = typeof(AsyncFtpClient).Assembly;
            var socketStreamType = FindType(ftpAsm, "FtpSocketStream");
            if (socketStreamType == null) missing.Add("FluentFTP.FtpSocketStream (type)");

            // GnuTlsStream is public (used as Config.CustomStream; the runtime type
            // of FtpSocketStream.m_customStream). Its assembly also holds the
            // internal GnuTlsInternalStream (the actual Read/Write stream).
            var gnuStreamType = typeof(GnuTlsStream);
            var gnuAsm = gnuStreamType.Assembly;
            var gnuInternalType = gnuAsm.GetType("FluentFTP.GnuTLS.GnuTlsInternalStream")
                                  ?? FindType(gnuAsm, "GnuTlsInternalStream");
            if (gnuInternalType == null) missing.Add("FluentFTP.GnuTLS.GnuTlsInternalStream (type)");

            // --- FtpSocketStream members (NeutralizeGnuTls nulls m_customStream +
            // closes m_socket — these two are the load-bearing crash-avoidance) ---
            if (socketStreamType != null)
            {
                if (socketStreamType.GetField("m_customStream", NonPublicInstance) == null)
                    missing.Add("FtpSocketStream.m_customStream (field)");
                if (socketStreamType.GetField("m_socket", NonPublicInstance) == null)
                    missing.Add("FtpSocketStream.m_socket (field)");
            }

            // --- GnuTlsStream.BaseStream: a FIELD (not a property!) holding the
            // GnuTlsInternalStream. The pre-v3.6 helpers wrongly used GetProperty
            // here, silently no-op'ing the IsSessionUsable protection — corrected
            // to GetField in IsGnuTlsHealthy/NeutralizeGnuTls. ---
            var baseStreamField = gnuStreamType.GetField("BaseStream", NonPublicInstance)
                ?? gnuStreamType.GetField("BaseStream", BindingFlags.Public | BindingFlags.Instance);
            if (baseStreamField == null)
                missing.Add("GnuTlsStream.BaseStream (field)");

            // --- GnuTlsInternalStream members ---
            if (gnuInternalType != null)
            {
                // IsSessionUsable: property OR its compiler backing field.
                var usableProp = gnuInternalType.GetProperty("IsSessionUsable");
                var usableField = gnuInternalType.GetField("<IsSessionUsable>k__BackingField", NonPublicInstance);
                if (usableProp == null && usableField == null)
                    missing.Add("GnuTlsInternalStream.IsSessionUsable (property or backing field)");

                // Managed session object: 'sess' (ClientSession). A null sess means
                // the session is gone. (The pre-v3.6 helper looked for an IntPtr
                // '_session'/'session' that never existed — corrected to 'sess'.)
                if (gnuInternalType.GetField("sess", NonPublicInstance) == null)
                    missing.Add("GnuTlsInternalStream.sess (field)");
            }
        }
        catch (Exception ex)
        {
            // A reflection failure here is itself a red flag — surface it.
            missing.Add($"reflection resolve threw: {ex.GetType().Name}: {ex.Message}");
        }

        return new ResolveResult(missing);
    }

    /// <summary>
    /// One-time startup self-check. Logs success at Information; on failure logs
    /// Fatal and invokes <paramref name="onBroken"/> (e.g. a MessageBox) with a
    /// human-readable summary. Per product decision the app KEEPS RUNNING after a
    /// loud warning (degraded crash-protection) rather than refusing to start —
    /// the user can read logs / downgrade the package.
    /// </summary>
    internal static void VerifyOrFail(Action<string>? onBroken = null)
    {
        ResolveResult result;
        try { result = Resolve(); }
        catch (Exception ex)
        {
            Log.Fatal(ex, "GnuTLS crash-guard self-check threw — native crash protection may be broken");
            onBroken?.Invoke("GlDrive's TLS crash-protection self-check failed to run. The app will keep "
                + "running but may be unstable. See the log for details and consider downgrading FluentFTP.");
            return;
        }

        if (result.Ok)
        {
            Log.Information("GnuTLS crash-guard: all {Count} reflected members resolved", ResolvedMemberCount);
            return;
        }

        var summary = string.Join("; ", result.Missing);
        Log.Fatal("GnuTLS crash-guard BROKEN — {Count} reflected member(s) missing: {Missing}. "
            + "A FluentFTP/GnuTLS package change likely renamed internals; IsGnuTlsHealthy/NeutralizeGnuTls "
            + "are now no-ops and native crash protection is DEGRADED. Pin the package back or update "
            + "GnuTlsReflectionGuard + the pool helpers to match.", result.Missing.Count, summary);
        onBroken?.Invoke("GlDrive's TLS crash-protection is broken after a library change "
            + $"({result.Missing.Count} internal member(s) missing). The app will keep running but may crash "
            + "natively under connection churn. See the log; downgrading FluentFTP restores protection.");
    }

    /// <summary>Number of distinct members the guard verifies — for the success log line.</summary>
    private const int ResolvedMemberCount = 6;

    private static Type? FindType(Assembly asm, string simpleName)
    {
        try
        {
            foreach (var t in asm.GetTypes())
                if (t.Name == simpleName) return t;
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var t in ex.Types)
                if (t?.Name == simpleName) return t;
        }
        catch { /* fall through */ }
        return null;
    }
}
