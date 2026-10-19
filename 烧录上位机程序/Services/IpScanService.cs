using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace 烧录上位机程序.Services;

public enum IpProbeStatus
{
    Reachable,
    TimedOut,
    Unreachable
}

public sealed record IpProbeResult(IpProbeStatus Status, long? RoundtripTimeMs = null);
public sealed record IpScanProgress(int Completed, IPAddress Address, IpProbeResult Result);

public interface IIpScanService
{
    Task ScanAsync(string input, Func<IpScanProgress, Task> reportAsync, CancellationToken cancellationToken);
}

/// <summary>Fixed /24 ICMP scanner. Callbacks are serialized and awaited, including during cancellation.</summary>
public sealed class IpScanService : IIpScanService
{
    public const int HostCount = 254;
    public const int MaxConcurrency = 32;
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Func<IPAddress, TimeSpan, CancellationToken, Task<IpProbeResult>> _probeAsync;
    private readonly Action<IPAddress> _ensureNetwork;

    public IpScanService(
        Func<IPAddress, TimeSpan, CancellationToken, Task<IpProbeResult>>? probeAsync = null,
        Action<IPAddress>? ensureNetwork = null)
    {
        _probeAsync = probeAsync ?? PingAsync;
        _ensureNetwork = ensureNetwork ?? EnsureNetworkAvailable;
    }

    public static bool TryParseInput(string? input, out IPAddress address, out string error)
    {
        address = IPAddress.None;
        error = "请输入完整 IPv4 地址（例如 192.168.1.99）；仅支持 IPv4 /24 网段。";
        var parts = input?.Trim().Split('.');
        if (parts is not { Length: 4 }) return false;
        var bytes = new byte[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is < 1 or > 3 || parts[i].Any(c => c is < '0' or > '9')
                || !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
        }
        if (bytes[0] is 0 or 127 or >= 224)
        {
            error = "不支持未指定、回环、组播或保留地址；请输入局域网 IPv4 地址，仅扫描 /24 网段。";
            return false;
        }
        address = new IPAddress(bytes);
        error = "";
        return true;
    }

    public static IReadOnlyList<IPAddress> EnumerateSubnet(string input)
    {
        if (!TryParseInput(input, out var address, out var error)) throw new ArgumentException(error, nameof(input));
        var bytes = address.GetAddressBytes();
        return Enumerable.Range(1, HostCount)
            .Select(host => new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], (byte)host })).ToArray();
    }

    public static IpProbeResult ClassifyReply(IPStatus status, long roundtripTimeMs) => status switch
    {
        IPStatus.Success => new(IpProbeStatus.Reachable, roundtripTimeMs),
        IPStatus.TimedOut => new(IpProbeStatus.TimedOut),
        _ => new(IpProbeStatus.Unreachable)
    };

    public async Task ScanAsync(string input, Func<IpScanProgress, Task> reportAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportAsync);
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = EnumerateSubnet(input);
        _ensureNetwork(addresses[0]);
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(MaxConcurrency);
        using var reports = new SemaphoreSlim(1);
        var completed = 0;
        // WhenAll drains every worker before the caller can release host ownership or start another scan.
        await Task.WhenAll(addresses.Select(ScanOneAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        async Task ScanOneAsync(IPAddress address)
        {
            await slots.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                IpProbeResult result;
                try
                {
                    result = await _probeAsync(address, ProbeTimeout, stop.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    result = new(IpProbeStatus.TimedOut);
                }
                await reports.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    await reportAsync(new(++completed, address, result)).ConfigureAwait(false);
                }
                finally { reports.Release(); }
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally { slots.Release(); }
        }
    }

    private static async Task<IpProbeResult> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken token)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return ClassifyReply(reply.Status, reply.RoundtripTime);
        }
        catch (PingException error)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("无法发送 ICMP 探测，请检查网卡连接、路由或系统 ICMP 权限。", error);
        }
    }

    private static void EnsureNetworkAvailable(IPAddress target)
    {
        try
        {
            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)
                    && address.GetAddressBytes()[0] is > 0 and < 224)
                .ToArray();
            if (localAddresses.Length == 0)
                throw new InvalidOperationException("未找到在线的 IPv4 网卡，请连接有线网络或 Wi-Fi 后重试。");
            var prefix = target.GetAddressBytes().Take(3);
            if (!localAddresses.Any(address => address.GetAddressBytes().Take(3).SequenceEqual(prefix)))
                throw new InvalidOperationException("本机没有与目标 /24 网段匹配的在线 IPv4 网卡，请检查输入地址和网卡配置。");
        }
        catch (NetworkInformationException error)
        {
            throw new InvalidOperationException("无法读取网卡信息，请检查系统网络服务和网卡状态。", error);
        }
    }
}
