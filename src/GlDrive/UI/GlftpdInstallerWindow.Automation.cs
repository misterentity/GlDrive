using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Tls;
using Renci.SshNet;

namespace GlDrive.UI;

public partial class GlftpdInstallerWindow
{
    private CancellationTokenSource? _installationCancellation;
    private bool _automationConnecting;
    private readonly HashSet<string> _installerKnownSecrets = new(StringComparer.Ordinal);

    internal async Task<object?> AutomationExecute(string operation, JsonElement args, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "installer.state": return AutomationState();
            case "installer.profile_get": return AutomationProfile(AutomationService.Flag(args, "includeSecrets"));
            case "installer.profile_set":
                RequireInstallerIdle();
                if (!args.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("'profile' must be an object.");
                AutomationApplyProfile(profile);
                return AutomationProfile(false);
            case "installer.connect":
                RequireInstallerIdle();
                return await AutomationConnect(AutomationService.Optional(args, "expectedHostKey", 128), ct);
            case "installer.disconnect":
                RequireInstallerIdle();
                Disconnect();
                return AutomationState();
            case "installer.start":
                RequireInstallerIdle();
                if (_ssh?.IsConnected != true || _sftp?.IsConnected != true)
                    throw new InvalidOperationException("Connect with installer.connect first.");
                // The underlying installer prompts interactively when this directory exists.
                // Refuse that case rather than feeding guessed answers to a destructive prompt.
                await AutomationCommand("test ! -e /glftpd", ct, "An existing /glftpd installation needs interactive maintenance; unattended installation requires a fresh destination.");
                await RunInstallationAsync(interactive: false, ct);
                return AutomationState();
            case "installer.cancel":
                if (_installationCancellation == null) throw new InvalidOperationException("No installation is running.");
                _installationCancellation.Cancel();
                return new { cancellationRequested = true, note = "Remote effects already applied are not rolled back; the SSH server must support process signals to terminate child processes." };
            case "installer.logs":
            {
                var lines = LogOutput.Text.Split('\n');
                var offset = AutomationService.Integer(args, "offset", 0, 0, int.MaxValue);
                var limit = AutomationService.Integer(args, "limit", 200, 1, 2000);
                var text = string.Join('\n', lines.Skip(offset).Take(limit));
                if (!AutomationService.Flag(args, "includeSecrets"))
                    foreach (var secret in AutomationSecrets().Values.Concat(_installerKnownSecrets).Where(v => v.Length > 0).Distinct().OrderByDescending(v => v.Length))
                        text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
                return new { lines = text.Split('\n'), total = lines.Length, offset, limit };
            }
            case "installer.logs_clear": LogOutput.Clear(); return new { cleared = true };
            default: throw new ArgumentException($"Unknown installer operation: {operation}");
        }
    }

    private void RequireInstallerIdle()
    {
        if (_installing || _automationConnecting) throw new InvalidOperationException("Installer is busy; inspect installer.state or cancel the running job first.");
    }

    private object AutomationState() => new
    {
        connected = _ssh?.IsConnected == true && _sftp?.IsConnected == true,
        connecting = _automationConnecting, installing = _installing, host = SshHost.Text,
        port = SshPort.Text, username = SshUsername.Text,
        status = StatusBar.Text, progress = ProgressBar.Value, stage = ProgressLabel.Text,
        elapsed = _stopwatch?.Elapsed.ToString(),
        installerPath = InstallerRepoPath, installerAvailable = File.Exists(Path.Combine(InstallerRepoPath, "install.sh")),
        validationErrors = Validate(), profile = AutomationProfile(false)
    };

    private Dictionary<string, string> AutomationSecrets() => new()
    {
        ["ssh_password"] = SshPassword.Password, ["chanpass1"] = ChanPass1.Password,
        ["chanpass2"] = ChanPass2.Password, ["ircpass"] = IrcPass.Password, ["admin_password"] = AdminPassword.Password
    };

    private object AutomationProfile(bool includeSecrets)
    {
        var profile = CollectProfile();
        var secrets = AutomationSecrets();
        if (includeSecrets)
            foreach (var (key, value) in secrets) profile[key] = value;
        return new { profile, secretsPresent = secrets.ToDictionary(v => v.Key, v => !string.IsNullOrEmpty(v.Value)) };
    }

