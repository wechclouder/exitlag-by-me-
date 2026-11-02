using wetwiLAG.Models;
using wetwiLAG.Services;

var checks = new List<string>();
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"wetwiLAG-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryRoot);

try
{
    Check(NetworkDiagnostics.IsValidHost("1.1.1.1"), "IPv4 probe validation");
    Check(NetworkDiagnostics.IsValidHost("example.com"), "DNS host validation");
    Check(!NetworkDiagnostics.IsValidHost("https://example.com/path"), "reject URL as probe host");
    Check(!NetworkDiagnostics.IsValidHost("bad host"), "reject whitespace in probe host");

    Check(
        SafeOptimizerService.ValidateDnsServers(["1.1.1.1", "8.8.8.8"]).Count == 2,
        "validate two IPv4 DNS servers");
    CheckThrows<ArgumentException>(() => SafeOptimizerService.ValidateDnsServers(["::1"]), "reject IPv6 DNS input");
    CheckThrows<ArgumentException>(() => SafeOptimizerService.ValidateDnsServers(["127.0.0.1"]), "reject loopback DNS input");
    CheckThrows<ArgumentException>(() => SafeOptimizerService.ValidateDnsServers(["1.1.1.1", "1.1.1.1"]), "reject duplicate DNS input");

    var store = new AppDataStore(temporaryRoot);
    var settings = new AppSettings
    {
        Theme = "Light",
        AccentColor = "#FF7AA2F7",
        Scale = 1.15,
        SidebarCollapsed = true,
        SidebarOnRight = true,
        DashboardOrder = new System.Collections.ObjectModel.ObservableCollection<string>(["latency", "traffic"]),
        HiddenWidgets = new System.Collections.ObjectModel.ObservableCollection<string>(["connection"])
    };
    await store.SaveSettingsAsync(settings);
    var loadedSettings = await store.LoadSettingsAsync();
    Check(loadedSettings.Theme == "Light" && loadedSettings.Scale == 1.15, "persist appearance settings");
    Check(loadedSettings.DashboardOrder.SequenceEqual(["latency", "traffic"]), "persist widget order");
    Check(loadedSettings.HiddenWidgets.SequenceEqual(["connection"]), "persist hidden widgets");

    var profile = new UserProfile
    {
        Name = "Smoke test",
        ProbeHost = "localhost",
        PreferredAdapterName = "Default adapter"
    };
    await store.SaveProfilesAsync([profile]);
    var loadedProfiles = await store.LoadProfilesAsync();
    Check(loadedProfiles.Count == 1 && loadedProfiles[0].Name == profile.Name, "persist profiles");

    var measurement = new MeasurementRecord
    {
        ProbeHost = "127.0.0.1",
        AverageLatencyMs = 0,
        JitterMs = 0,
        PacketLossPercent = 0,
        Sent = 1,
        Received = 1,
        TestType = "Smoke test"
    };
    await store.AppendMeasurementAsync(measurement);
    Check((await store.LoadHistoryAsync()).Single().Id == measurement.Id, "persist measurement history");

    var backup = new OptimizerBackup
    {
        SettingKind = "Dns",
        SettingName = "Test backup",
        OriginalMode = "Static",
        OriginalServers = ["1.1.1.1"],
        OriginalValue = "1.1.1.1",
        ProposedValue = "8.8.8.8",
        AdapterId = "test-adapter",
        AdapterName = "test"
    };
    var backupFile = await store.SaveBackupAsync(backup);
    backup.OriginalValue = "8.8.8.8";
    var savedBackup = (await store.LoadBackupsAsync()).Single();
    Check(savedBackup.OriginalValue == "1.1.1.1", "preserve immutable original backup values");
    Check(savedBackup.BackupFile == backupFile, "persist timestamped backup path");
    await CheckThrowsAsync<IOException>(() => store.SaveBackupAsync(
        new OptimizerBackup
        {
            Id = savedBackup.Id,
            CreatedAt = savedBackup.CreatedAt,
            SettingKind = savedBackup.SettingKind,
            OriginalValue = "should not overwrite"
        }), "refuse backup overwrite");
    Check((await store.LoadBackupsAsync()).Single().OriginalValue == "1.1.1.1", "backup remains unchanged after collision");

    var adapters = NetworkDiagnostics.GetAdapters();
    Check(adapters.Count > 0, "enumerate real Windows network interfaces");
    var activeAdapter = adapters.FirstOrDefault(adapter => adapter.Status == "Up");
    if (activeAdapter is not null)
    {
        var optimizer = new SafeOptimizerService(store);
        var dnsConfiguration = await optimizer.GetDnsConfigurationAsync(activeAdapter.Id);
        Check(dnsConfiguration.AdapterId == activeAdapter.Id, "read the active adapter DNS configuration without changes");
        Check(dnsConfiguration.Mode is "DHCP" or "Static" or "Unknown", "report DNS mode explicitly");
        var powerPlan = await optimizer.GetPowerPlanAsync();
        Check(
            !string.IsNullOrWhiteSpace(powerPlan.ActiveId) || !string.IsNullOrWhiteSpace(powerPlan.Error),
            "read the Windows power plan or report why it is unavailable");
    }

    var localhost = await NetworkDiagnostics.ResolveDnsAsync("localhost");
    Check(localhost.Error is null && localhost.Addresses.Count > 0, "resolve localhost using the system resolver");
    var loopbackTrace = await NetworkDiagnostics.TraceRouteAsync("127.0.0.1", 2, TimeSpan.FromMilliseconds(500));
    Check(loopbackTrace.Count > 0, "run bounded loopback route trace");

    var localPing = await NetworkDiagnostics.MeasureAsync("127.0.0.1", 2, TimeSpan.FromMilliseconds(500));
    Check(localPing.Sent == 2 && localPing.Received is >= 0 and <= 2, "record real loopback ping reply counts");
    Check(
        localPing.Received == 0
            ? localPing.AverageLatencyMs is null && localPing.PacketLossPercent == 100
            : localPing.AverageLatencyMs is >= 0 && localPing.PacketLossPercent < 100,
        "derive latency and loss only from actual replies");
    var unavailablePing = await NetworkDiagnostics.MeasureAsync(
        "192.0.2.1",
        1,
        TimeSpan.FromMilliseconds(400));
    Check(
        unavailablePing.Sent == 1 &&
        (unavailablePing.Received == 0
            ? unavailablePing.AverageLatencyMs is null
            : unavailablePing.AverageLatencyMs is >= 0),
        "handle a potentially unavailable network target without fake values");

    foreach (var check in checks)
    {
        Console.WriteLine($"PASS {check}");
    }

    Console.WriteLine($"Passed {checks.Count} smoke checks.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL {exception}");
    return 1;
}
finally
{
    Directory.Delete(temporaryRoot, true);
}

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Check failed: {name}");
    }

    checks.Add(name);
}

void CheckThrows<TException>(Action action, string name) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        checks.Add(name);
        return;
    }

    throw new InvalidOperationException($"Check failed: {name}");
}

async Task CheckThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        checks.Add(name);
        return;
    }

    throw new InvalidOperationException($"Check failed: {name}");
}
