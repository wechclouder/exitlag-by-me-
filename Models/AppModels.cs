using System.Collections.ObjectModel;

namespace wetwiLAG.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#FF63D6C5";
    public double Scale { get; set; } = 1.0;
    public bool SidebarCollapsed { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public bool SidebarOnRight { get; set; }
    public string ProbeHost { get; set; } = "1.1.1.1";
    public string? SelectedAdapterId { get; set; }
    public string? SelectedProfileId { get; set; }
    public ObservableCollection<string> DashboardOrder { get; set; } =
        new(["connection", "latency", "traffic", "fortnite", "adapter"]);
    public ObservableCollection<string> HiddenWidgets { get; set; } = [];
    public Dictionary<string, double> WidgetWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<LayoutPreset> LayoutPresets { get; set; } = [];
}

public sealed class LayoutPreset : Infrastructure.ObservableObject
{
    private string _name = "New preset";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#FF63D6C5";
    public double Scale { get; set; } = 1.0;
    public bool SidebarCollapsed { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public bool SidebarOnRight { get; set; }
    public ObservableCollection<string> DashboardOrder { get; set; } = [];
    public ObservableCollection<string> HiddenWidgets { get; set; } = [];
    public Dictionary<string, double> WidgetWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string ProbeHost { get; set; } = "1.1.1.1";
    public string? PreferredAdapterId { get; set; }
    public string? PreferredAdapterName { get; set; }
}

public sealed class NetworkAdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string InterfaceType { get; set; } = "";
    public long SpeedBitsPerSecond { get; set; }
    public string IPv4Addresses { get; set; } = "Unavailable";
    public string Gateway { get; set; } = "Unavailable";
    public string DnsServers { get; set; } = "Unavailable";
    public string DnsMode { get; set; } = "Unknown";
    public int? Mtu { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

public sealed class NetworkSnapshot
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public string AdapterName { get; set; } = "Unavailable";
    public string InterfaceStatus { get; set; } = "Unavailable";
    public double DownloadBytesPerSecond { get; set; }
    public double UploadBytesPerSecond { get; set; }
    public bool? InternetReachable { get; set; }
    public string ProbeHost { get; set; } = "";
    public double? LatencyMs { get; set; }
    public double? JitterMs { get; set; }
    public double? PacketLossPercent { get; set; }
    public int Sent { get; set; }
    public int Received { get; set; }
    public string? DiagnosticError { get; set; }
}

public sealed class MeasurementRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string ProbeHost { get; set; } = "";
    public string TestType { get; set; } = "Ping";
    public double? AverageLatencyMs { get; set; }
    public double? JitterMs { get; set; }
    public double PacketLossPercent { get; set; }
    public int Sent { get; set; }
    public int Received { get; set; }
    public string Replies => $"{Received}/{Sent}";
    public string? Error { get; set; }
}

public sealed class DiagnosticResult
{
    public string Target { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public double? AverageLatencyMs { get; set; }
    public double? JitterMs { get; set; }
    public double PacketLossPercent { get; set; }
    public int Sent { get; set; }
    public int Received { get; set; }
    public string? Error { get; set; }
}

public sealed class DnsResolutionResult
{
    public string Host { get; set; } = "";
    public IReadOnlyList<string> Addresses { get; set; } = [];
    public double ElapsedMilliseconds { get; set; }
    public string? Error { get; set; }
}

public sealed class RouteHop
{
    public int Hop { get; set; }
    public string Address { get; set; } = "*";
    public double? LatencyMs { get; set; }
    public string Status { get; set; } = "";
}

public sealed class DnsConfiguration
{
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string Mode { get; set; } = "Unknown";
    public IReadOnlyList<string> Servers { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class OptimizerBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string SettingKind { get; set; } = "";
    public string SettingName { get; set; } = "";
    public string OriginalValue { get; set; } = "";
    public string OriginalMode { get; set; } = "";
    public List<string> OriginalServers { get; set; } = [];
    public string ProposedValue { get; set; } = "";
    public string? AdapterId { get; set; }
    public string? AdapterName { get; set; }
    public string? BackupFile { get; set; }
}

public sealed class PowerPlanInfo
{
    public string ActiveId { get; set; } = "";
    public string ActiveName { get; set; } = "Unavailable";
    public bool HighPerformanceAvailable { get; set; }
    public string? Error { get; set; }
}

public sealed class OptimizerApplyResult
{
    public bool Applied { get; set; }
    public bool Restored { get; set; }
    public string Status { get; set; } = "";
    public string Details { get; set; } = "";
    public string? BackupFile { get; set; }
    public DiagnosticResult? Before { get; set; }
    public DiagnosticResult? After { get; set; }
}

public sealed class OptimizerOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string SettingKind { get; set; } = "";
    public string SettingName { get; set; } = "";
    public string OriginalValue { get; set; } = "";
    public string ProposedValue { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Details { get; set; }
    public string? BackupFile { get; set; }
    public DiagnosticResult? Before { get; set; }
    public DiagnosticResult? After { get; set; }
}

public sealed class DashboardWidget : Infrastructure.ObservableObject
{
    private string _value = "Unavailable";
    private string _detail = "";
    private double _width = 265;

    public DashboardWidget(string id, string title, string icon)
    {
        Id = id;
        Title = title;
        Icon = icon;
    }

    public string Id { get; }
    public string Title { get; }
    public string Icon { get; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public double Width { get => _width; set => SetProperty(ref _width, value); }
}
