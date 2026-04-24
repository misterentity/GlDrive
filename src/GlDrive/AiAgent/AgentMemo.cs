using System.IO;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentMemo
{
    private readonly string _path;

    public AgentMemo(string aiDataRoot)
    {
        _path = Path.Combine(aiDataRoot, "agent-memo.md");
    }

    public string Load()
    {
        try { return File.Exists(_path) ? File.ReadAllText(_path) : ""; }
        catch (Exception ex) { Log.Warning(ex, "AgentMemo load failed"); return ""; }
    }

    public void Save(string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_path, content ?? "");
        }
        catch (Exception ex) { Log.Warning(ex, "AgentMemo save failed"); }
    }
}
