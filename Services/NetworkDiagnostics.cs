using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using wetwiLAG.Models;

namespace wetwiLAG.Services;

public static partial class NetworkDiagnostics
{
    private static readonly byte[] PingPayload = new byte[32];

    public static IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var info = new NetworkAdapterInfo
            {
                Id = adapter.Id,
                Name = adapter.Name,
                Description = adapter.Description,
                Status = adapter.OperationalStatus.ToString(),
                InterfaceType = adapter.NetworkInterfaceType.ToString(),
                SpeedBitsPerSecond = adapter.Speed
            };

            try
            {
                var properties = adapter.GetIPProperties();
                info.IPv4Addresses = JoinOrUnavailable(
                    properties.UnicastAddresses
                        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(address => address.Address.ToString()));
                info.Gateway = JoinOrUnavailable(
                    properties.GatewayAddresses
                        .Select(gateway => gateway.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(address => address.ToString()));
                info.DnsServers = JoinOrUnavailable(
                    properties.DnsAddresses
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(address => address.ToString()));

                try
                {
                    info.Mtu = properties.GetIPv4Properties()?.Mtu;
                }
                catch (NetworkInformationException)
                {
                    info.Mtu = null;
                }
            }
            catch (NetworkInformationException)
            {
                info.IPv4Addresses = "Unavailable";
                info.Gateway = "Unavailable";
                info.DnsServers = "Unavailable";
            }

            try
            {
                var statistics = adapter.GetIPStatistics();
                info.BytesReceived = statistics.BytesReceived;
                info.BytesSent = statistics.BytesSent;
            }
            catch (NetworkInformationException)
            {
                info.BytesReceived = 0;
                info.BytesSent = 0;
            }

            adapters.Add(info);
        }

        return adapters.OrderByDescending(adapter => adapter.Status == "Up")
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static NetworkAdapterInfo? FindAdapter(string? id) =>
        GetAdapters().FirstOrDefault(adapter => !string.IsNullOrWhiteSpace(id) && adapter.Id == id);

    public static NetworkAdapterInfo? FindDefaultAdapter()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                if (networkInterface.GetIPProperties().GatewayAddresses.Any(
                        gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                                   !gateway.Address.Equals(IPAddress.Any)))
                {
                    return GetAdapters().FirstOrDefault(adapter => adapter.Id == networkInterface.Id);
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return null;
    }

    public static bool IsValidHost(string host)
    {
        var candidate = host.Trim();
        return candidate.Length is > 0 and <= 253 &&
               !candidate.Any(char.IsWhiteSpace) &&
               (IPAddress.TryParse(candidate, out _) ||
                Uri.CheckHostName(candidate) == UriHostNameType.Dns);
    }

    public static async Task<DiagnosticResult> MeasureAsync(
        string host,
        int sampleCount = 5,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidHost(host))
        {
            throw new ArgumentException("Enter a valid IP address or DNS host name.", nameof(host));
        }

        if (sampleCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "Sample count must be between 1 and 100.");
        }

        var result = new DiagnosticResult
        {
            Target = host.Trim(),
            Timestamp = DateTimeOffset.Now,
            Sent = sampleCount
        };
        var latencies = new List<double>();
        var timeoutMilliseconds = timeout ?? TimeSpan.FromSeconds(2);

        for (var index = 0; index < sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(
                    result.Target,
                    timeoutMilliseconds,
                    PingPayload,
                    new PingOptions(128, true),
                    cancellationToken);
                if (reply.Status == IPStatus.Success)
                {
                    latencies.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
            }
            catch (SocketException)
            {
            }

            if (index + 1 < sampleCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        result.Received = latencies.Count;
        result.PacketLossPercent = 100d * (sampleCount - latencies.Count) / sampleCount;
        if (latencies.Count > 0)
        {
            result.AverageLatencyMs = latencies.Average();
            result.JitterMs = latencies.Count > 1
                ? latencies.Zip(latencies.Skip(1), (previous, next) => Math.Abs(next - previous)).Average()
                : null;
        }
        else
        {
            result.Error = $"No ICMP replies were received from {result.Target}.";
        }

        return result;
    }

    public static async Task<DnsResolutionResult> ResolveDnsAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidHost(host))
        {
            throw new ArgumentException("Enter a valid IP address or DNS host name.", nameof(host));
        }

        var result = new DnsResolutionResult { Host = host.Trim() };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = IPAddress.TryParse(result.Host, out var address)
                ? [address]
                : await Dns.GetHostAddressesAsync(result.Host, cancellationToken);
            result.Addresses = addresses.Select(value => value.ToString()).Distinct().ToList();
        }
        catch (SocketException exception)
        {
            result.Error = exception.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        return result;
    }

    public static async Task<IReadOnlyList<RouteHop>> TraceRouteAsync(
        string host,
        int maximumHops = 30,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidHost(host))
        {
            throw new ArgumentException("Enter a valid IP address or DNS host name.", nameof(host));
        }

        var addresses = IPAddress.TryParse(host.Trim(), out var parsedAddress)
            ? [parsedAddress]
            : await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
        var target = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault()
                     ?? throw new SocketException((int)SocketError.HostNotFound);
        var hops = new List<RouteHop>();
        var timeoutMilliseconds = timeout ?? TimeSpan.FromSeconds(1.5);

        for (var hop = 1; hop <= maximumHops; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ping = new Ping();
            var stopwatch = Stopwatch.StartNew();
            var entry = new RouteHop { Hop = hop };
            try
            {
                var reply = await ping.SendPingAsync(
                    target,
                    timeoutMilliseconds,
                    PingPayload,
                    new PingOptions(hop, true),
                    cancellationToken);
                stopwatch.Stop();
                entry.Address = reply.Address?.ToString() ?? "*";
                entry.LatencyMs = reply.Status is IPStatus.Success or IPStatus.TtlExpired
                    ? stopwatch.Elapsed.TotalMilliseconds
                    : null;
                entry.Status = reply.Status.ToString();
                hops.Add(entry);
                if (reply.Status == IPStatus.Success)
                {
                    break;
                }
            }
            catch (PingException exception)
            {
                stopwatch.Stop();
                entry.Status = exception.InnerException?.Message ?? exception.Message;
                hops.Add(entry);
            }
            catch (SocketException exception)
            {
                stopwatch.Stop();
                entry.Status = exception.Message;
                hops.Add(entry);
            }
        }

        return hops;
    }

    public static bool IsFortniteRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.ProcessName.StartsWith(
                        "FortniteClient-Win64-Shipping",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string FormatRate(double bytesPerSecond)
    {
        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var rate = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1)
        {
            rate /= 1024;
            unit++;
        }

        return $"{rate:0.0} {units[unit]}";
    }

    private static string JoinOrUnavailable(IEnumerable<string> values)
    {
        var result = string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(result) ? "Unavailable" : result;
    }
}
