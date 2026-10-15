namespace GlDrive.UI;

/// <summary>
/// Opens a disclosure when its live signal starts (e.g. a multi-stream transfer begins) but
/// never fights the user: collapsing it while the signal is active keeps it closed until that
/// signal ends. The next signal starts fresh.
/// </summary>
public sealed class AutoExpandLatch
{
    private bool _active;
    private bool _userCollapsed;

    /// <summary>Returns true when the disclosure should be opened now.</summary>
    public bool SignalChanged(bool active)
    {
        if (active == _active) return false;
        _active = active;
        if (!active)
        {
            _userCollapsed = false;
            return false;
        }
        return !_userCollapsed;
    }

    /// <summary>Record an expand/collapse the user made.</summary>
    public void UserToggled(bool expanded)
    {
        if (_active) _userCollapsed = !expanded;
    }
}
