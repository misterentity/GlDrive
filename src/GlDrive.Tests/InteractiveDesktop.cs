using System.Runtime.InteropServices;

namespace GlDrive.Tests;

/// <summary>
/// Real keyboard focus, native key delivery and live repaint need the interactive input desktop.
/// On a locked or disconnected session (the release script often runs unattended) those checks
/// cannot pass, so tests that need them skip just those steps instead of failing the run.
/// </summary>
internal static class InteractiveDesktop
{
    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SwitchDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint desktop);

    public static bool IsAvailable()
    {
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == 0) return false;
        try { return SwitchDesktop(desktop); }
        finally { CloseDesktop(desktop); }
    }
}
