using Meziantou.Framework.Win32;
using Serilog;

namespace GlDrive.Config;

public static class CredentialStore
{
    private static string GetTargetName(string host, int port, string username) =>
        $"GlDrive:{host}:{port}:{username}";

    private static string GetProxyTargetName(string host, int port, string username) =>
        $"GlDrive:proxy:{host}:{port}:{username}";

    public static string? GetProxyPassword(string host, int port, string username)
    {
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
        var target = GetProxyTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(target, username, password, CredentialPersistence.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save proxy credential");
            throw;
        }
    }

    public static string? GetPassword(string host, int port, string username)
    {
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
        var target = GetTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(
                target,
                username,
                password,
                CredentialPersistence.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save credential");
            throw;
        }
    }

    public static void DeletePassword(string host, int port, string username)
    {
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
        var target = GetIrcTargetName(host, port, nick);
        try
        {
            CredentialManager.WriteCredential(target, nick, password, CredentialPersistence.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save IRC credential");
            throw;
        }
    }

    public static void DeleteIrcPassword(string host, int port, string nick)
    {
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
        var target = GetSshTargetName(host, port, username);
        try
        {
            CredentialManager.WriteCredential(target, username, password, CredentialPersistence.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save SSH credential");
            throw;
        }
    }

    // API keys stored securely in Credential Manager

    public static string? GetApiKey(string service)
    {
        try
        {
            var cred = CredentialManager.ReadCredential($"GlDrive:api:{service}");
            return cred?.Password;
        }
        catch { return null; }
    }

    public static void SaveApiKey(string service, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            CredentialManager.WriteCredential($"GlDrive:api:{service}", service, key, CredentialPersistence.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save API key for {Service}", service);
        }
    }
}
