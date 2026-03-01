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
            Log.Warning(ex, "Failed to read proxy credential for {Target}", target);
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
            Log.Error(ex, "Failed to save proxy credential for {Target}", target);
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
            Log.Warning(ex, "Failed to read credential for {Target}", target);
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
            Log.Error(ex, "Failed to save credential for {Target}", target);
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
            Log.Warning(ex, "Failed to delete credential for {Target}", target);
        }
    }
}
