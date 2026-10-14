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

    public bool Use { get => _use; set { _use = value; OnPropertyChanged(); } }
    public string FriendlyName { get => _friendlyName; set { _friendlyName = value; OnPropertyChanged(); } }
    public string LimitKbps { get => _limitKbps; set { _limitKbps = value; OnPropertyChanged(); } }
    public string AllowanceGb { get => _allowanceGb; set { _allowanceGb = value; OnPropertyChanged(); } }
    public AllowancePeriod Period { get => _period; set { _period = value; OnPropertyChanged(); } }

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
