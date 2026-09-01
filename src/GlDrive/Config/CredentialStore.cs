using Meziantou.Framework.Win32;
using Serilog;

namespace GlDrive.Config;

public static class CredentialStore
{
    /// <summary>
    /// Prevents isolated utility modes (currently screenshot rendering) from reading,
    /// writing, or deleting credentials belonging to the interactive production app.
    /// Screenshot mode runs in its own short-lived process, so this is deliberately
    /// process-wide rather than an ambient async scope.
    /// </summary>
    internal static bool AccessDisabled { get; set; }

    private static string GetTargetName(string host, int port, string username) =>
        $"GlDrive:{host}:{port}:{username}";

    private static string GetProxyTargetName(string host, int port, string username) =>
        $"GlDrive:proxy:{host}:{port}:{username}";

    public static string? GetProxyPassword(string host, int port, string username)
    {
        if (AccessDisabled) return null;
        var target = GetProxyTargetName(host, port, username);
        try
        {
            var cred = CredentialManager.ReadCredential(target);
            return cred?.Password;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read proxy credential");
            return null;
        }
    }

    public static void SaveProxyPassword(string host, int port, string username, string password)
    {
        if (AccessDisabled) return;
        var target = GetProxyTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(target, username, password, CredentialPersistence.Enterprise);
        }
        catch (Exception ex)
        {
            // Write failure is logged and swallowed (consistent with reads + SaveApiKey) so a locked Credential Manager doesn't crash first-run setup; a later GetProxyPassword==null surfaces as a normal auth failure.
            Log.Error(ex, "Failed to save proxy credential");
        }
    }

    public static string? GetPassword(string host, int port, string username)
    {
        if (AccessDisabled) return null;
        var target = GetTargetName(host, port, username);
        try
        {
            var cred = CredentialManager.ReadCredential(target);
            return cred?.Password;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read credential");
            return null;
        }
    }

    public static void SavePassword(string host, int port, string username, string password)
    {
        if (AccessDisabled) return;
        var target = GetTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(
                target,
                username,
                password,
                CredentialPersistence.Enterprise);
        }
        catch (Exception ex)
        {
            // Write failure is logged and swallowed (consistent with reads + SaveApiKey) so a locked Credential Manager doesn't crash first-run setup; a later GetPassword==null surfaces as a normal auth failure.
            Log.Error(ex, "Failed to save credential");
        }
    }

    public static void DeletePassword(string host, int port, string username)
    {
        if (AccessDisabled) return;
        var target = GetTargetName(host, port, username);
        try
        {
            CredentialManager.DeleteCredential(target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete credential");
        }
    }

    private static string GetIrcTargetName(string host, int port, string nick) =>
        $"GlDrive:irc:{host}:{port}:{nick}";

    public static string? GetIrcPassword(string host, int port, string nick)
    {
        if (AccessDisabled) return null;
        var target = GetIrcTargetName(host, port, nick);
        try
        {
            var cred = CredentialManager.ReadCredential(target);
            return cred?.Password;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read IRC credential");
            return null;
        }
    }

    public static void SaveIrcPassword(string host, int port, string nick, string password)
    {
        if (AccessDisabled) return;
        var target = GetIrcTargetName(host, port, nick);
        try
        {
            CredentialManager.WriteCredential(target, nick, password, CredentialPersistence.Enterprise);
        }
        catch (Exception ex)
        {
            // Write failure is logged and swallowed (consistent with reads + SaveApiKey) so a locked Credential Manager doesn't crash first-run setup; a later GetIrcPassword==null surfaces as a normal auth failure.
            Log.Error(ex, "Failed to save IRC credential");
        }
    }

    public static void DeleteIrcPassword(string host, int port, string nick)
    {
        if (AccessDisabled) return;
        var target = GetIrcTargetName(host, port, nick);
        try
        {
            CredentialManager.DeleteCredential(target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete IRC credential");
        }
    }

    // SSH credentials for glftpd installer

    private static string GetSshTargetName(string host, int port, string username) =>
        $"GlDrive:ssh:{host}:{port}:{username}";

    public static string? GetSshPassword(string host, int port, string username)
    {
        if (AccessDisabled) return null;
        var target = GetSshTargetName(host, port, username);
        try
        {
            var cred = CredentialManager.ReadCredential(target);
            return cred?.Password;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read SSH credential");
            return null;
        }
    }

    public static void SaveSshPassword(string host, int port, string username, string password)
    {
        if (AccessDisabled) return;
        var target = GetSshTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(target, username, password, CredentialPersistence.Enterprise);
        }
        catch (Exception ex)
        {
            // Write failure is logged and swallowed (consistent with reads + SaveApiKey) so a locked Credential Manager doesn't crash first-run setup; a later GetSshPassword==null surfaces as a normal auth failure.
            Log.Error(ex, "Failed to save SSH credential");
        }
    }

    // API keys stored securely in Credential Manager

    public static string? GetApiKey(string service)
    {
        if (AccessDisabled) return null;
        try
        {
            var cred = CredentialManager.ReadCredential($"GlDrive:api:{service}");
            return cred?.Password;
        }
        catch { return null; }
    }

    public static void SaveApiKey(string service, string key)
    {
        if (AccessDisabled) return;
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            CredentialManager.WriteCredential($"GlDrive:api:{service}", service, key, CredentialPersistence.Enterprise);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save API key for {Service}", service);
        }
    }

    public static void DeleteApiKey(string service)
    {
        if (AccessDisabled) return;
        try
        {
            CredentialManager.DeleteCredential($"GlDrive:api:{service}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete API key for {Service}", service);
        }
    }
}
