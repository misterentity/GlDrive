namespace GlDrive.Services.Operations;

/// <summary>Copies pointer names already produced by the ordinary save diff; never receives values or graphs.</summary>
internal sealed class ConfigPointerSummary
{
    private readonly List<string> _pointers = [];
    private int _characters;
    internal bool Partial { get; private set; }
    internal void Add(string? pointer)
    {
        if (pointer is not { Length: > 0 and <= 512 } || _pointers.Count == 32 || _characters + pointer.Length > 3500)
        { Partial = true; return; }
        _pointers.Add(pointer); _characters += pointer.Length;
    }
    internal string Detail => _pointers.Count == 0 && !Partial ? "No changed pointers." :
        "Changed pointers" + (Partial ? " (partial; additional or oversized pointers omitted)" : "") + ": " + string.Join(", ", _pointers);
}