    private void AutomationApplyProfile(JsonElement patch)
    {
        var existing = CollectProfile();
        foreach (var (key, value) in AutomationSecrets()) { existing[key] = value; if (value.Length > 0) _installerKnownSecrets.Add(value); }
        var merged = JsonSerializer.SerializeToElement(existing).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        foreach (var property in patch.EnumerateObject())
        {
            if (!merged.ContainsKey(property.Name)) throw new ArgumentException($"Unknown installer profile field: {property.Name}");
            if (property.Name != "section_entries")
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 4096)
                    throw new ArgumentException($"Profile field {property.Name} must be a string of at most 4096 characters.");
                QuoteInstallerValue(property.Value.GetString()!);
            }
            else
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is < 1 or > 22)
                    throw new ArgumentException("section_entries must contain 1..22 sections.");
                foreach (var section in property.Value.EnumerateArray())
                {
                    if (section.ValueKind != JsonValueKind.Object) throw new ArgumentException("Sections must be objects.");
                    foreach (var field in new[] { "name", "path", "dated" })
                    {
                        if (!section.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4096)
                            throw new ArgumentException($"Each section needs string '{field}'.");
                        QuoteInstallerValue(value.GetString()!);
                    }
                    if (section.GetProperty("dated").GetString() is not ("y" or "n")) throw new ArgumentException("Section dated must be y or n.");
                }
            }
            merged[property.Name] = property.Value.Clone();
        }
        foreach (var field in new[] { "ssh_port", "port", "ircport" })
            if (!int.TryParse(merged[field].GetString(), out var port) || port is < 1 or > 65535)
                throw new ArgumentException($"{field} must be a port from 1 to 65535.");
        ApplyProfile(merged);
        SshPassword.Password = merged["ssh_password"].GetString()!;
        ChanPass1.Password = merged["chanpass1"].GetString()!;
        ChanPass2.Password = merged["chanpass2"].GetString()!;
        IrcPass.Password = merged["ircpass"].GetString()!;
        AdminPassword.Password = merged["admin_password"].GetString()!;
        foreach (var value in AutomationSecrets().Values.Where(v => v.Length > 0)) _installerKnownSecrets.Add(value);
    }

    private async Task<object> AutomationConnect(string? expectedHostKey, CancellationToken ct)
    {
        var host = SshHost.Text.Trim();
        var user = SshUsername.Text.Trim();
        if (host.Length == 0 || user.Length == 0 || !int.TryParse(SshPort.Text, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Set ssh_host, ssh_username and a valid ssh_port in the installer profile first.");
        var password = SshPassword.Password;
        if (password.Length == 0) password = CredentialStore.GetSshPassword(host, port, user) ?? "";
        var keyPath = SshKeyFile.Text.Trim();
        if (keyPath.Length > 0 && (!Path.IsPathFullyQualified(keyPath) || !File.Exists(keyPath)))
            throw new ArgumentException("ssh_keyfile must name an existing absolute local file.");
        if (keyPath.Length == 0 && password.Length == 0) throw new ArgumentException("Provide an SSH password or private-key file.");
        Disconnect();
        _automationConnecting = true;
        string? presentedFingerprint = null, presentedAlgorithm = null, previousFingerprint = null;
        var keyStatus = SshHostKeyStatus.Unknown;
        var hostKeyRejected = false;
        try
        {
            await Task.Run(async () =>
            {
                using var key = keyPath.Length == 0 ? null : password.Length == 0 ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, password);
                var methods = new List<AuthenticationMethod>();
                if (key != null) methods.Add(new PrivateKeyAuthenticationMethod(user, key));
                if (password.Length > 0) methods.Add(new PasswordAuthenticationMethod(user, password));
                var connection = new ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
                void Verify(object? sender, Renci.SshNet.Common.HostKeyEventArgs e)
                {
                    presentedFingerprint = e.FingerPrintSHA256;
                    presentedAlgorithm = e.HostKeyName;
                    keyStatus = _sshHostKeys.Check(host, port, presentedFingerprint, out previousFingerprint);
                    // An explicitly provided pin always wins, including over a previously trusted key.
                    e.CanTrust = expectedHostKey != null
                        ? expectedHostKey.Equals(presentedFingerprint, StringComparison.Ordinal)
                        : keyStatus == SshHostKeyStatus.Match;
                    hostKeyRejected |= !e.CanTrust;
                }
                _ssh = new SshClient(connection);
                _ssh.HostKeyReceived += Verify;
                await _ssh.ConnectAsync(ct);
                _sftp = new SftpClient(connection) { OperationTimeout = TimeSpan.FromSeconds(60) };
                _sftp.HostKeyReceived += Verify;
                await _sftp.ConnectAsync(ct);
                if (expectedHostKey != null && presentedFingerprint != null)
                    _sshHostKeys.Trust(host, port, presentedAlgorithm!, presentedFingerprint);
            }, ct);
            SshPassword.Password = password;
            if (password.Length > 0) _installerKnownSecrets.Add(password);
            if (password.Length > 0) CredentialStore.SaveSshPassword(host, port, user, password);
            ConnectionStatus.Text = $"Connected to {host}:{port}";
            StatusBar.Text = ConnectionStatus.Text;
            AppendLog("SSH connection established", "success");
            return new { connected = true, host, port, fingerprint = presentedFingerprint };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Disconnect();
            // The fingerprint is public identity evidence, not a credential. Return it so the
            // caller can verify it out of band and retry with an exact pin, without a modal UI.
            if (presentedFingerprint != null && hostKeyRejected)
                return new { connected = false, error = "host_key_verification_required", host, port, fingerprint = presentedFingerprint, algorithm = presentedAlgorithm, previousFingerprint };
            throw;
        }
        catch { Disconnect(); throw; }
        finally { _automationConnecting = false; }
    }

    /// <summary>The installer's cache reader strips double quotes; it does not evaluate shell code.</summary>
    internal static string QuoteInstallerValue(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0)
            throw new ArgumentException("Installer cache values cannot contain double quotes, CR, LF, or NUL.");
        return "\"" + value + "\"";
    }

    private async Task AutomationCommand(string commandText, CancellationToken ct, string? error = null)
    {
        using var command = _ssh!.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(30);
        await command.ExecuteAsync(ct);
        if (command.ExitStatus != 0) throw new IOException(error ?? $"Installer command failed with exit code {command.ExitStatus}.");
    }

    private async Task UploadDirectoryAsync(string localDir, string remoteDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await _sftp!.ExistsAsync(remoteDir, ct)) await _sftp.CreateDirectoryAsync(remoteDir, ct);
        foreach (var file in Directory.EnumerateFiles(localDir))
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(file);
            await _sftp.UploadFileAsync(stream, $"{remoteDir}/{Path.GetFileName(file)}", ct);
        }
        foreach (var directory in Directory.EnumerateDirectories(localDir))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installer package directories cannot contain symbolic links or junctions.");
            await UploadDirectoryAsync(directory, $"{remoteDir}/{Path.GetFileName(directory)}", ct);
        }
    }
}
