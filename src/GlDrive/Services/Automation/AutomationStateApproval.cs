using System.Security.Cryptography;
using System.Text.Json;

namespace GlDrive.Services.Automation;

/// <summary>A pending approval is tied to the exact form, queue and live resources it reviewed.</summary>
internal sealed class AutomationStateApproval
{
    private readonly byte[] _fingerprint;
    private readonly object?[] _resources;
    public string Description { get; }

    public AutomationStateApproval(object state, string description, IEnumerable<string> secrets, params object?[] resources)
    {
        _fingerprint = Fingerprint(state);
        _resources = resources.ToArray();
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s))
                     .SelectMany(s => new[] { s, JsonSerializer.Serialize(s)[1..^1] }).Distinct().OrderByDescending(s => s.Length))
            description = description.Replace(secret, "[redacted]", StringComparison.Ordinal);
        description = new string(description.Select(c => char.IsControl(c) && c != '\n' ? ' ' : c).ToArray());
        Description = description.Length <= 8000 ? description : description[..8000] + "\nAdditional details omitted; deny if the selection is unexpected.";
    }

    public void Validate(object state, params object?[] resources)
    {
        if (resources.Length != _resources.Length || resources.Where((resource, index) => !ReferenceEquals(resource, _resources[index])).Any() ||
            !CryptographicOperations.FixedTimeEquals(_fingerprint, Fingerprint(state)))
            throw new AutomationDeniedException("The target or settings changed while approval was pending. Request approval again.");
    }

    private static byte[] Fingerprint(object state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
