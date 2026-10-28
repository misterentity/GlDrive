using System.IO;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Tls;

/// <summary>Checks default admission on a separate synthetic account over real native FTPS.</summary>
internal static class NativeLoginBudgetCheck
{
    internal static async Task Run(ConnectionConfig connection, CertificateManager certs, string root, CancellationToken ct)
    {
        var config = new ServerConfig
        {
            Connection = new() { Host = connection.Host, Port = connection.Port, Username = "login-budget-check" }
        };
        var account = FtpLoginAccount.From(config.Connection);
        var factory = new FtpClientFactory(config, certs);
        var authenticationLog = Path.Combine(root, "authentication-attempts");
        int Authentications() => File.Exists(authenticationLog) ? File.ReadAllLines(authenticationLog).Length : 0;
        var before = Authentications();
        await using var retained = new FtpConnectionPool(factory, 1);
        await retained.Initialize(ct);
        for (var index = 1; index < 7; index++)
        {
            await using var next = new FtpConnectionPool(factory, 1);
            await next.Initialize(ct);
        }
        var snapshot = GuardedFtpLogin.GetLoginBudget(account);
        Check(snapshot.Enabled && snapshot.Limit == 8 && snapshot.AttemptsInWindow == 7 &&
              snapshot.ReservedGhostSlots == 1 && snapshot.PersistenceHealthy && Authentications() == before + 7,
            "default login budget admits seven native authentications and reserves the ghost slot");

        await using var denied = new FtpConnectionPool(factory, 1);
        FtpLoginHeldException? hold = null;
        try { await denied.Initialize(ct); }
        catch (FtpLoginHeldException ex) { hold = ex; }
        Check(hold is { LoginRateHold: true, AuthenticationHold: false, RetryAtUtc: not null } &&
              Authentications() == before + 7 && denied.TotalCreated == 0,
            "default login budget denies the eighth native pool before authentication without leaking a slot");
        await using var lease = await retained.Borrow(ct);
        Check((await lease.Client.Execute("NOOP", ct)).Success && retained.TotalCreated == 1 &&
              GuardedFtpLogin.GetLoginBudget(account).AttemptsInWindow == 7 && Authentications() == before + 7,
            "a held login budget still permits existing native session reuse without another authentication");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
