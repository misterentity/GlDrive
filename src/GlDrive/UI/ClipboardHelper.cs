using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace GlDrive.UI;

public static class ClipboardHelper
{
    // Another process (clipboard managers, RDP) can hold the clipboard open for a few ms;
    // Clipboard.SetDataObject then throws CLIPBRD_E_CANT_OPEN. Retry briefly instead of crashing.
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Clipboard copy failed");
                return false;
            }
        }
        return false;
    }
}
