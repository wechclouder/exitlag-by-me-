using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using wetwiLAG.Models;

namespace wetwiLAG.Services;

public sealed partial class SafeOptimizerService(AppDataStore store)
{
    private const string HighPerformancePlanId = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<DnsConfiguration> GetDnsConfigurationAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var adapter = NetworkDiagnostics.FindAdapter(adapterId)
                      ?? throw new InvalidOperationException("The selected network adapter is no longer available.");
        var netsh = await RunNetshAsync(
            ["interface", "ipv4", "show", "dnsservers", $"name={QuoteNetshValue(adapter.Name)}"],
            cancellationToken);
        var mode = DetermineDnsMode(netsh.StandardOutput);
        var servers = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate => candidate.Id == adapter.Id)?
            .GetIPProperties()
            .DnsAddresses
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return new DnsConfiguration
        {
            AdapterId = adapter.Id,
            AdapterName = adapter.Name,
            Mode = mode,
            Servers = servers,
            Error = mode == "Unknown"
                ? "Windows did not report whether DNS is automatic or static. To avoid an unsafe change that cannot be exactly restored, DNS changes are disabled for this adapter."
                : null
        };
    }

    public async Task<PowerPlanInfo> GetPowerPlanAsync(CancellationToken cancellationToken = default)
    {
        var result = new PowerPlanInfo();
        try
        {
            var active = await RunPowerCfgAsync(["/getactivescheme"], cancellationToken);
            var match = GuidPattern().Match(active.StandardOutput);
            if (!match.Success)
            {
                throw new InvalidOperationException("Windows did not return the active power plan identifier.");
            }

            result.ActiveId = match.Groups["id"].Value;
            result.ActiveName = ExtractPlanName(active.StandardOutput, result.ActiveId);
            var plans = await RunPowerCfgAsync(["/list"], cancellationToken);
            result.HighPerformanceAvailable = plans.StandardOutput.Contains(
                HighPerformancePlanId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            result.Error = exception.Message;
        }

        return result;
    }

    public async Task<string> GetSelectiveSuspendAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module NetAdapter -ErrorAction Stop
            $n = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeBase64(adapterName)}}'))
            $a = Get-NetAdapterPowerManagement -Name $n -ErrorAction Stop
            if ($null -eq $a.SelectiveSuspend) { throw 'Selective suspend is not supported on this adapter.' }
            [string]$a.SelectiveSuspend
            """;
        var result = await RunPowerShellAsync(script, cancellationToken);
        return LastNonEmptyLine(result.StandardOutput);
    }

    public async Task<OptimizerApplyResult> ApplyDnsAsync(
        string adapterId,
        IReadOnlyList<string> proposedServers,
        string probeHost,
        Func<DnsConfiguration, IReadOnlyList<string>, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var servers = ValidateDnsServers(proposedServers);
        var current = await GetDnsConfigurationAsync(adapterId, cancellationToken);
        if (current.Mode == "Unknown")
        {
            throw new InvalidOperationException(current.Error);
        }

        if (current.Mode == "Static" && current.Servers.Count == 0)
        {
            throw new InvalidOperationException(
                "The existing static DNS configuration is empty, so a complete restore point cannot be created.");
        }

        var before = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: cancellationToken);
        if (!await confirm(current, servers))
        {
            return new OptimizerApplyResult { Status = "Not applied", Details = "The change was cancelled." };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var adapter = NetworkDiagnostics.FindAdapter(current.AdapterId)
                      ?? throw new InvalidOperationException("The adapter disappeared before the change was applied.");
        var backup = new OptimizerBackup
        {
            SettingKind = "Dns",
            SettingName = $"IPv4 DNS on {adapter.Name}",
            OriginalValue = string.Join(", ", current.Servers),
            OriginalMode = current.Mode,
            OriginalServers = [.. current.Servers],
            ProposedValue = string.Join(", ", servers),
            AdapterId = adapter.Id,
            AdapterName = adapter.Name
        };
        var backupFile = await store.SaveBackupAsync(backup);
        store.LogInformation("optimizer-backup", $"{backup.SettingName}; original mode={current.Mode}; file={backupFile}");

        try
        {
            await SetDnsAsync(adapter.Name, "Static", servers, CancellationToken.None);
            await VerifyDnsAsync(adapter.Id, "Static", servers);
        }
        catch (Exception applyException)
        {
            var rollback = await TryRestoreDnsAsync(adapter.Name, current);
            var details = rollback.Succeeded
                ? $"The change failed and the original DNS configuration was restored. {applyException.Message}"
                : $"The change failed and automatic rollback also failed: {rollback.Error}. Original settings remain in backup {backupFile}.";
            await RecordOperationAsync(new OptimizerOperation
            {
                SettingKind = backup.SettingKind,
                SettingName = backup.SettingName,
                OriginalValue = backup.OriginalValue,
                ProposedValue = backup.ProposedValue,
                Status = "Failed",
                Details = details,
                BackupFile = backupFile,
                Before = before
            });
            throw new InvalidOperationException(details, applyException);
        }

        var after = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: CancellationToken.None);
        var result = new OptimizerApplyResult
        {
            Applied = true,
            Status = "Applied",
            BackupFile = backupFile,
            Before = before,
            After = after,
            Details = $"The IPv4 DNS servers were updated and verified. {DescribeComparison(before, after)}"
        };
        await RecordOperationAsync(new OptimizerOperation
        {
            SettingKind = backup.SettingKind,
            SettingName = backup.SettingName,
            OriginalValue = backup.OriginalValue,
            ProposedValue = backup.ProposedValue,
            Status = result.Status,
            Details = result.Details,
            BackupFile = backupFile,
            Before = before,
            After = after
        });
        return result;
    }

    public async Task<OptimizerApplyResult> ApplyHighPerformancePlanAsync(
        string probeHost,
        Func<PowerPlanInfo, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPowerPlanAsync(cancellationToken);
        if (current.Error is not null)
        {
            throw new InvalidOperationException(current.Error);
        }

        if (!current.HighPerformanceAvailable)
        {
            throw new InvalidOperationException("The built-in High performance power plan is not available on this PC.");
        }

        var before = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: cancellationToken);
        if (!await confirm(current))
        {
            return new OptimizerApplyResult { Status = "Not applied", Details = "The change was cancelled." };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var backup = new OptimizerBackup
        {
            SettingKind = "PowerPlan",
            SettingName = "Active Windows power plan",
            OriginalValue = current.ActiveId,
            ProposedValue = HighPerformancePlanId
        };
        var backupFile = await store.SaveBackupAsync(backup);
        store.LogInformation("optimizer-backup", $"{backup.SettingName}; original={current.ActiveId}; file={backupFile}");
        try
        {
            await RunPowerCfgAsync(["/setactive", HighPerformancePlanId], CancellationToken.None);
            var verified = await GetPowerPlanAsync(CancellationToken.None);
            if (!verified.ActiveId.Equals(HighPerformancePlanId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Windows did not verify the High performance power plan as active.");
            }
        }
        catch (Exception applyException)
        {
            var rollback = await TryRestorePowerPlanAsync(current.ActiveId);
            var details = rollback.Succeeded
                ? $"The change failed and the previous power plan was restored. {applyException.Message}"
                : $"The change failed and automatic rollback also failed: {rollback.Error}. Original settings remain in backup {backupFile}.";
            await RecordOperationAsync(new OptimizerOperation
            {
                SettingKind = backup.SettingKind,
                SettingName = backup.SettingName,
                OriginalValue = backup.OriginalValue,
                ProposedValue = backup.ProposedValue,
                Status = "Failed",
                Details = details,
                BackupFile = backupFile,
                Before = before
            });
            throw new InvalidOperationException(details, applyException);
        }

        var after = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: CancellationToken.None);
        var result = new OptimizerApplyResult
        {
            Applied = true,
            Status = "Applied",
            BackupFile = backupFile,
            Before = before,
            After = after,
            Details = $"The power plan changed to High performance and was verified. {DescribeComparison(before, after)}"
        };
        await RecordOperationAsync(new OptimizerOperation
        {
            SettingKind = backup.SettingKind,
            SettingName = backup.SettingName,
            OriginalValue = backup.OriginalValue,
            ProposedValue = backup.ProposedValue,
            Status = result.Status,
            Details = result.Details,
            BackupFile = backupFile,
            Before = before,
            After = after
        });
        return result;
    }

    public async Task<OptimizerApplyResult> ApplySelectiveSuspendAsync(
        string adapterId,
        string probeHost,
        Func<string, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var adapter = NetworkDiagnostics.FindAdapter(adapterId)
                      ?? throw new InvalidOperationException("The selected network adapter is no longer available.");
        var originalState = await GetSelectiveSuspendAsync(adapter.Name, cancellationToken);
        if (!originalState.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                originalState.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Selective suspend is already disabled; no change is needed."
                    : $"Selective suspend reports '{originalState}', which cannot be safely changed or restored.");
        }

        var before = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: cancellationToken);
        if (!await confirm(originalState))
        {
            return new OptimizerApplyResult { Status = "Not applied", Details = "The change was cancelled." };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var backup = new OptimizerBackup
        {
            SettingKind = "SelectiveSuspend",
            SettingName = $"Adapter selective suspend on {adapter.Name}",
            OriginalValue = originalState,
            ProposedValue = "Disabled",
            AdapterId = adapter.Id,
            AdapterName = adapter.Name
        };
        var backupFile = await store.SaveBackupAsync(backup);
        store.LogInformation("optimizer-backup", $"{backup.SettingName}; original={originalState}; file={backupFile}");
        try
        {
            await SetSelectiveSuspendAsync(adapter.Name, "Disabled", CancellationToken.None);
            var verified = await GetSelectiveSuspendAsync(adapter.Name, CancellationToken.None);
            if (!verified.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Windows reported selective suspend as '{verified}' after the change.");
            }
        }
        catch (Exception applyException)
        {
            var rollback = await TrySetSelectiveSuspendAsync(adapter.Name, originalState);
            var details = rollback.Succeeded
                ? $"The change failed and the original adapter setting was restored. {applyException.Message}"
                : $"The change failed and automatic rollback also failed: {rollback.Error}. Original settings remain in backup {backupFile}.";
            await RecordOperationAsync(new OptimizerOperation
            {
                SettingKind = backup.SettingKind,
                SettingName = backup.SettingName,
                OriginalValue = backup.OriginalValue,
                ProposedValue = backup.ProposedValue,
                Status = "Failed",
                Details = details,
                BackupFile = backupFile,
                Before = before
            });
            throw new InvalidOperationException(details, applyException);
        }

        var after = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: CancellationToken.None);
        var result = new OptimizerApplyResult
        {
            Applied = true,
            Status = "Applied",
            BackupFile = backupFile,
            Before = before,
            After = after,
            Details = $"Selective suspend is disabled and verified. {DescribeComparison(before, after)}"
        };
        await RecordOperationAsync(new OptimizerOperation
        {
            SettingKind = backup.SettingKind,
            SettingName = backup.SettingName,
            OriginalValue = backup.OriginalValue,
            ProposedValue = backup.ProposedValue,
            Status = result.Status,
            Details = result.Details,
            BackupFile = backupFile,
            Before = before,
            After = after
        });
        return result;
    }

    public async Task<OptimizerApplyResult> RestoreBackupAsync(
        OptimizerBackup requestedBackup,
        string probeHost,
        Func<OptimizerBackup, string, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var backups = await store.LoadBackupsAsync();
        var backup = backups.FirstOrDefault(candidate => candidate.Id == requestedBackup.Id)
                     ?? throw new FileNotFoundException("The selected restore point is no longer available.");
        var current = await CaptureCurrentValueAsync(backup, cancellationToken);
        var before = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: cancellationToken);
        if (!await confirm(backup, FormatBackupValue(current)))
        {
            return new OptimizerApplyResult { Status = "Not applied", Details = "Restoration was cancelled." };
        }

        cancellationToken.ThrowIfCancellationRequested();
        current.SettingName = $"Pre-restore snapshot: {backup.SettingName}";
        current.ProposedValue = backup.OriginalValue;
        var safetyBackupFile = await store.SaveBackupAsync(current);
        store.LogInformation("optimizer-pre-restore-backup", $"{backup.SettingName}; file={safetyBackupFile}");

        try
        {
            await ApplyBackupValueAsync(backup);
        }
        catch (Exception restoreException)
        {
            var rollback = await TryApplyBackupValueAsync(current);
            var details = rollback.Succeeded
                ? $"Restore failed; the setting was returned to its pre-restore value. {restoreException.Message}"
                : $"Restore failed and rollback also failed: {rollback.Error}. Original backup {backup.BackupFile} and safety backup {safetyBackupFile} were preserved.";
            await RecordOperationAsync(new OptimizerOperation
            {
                SettingKind = backup.SettingKind,
                SettingName = backup.SettingName,
                OriginalValue = FormatBackupValue(current),
                ProposedValue = FormatBackupValue(backup),
                Status = "Failed",
                Details = details,
                BackupFile = backup.BackupFile,
                Before = before
            });
            throw new InvalidOperationException(details, restoreException);
        }

        var after = await NetworkDiagnostics.MeasureAsync(probeHost, cancellationToken: CancellationToken.None);
        var result = new OptimizerApplyResult
        {
            Restored = true,
            Status = "Restored",
            BackupFile = safetyBackupFile,
            Before = before,
            After = after,
            Details = $"The original setting was restored and verified. A pre-restore snapshot is available as {safetyBackupFile}. {DescribeComparison(before, after)}"
        };
        await RecordOperationAsync(new OptimizerOperation
        {
            SettingKind = backup.SettingKind,
            SettingName = backup.SettingName,
            OriginalValue = FormatBackupValue(current),
            ProposedValue = FormatBackupValue(backup),
            Status = result.Status,
            Details = result.Details,
            BackupFile = safetyBackupFile,
            Before = before,
            After = after
        });
        store.LogInformation("optimizer-restore", $"{backup.SettingName}; backup={backup.BackupFile}");
        return result;
    }

    private async Task<OptimizerBackup> CaptureCurrentValueAsync(
        OptimizerBackup target,
        CancellationToken cancellationToken)
    {
        var backup = new OptimizerBackup
        {
            SettingKind = target.SettingKind,
            SettingName = target.SettingName,
            AdapterId = target.AdapterId,
            AdapterName = target.AdapterName,
            ProposedValue = target.OriginalValue
        };
        switch (target.SettingKind)
        {
            case "Dns":
            {
                var adapter = NetworkDiagnostics.FindAdapter(target.AdapterId)
                              ?? throw new InvalidOperationException(
                                  $"The original adapter '{target.AdapterName}' is unavailable. The backup was preserved.");
                var current = await GetDnsConfigurationAsync(adapter.Id, cancellationToken);
                if (current.Mode == "Unknown")
                {
                    throw new InvalidOperationException(
                        $"The current DNS source is unknown, so a safe restore snapshot cannot be created. {current.Error}");
                }

                backup.AdapterId = adapter.Id;
                backup.AdapterName = adapter.Name;
                backup.OriginalMode = current.Mode;
                backup.OriginalServers = [.. current.Servers];
                backup.OriginalValue = string.Join(", ", current.Servers);
                if (current.Mode == "Static" && current.Servers.Count == 0)
                {
                    throw new InvalidOperationException("The current static DNS configuration is empty and cannot be safely snapshotted.");
                }

                break;
            }
            case "PowerPlan":
            {
                var current = await GetPowerPlanAsync(cancellationToken);
                if (current.Error is not null || string.IsNullOrWhiteSpace(current.ActiveId))
                {
                    throw new InvalidOperationException(current.Error ?? "The active power plan could not be read.");
                }

                backup.OriginalValue = current.ActiveId;
                break;
            }
            case "SelectiveSuspend":
            {
                var adapter = NetworkDiagnostics.FindAdapter(target.AdapterId)
                              ?? throw new InvalidOperationException(
                                  $"The original adapter '{target.AdapterName}' is unavailable. The backup was preserved.");
                var current = await GetSelectiveSuspendAsync(adapter.Name, cancellationToken);
                if (current is not ("Enabled" or "Disabled"))
                {
                    throw new InvalidOperationException(
                        $"The current adapter value '{current}' cannot be safely snapshotted or restored.");
                }

                backup.AdapterId = adapter.Id;
                backup.AdapterName = adapter.Name;
                backup.OriginalValue = current;
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown backup type '{target.SettingKind}'.");
        }

        return backup;
    }

    private async Task ApplyBackupValueAsync(OptimizerBackup backup)
    {
        switch (backup.SettingKind)
        {
            case "Dns":
            {
                var adapter = NetworkDiagnostics.FindAdapter(backup.AdapterId)
                              ?? throw new InvalidOperationException($"The adapter '{backup.AdapterName}' is unavailable.");
                var configuration = new DnsConfiguration
                {
                    AdapterId = adapter.Id,
                    AdapterName = adapter.Name,
                    Mode = backup.OriginalMode,
                    Servers = backup.OriginalServers
                };
                await SetDnsAsync(adapter.Name, configuration.Mode, configuration.Servers, CancellationToken.None);
                await VerifyDnsAsync(adapter.Id, configuration.Mode, configuration.Servers);
                break;
            }
            case "PowerPlan":
            {
                await RunPowerCfgAsync(["/setactive", backup.OriginalValue], CancellationToken.None);
                var active = await GetPowerPlanAsync(CancellationToken.None);
                if (!active.ActiveId.Equals(backup.OriginalValue, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Windows did not verify the restored power plan.");
                }

                break;
            }
            case "SelectiveSuspend":
            {
                var adapter = NetworkDiagnostics.FindAdapter(backup.AdapterId)
                              ?? throw new InvalidOperationException($"The adapter '{backup.AdapterName}' is unavailable.");
                await SetSelectiveSuspendAsync(adapter.Name, backup.OriginalValue, CancellationToken.None);
                var verified = await GetSelectiveSuspendAsync(adapter.Name, CancellationToken.None);
                if (!verified.Equals(backup.OriginalValue, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Windows reported '{verified}' instead of '{backup.OriginalValue}'.");
                }

                break;
            }
            default:
                throw new InvalidOperationException($"Unknown backup type '{backup.SettingKind}'.");
        }
    }

    private async Task<(bool Succeeded, string? Error)> TryApplyBackupValueAsync(OptimizerBackup backup)
    {
        try
        {
            await ApplyBackupValueAsync(backup);
            return (true, null);
        }
        catch (Exception exception)
        {
            store.LogError("rollback-restore", exception);
            return (false, exception.Message);
        }
    }

    private static string FormatBackupValue(OptimizerBackup backup) => backup.SettingKind switch
    {
        "Dns" => backup.OriginalMode == "DHCP"
            ? "DNS assigned automatically by DHCP"
            : $"Static DNS — {string.Join(", ", backup.OriginalServers)}",
        "PowerPlan" => backup.OriginalValue,
        "SelectiveSuspend" => backup.OriginalValue,
        _ => backup.OriginalValue
    };

    public static IReadOnlyList<string> ValidateDnsServers(IEnumerable<string> serverInputs)
    {
        var servers = serverInputs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();
        if (servers.Count is < 1 or > 2)
        {
            throw new ArgumentException("Enter one or two IPv4 DNS server addresses.");
        }

        foreach (var server in servers)
        {
            if (!IPAddress.TryParse(server, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                IPAddress.IsLoopback(address))
            {
                throw new ArgumentException($"'{server}' is not a usable IPv4 DNS server address.");
            }
        }

        if (servers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != servers.Count)
        {
            throw new ArgumentException("The primary and secondary DNS server addresses must be different.");
        }

        return servers;
    }

    private async Task SetDnsAsync(
        string adapterName,
        string mode,
        IReadOnlyList<string> servers,
        CancellationToken cancellationToken)
    {
        if (mode == "DHCP")
        {
            await RunNetshAsync(
                ["interface", "ipv4", "set", "dnsservers", $"name={QuoteNetshValue(adapterName)}", "source=dhcp"],
                cancellationToken);
            return;
        }

        if (servers.Count == 0)
        {
            throw new InvalidOperationException("A static DNS configuration requires at least one server.");
        }

        await RunNetshAsync(
            [
                "interface", "ipv4", "set", "dnsservers",
                $"name={QuoteNetshValue(adapterName)}", "source=static",
                $"address={servers[0]}", "validate=no"
            ],
            cancellationToken);
        for (var index = 1; index < servers.Count; index++)
        {
            await RunNetshAsync(
                [
                    "interface", "ipv4", "add", "dnsservers",
                    $"name={QuoteNetshValue(adapterName)}",
                    $"address={servers[index]}", $"index={index + 1}", "validate=no"
                ],
                cancellationToken);
        }
    }

    private async Task<(bool Succeeded, string? Error)> TryRestoreDnsAsync(
        string adapterName,
        DnsConfiguration configuration)
    {
        try
        {
            await SetDnsAsync(adapterName, configuration.Mode, configuration.Servers, CancellationToken.None);
            return (true, null);
        }
        catch (Exception exception)
        {
            store.LogError("restore-dns", exception);
            return (false, exception.Message);
        }
    }

    private async Task VerifyDnsAsync(string adapterId, string expectedMode, IReadOnlyList<string> expectedServers)
    {
        var actual = await GetDnsConfigurationAsync(adapterId, CancellationToken.None);
        if (!actual.Mode.Equals(expectedMode, StringComparison.OrdinalIgnoreCase) ||
            (expectedMode == "Static" && !actual.Servers.SequenceEqual(expectedServers, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"DNS verification failed. Windows reports mode {actual.Mode} and servers {string.Join(", ", actual.Servers)}.");
        }
    }

    private async Task<(bool Succeeded, string? Error)> TryRestorePowerPlanAsync(string planId)
    {
        try
        {
            await RunPowerCfgAsync(["/setactive", planId], CancellationToken.None);
            return (true, null);
        }
        catch (Exception exception)
        {
            store.LogError("restore-power-plan", exception);
            return (false, exception.Message);
        }
    }

    private async Task SetSelectiveSuspendAsync(string adapterName, string state, CancellationToken cancellationToken)
    {
        if (state is not ("Enabled" or "Disabled"))
        {
            throw new ArgumentException($"Unsupported selective-suspend state '{state}'.", nameof(state));
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module NetAdapter -ErrorAction Stop
            $n = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EncodeBase64(adapterName)}}'))
            Set-NetAdapterPowerManagement -Name $n -SelectiveSuspend {state} -ErrorAction Stop
            [string](Get-NetAdapterPowerManagement -Name $n -ErrorAction Stop).SelectiveSuspend
            """;
        var result = await RunPowerShellAsync(script, cancellationToken);
        var reportedState = LastNonEmptyLine(result.StandardOutput);
        if (!reportedState.Equals(state, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Windows reported selective suspend as '{reportedState}'.");
        }
    }

    private async Task<(bool Succeeded, string? Error)> TrySetSelectiveSuspendAsync(string adapterName, string state)
    {
        try
        {
            await SetSelectiveSuspendAsync(adapterName, state, CancellationToken.None);
            return (true, null);
        }
        catch (Exception exception)
        {
            store.LogError("restore-selective-suspend", exception);
            return (false, exception.Message);
        }
    }

    private async Task RecordOperationAsync(OptimizerOperation operation)
    {
        try
        {
            await store.AppendOperationAsync(operation);
            store.LogInformation(
                "optimizer-operation",
                $"{operation.Status}: {operation.SettingName}; backup={operation.BackupFile}");
        }
        catch (Exception exception)
        {
            store.LogError("record-optimizer-operation", exception);
            throw new InvalidOperationException(
                $"The setting operation completed, but its operation history could not be saved: {exception.Message}. Backup: {operation.BackupFile}",
                exception);
        }
    }

    private async Task<ProcessResult> RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        await RunCommandAsync("netsh.exe", arguments, cancellationToken);

    private async Task<ProcessResult> RunPowerCfgAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        await RunCommandAsync("powercfg.exe", arguments, cancellationToken);

    private async Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunCommandAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", encoded],
            cancellationToken);
    }

    private async Task<ProcessResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Windows could not start {fileName}.");
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutputTask;
        var error = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}: {message}");
        }

        return new ProcessResult(output, error);
    }

    private static string DetermineDnsMode(string output)
    {
        var text = output.ToLowerInvariant();
        if (text.Contains("configured through dhcp", StringComparison.Ordinal) ||
            text.Contains("dhcp:", StringComparison.Ordinal) ||
            (text.Contains("dhcp", StringComparison.Ordinal) &&
             (text.Contains("dns", StringComparison.Ordinal) ||
              text.Contains("днс", StringComparison.Ordinal) ||
              text.Contains("сервер", StringComparison.Ordinal))))
        {
            return "DHCP";
        }

        if (text.Contains("statically configured dns servers", StringComparison.Ordinal) ||
            text.Contains("статически", StringComparison.Ordinal) ||
            text.Contains("статический", StringComparison.Ordinal))
        {
            return "Static";
        }

        return "Unknown";
    }

    private static string ExtractPlanName(string output, string planId)
    {
        var line = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.Contains(planId, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return "Active plan";
        }

        var openingParenthesis = line.LastIndexOf('(');
        var closingParenthesis = line.LastIndexOf(')');
        return openingParenthesis >= 0 && closingParenthesis > openingParenthesis
            ? line[(openingParenthesis + 1)..closingParenthesis].Trim()
            : line.Trim();
    }

    private static string QuoteNetshValue(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string EncodeBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string LastNonEmptyLine(string output) =>
        output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";

    private static string DescribeComparison(DiagnosticResult before, DiagnosticResult after)
    {
        static string Format(DiagnosticResult result) =>
            result.AverageLatencyMs is null
                ? $"latency unavailable; jitter unavailable; packet loss {result.PacketLossPercent:0.#}% ({result.Received}/{result.Sent} replies)"
                : $"latency {result.AverageLatencyMs:0.##} ms; jitter {result.JitterMs?.ToString("0.##") ?? "unavailable"} ms; packet loss {result.PacketLossPercent:0.#}%";

        if (before.AverageLatencyMs is null || after.AverageLatencyMs is null)
        {
            return $"Before: {Format(before)}. After: {Format(after)}. A performance improvement cannot be established from these measurements.";
        }

        var difference = after.AverageLatencyMs.Value - before.AverageLatencyMs.Value;
        var comparison = Math.Abs(difference) < 1
            ? "No measurable latency change was observed."
            : difference < 0
                ? $"Measured latency was {Math.Abs(difference):0.##} ms lower after the change; this is not a guarantee of future performance."
                : $"Measured latency was {difference:0.##} ms higher after the change; this is not a guarantee of future performance.";
        return $"Before: {Format(before)}. After: {Format(after)}. {comparison}";
    }

    [GeneratedRegex(@"(?i)(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})")]
    private static partial Regex GuidPattern();

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
