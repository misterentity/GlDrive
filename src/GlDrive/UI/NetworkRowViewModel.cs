using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;

namespace GlDrive.UI;

/// <summary>One row of the Settings → Networks grid: a live adapter, a configured one, or both.</summary>
public class NetworkRowViewModel : INotifyPropertyChanged
{
    private bool _use;
    private string _friendlyName = "";
    private string _limitKbps = "0";
    private string _allowanceGb = "0";
    private AllowancePeriod _period;

    public string AdapterId { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public string? IPv4 { get; init; }
    public bool IsConnected { get; init; }

    public string AdapterDisplay
    {
        get
        {
            var name = string.IsNullOrEmpty(AdapterName) ? AdapterId : AdapterName;
            if (!IsConnected) return $"{name} (not connected)";
            return string.IsNullOrEmpty(IPv4) ? name : $"{name} ({IPv4})";
        }
    }

    public string NameDisplay => string.IsNullOrEmpty(AdapterName) ? AdapterId : AdapterName;
    public string AddressDisplay => IsConnected ? (string.IsNullOrEmpty(IPv4) ? "no ipv4" : IPv4) : "offline";

    public bool Use { get => _use; set { _use = value; OnPropertyChanged(); } }
    public string FriendlyName { get => _friendlyName; set { _friendlyName = value; OnPropertyChanged(); } }
    public string LimitKbps { get => _limitKbps; set { _limitKbps = value; OnPropertyChanged(); } }
    public string AllowanceGb { get => _allowanceGb; set { _allowanceGb = value; OnPropertyChanged(); OnUsageChanged(); } }
    public AllowancePeriod Period { get => _period; set { _period = value; OnPropertyChanged(); RefreshUsage(); } }

    // ── Data usage this period (read-only view of NetworkUsageStore) ──

    private long _usedBytes;

    /// <summary>Reads usage for (adapter id, period); null when local state isn't read (screenshots, tests).</summary>
    public Func<string, AllowancePeriod, long>? UsageLookup { get; set; }

    /// <summary>With no allowance period set, usage is still shown for the current month.</summary>
    public AllowancePeriod UsagePeriod => Period == AllowancePeriod.None ? AllowancePeriod.Monthly : Period;

    public long UsedBytes { get => _usedBytes; set { _usedBytes = value; OnUsageChanged(); } }

    public double AllowanceBytes =>
        double.TryParse(AllowanceGb, NumberStyles.Float, CultureInfo.CurrentCulture, out var g) && g > 0 && Period != AllowancePeriod.None
            ? g * 1024 * 1024 * 1024 : 0;

    public bool HasAllowance => AllowanceBytes > 0;
    public double UsageFraction => HasAllowance ? Math.Clamp(UsedBytes / AllowanceBytes, 0, 1) : 0;
    public bool IsOverAllowance => HasAllowance && UsedBytes >= AllowanceBytes;

    public string UsageText
    {
        get
        {
            var used = $"{UsedBytes / (1024.0 * 1024 * 1024):0.00}";
            var period = UsagePeriod switch
            {
                AllowancePeriod.Daily => "today",
                AllowancePeriod.Weekly => "this week",
                _ => "this month",
            };
            return HasAllowance
                ? $"{used} / {AllowanceBytes / (1024.0 * 1024 * 1024):0.##} GB {period}"
                : $"{used} GB {period}";
        }
    }

    public void RefreshUsage()
    {
        if (UsageLookup != null) UsedBytes = UsageLookup(AdapterId, UsagePeriod);
        else OnUsageChanged();
    }

    private void OnUsageChanged()
    {
        OnPropertyChanged(nameof(UsedBytes));
        OnPropertyChanged(nameof(UsagePeriod));
        OnPropertyChanged(nameof(HasAllowance));
        OnPropertyChanged(nameof(UsageFraction));
        OnPropertyChanged(nameof(IsOverAllowance));
        OnPropertyChanged(nameof(UsageText));
    }

    public static AllowancePeriod[] PeriodOptions { get; } = Enum.GetValues<AllowancePeriod>();

    /// <summary>Live candidate adapters first, then configured adapters that aren't up right now.</summary>
    public static List<NetworkRowViewModel> Build(IEnumerable<NetworkPathConfig> configured, IEnumerable<NetworkCatalog.Adapter> candidates)
    {
        var remaining = configured.ToList();
        var rows = new List<NetworkRowViewModel>();

        foreach (var a in candidates)
        {
            var cfg = remaining.FirstOrDefault(c => !string.IsNullOrEmpty(c.AdapterId) && c.AdapterId == a.Id)
                ?? remaining.FirstOrDefault(c => !string.IsNullOrEmpty(c.AdapterName) &&
                    string.Equals(c.AdapterName, a.Name, StringComparison.OrdinalIgnoreCase));
            if (cfg != null) remaining.Remove(cfg);
            rows.Add(FromConfig(cfg, a.Id, a.Name, a.IPv4, true));
        }

        foreach (var cfg in remaining)
            rows.Add(FromConfig(cfg, cfg.AdapterId, cfg.AdapterName, null, false));

        return rows;
    }

    /// <summary>Rows with any non-default value are kept; only ticked rows are Enabled.</summary>
    public static List<NetworkPathConfig> ToConfigs(IEnumerable<NetworkRowViewModel> rows)
    {
        var result = new List<NetworkPathConfig>();
        foreach (var r in rows)
        {
            var limit = int.TryParse(r.LimitKbps, out var l) ? Math.Max(l, 0) : 0;
            var allowance = double.TryParse(r.AllowanceGb, NumberStyles.Float, CultureInfo.CurrentCulture, out var g)
                ? Math.Max(g, 0) : 0;
            var friendly = r.FriendlyName.Trim();
            if (!r.Use && friendly.Length == 0 && limit == 0 && allowance == 0 && r.Period == AllowancePeriod.None)
                continue;

            result.Add(new NetworkPathConfig
            {
                AdapterId = r.AdapterId,
                AdapterName = r.AdapterName,
                FriendlyName = friendly,
                Enabled = r.Use,
                SpeedLimitKbps = limit,
                AllowanceGb = allowance,
                Period = r.Period
            });
        }
        return result;
    }

    private static NetworkRowViewModel FromConfig(NetworkPathConfig? cfg, string id, string name, string? ipv4, bool connected) => new()
    {
        AdapterId = id,
        AdapterName = name,
        IPv4 = ipv4,
        IsConnected = connected,
        Use = cfg?.Enabled ?? false,
        FriendlyName = cfg?.FriendlyName ?? "",
        LimitKbps = (cfg?.SpeedLimitKbps ?? 0).ToString(),
        AllowanceGb = (cfg?.AllowanceGb ?? 0).ToString(CultureInfo.CurrentCulture),
        Period = cfg?.Period ?? AllowancePeriod.None
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
