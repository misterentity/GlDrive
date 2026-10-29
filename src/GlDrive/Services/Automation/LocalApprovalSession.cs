using System.Runtime.InteropServices;

namespace GlDrive.Services.Automation;

internal static class LocalApprovalSession
{
    // Read-only desktop access check. A locked or disconnected desktop cannot approve.
    internal static bool CanPrompt()
    {
        try { return CanPromptCore(); }
        catch { return false; }
    }
    private static bool CanPromptCore()
    {
        if (!Environment.UserInteractive) return false;
        var desktop = OpenInputDesktop(0, false, 0x0101); // READOBJECTS | SWITCHDESKTOP access; never switch it.
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
