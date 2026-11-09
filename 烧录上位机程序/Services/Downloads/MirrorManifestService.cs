using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>镜像清单服务：拉取并强校验 manifest.json（每条目必须带 size 与 sha256——缺校验值的清单直接拒绝），
/// 节点竞速排序（bytes=0-0 计时），生成带镜像 failover 与强制校验的 DownloadOptions。
/// 清单与节点地址必须 https 且公网（DownloadUrlGuard.EnsureManifestUrl）。</summary>
public sealed class MirrorManifestService
{
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan NodeProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDownloadTransport _transport;
    private readonly Func<long> _clock;
    private readonly TimeSpan _nodeProbeTimeout;

    public MirrorManifestService(IDownloadTransport? transport = null, Func<long>? clock = null,
        TimeSpan? nodeProbeTimeout = null)
    {
        _transport = transport ?? new DownloadHttpTransport();
        _clock = clock ?? Stopwatch.GetTimestampMilliseconds;
        _nodeProbeTimeout = nodeProbeTimeout ?? NodeProbeTimeout;
    }

    public async Task<MirrorManifest> FetchAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        DownloadUrlGuard.EnsureManifestUrl(manifestUrl); // https + 非 localhost/环回/私网/保留地址（重定向逐跳复检）
        await using var response = await _transport.SendAsync(
            new TransportRequest(manifestUrl.Trim(), null, null, null, EnforcePublicHttps: true),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is < 200 or > 299)
            throw new DownloadTransportException($"清单下载失败（HTTP {response.StatusCode}），请稍后重试或更换清单地址。",
                response.StatusCode, terminal: response.StatusCode is 403 or 404 or 410);
        if (response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new DownloadTransportException("清单地址返回网页而非 JSON，请核对地址。", terminal: true);

        var payload = await ReadCappedAsync(response.Content, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        MirrorManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MirrorManifestDocument>(payload, JsonOptions);
        }
        catch (JsonException error)
        {
            throw new DownloadTransportException("清单不是合法的 JSON：" + error.Message, terminal: true);
        }
        return Validate(document, manifestUrl);
    }

    internal static MirrorManifest Validate(MirrorManifestDocument? document, string manifestUrl)
    {
        if (document is null || document.Files is null || document.Files.Count == 0)
            throw new DownloadTransportException("清单为空或缺少 files 数组。", terminal: true);
        if (document.SchemaVersion != MirrorManifest.CurrentSchemaVersion)
            throw new DownloadTransportException(
                $"清单版本不支持（{document.SchemaVersion}，期望 {MirrorManifest.CurrentSchemaVersion}）。", terminal: true);

        var entries = new List<MirrorFileEntry>();
        foreach (var file in document.Files)
        {
            var name = (file.Name ?? "").Trim();
            if (name.Length == 0) throw new DownloadTransportException("清单存在缺少 name 的条目。", terminal: true);
            if (file.Size <= 0) throw new DownloadTransportException($"清单条目 {name} 缺少有效的 size。", terminal: true);
            var sha256 = (file.Sha256 ?? "").Trim();
            if (sha256.Length != 64 || sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
                throw new DownloadTransportException($"清单条目 {name} 缺少有效的 sha256（64 位十六进制）。", terminal: true);
            var urls = file.Urls ?? [];
            var allowed = DownloadUrlGuard.FilterMirrorUrls(urls);
            if (allowed.Count == 0)
                throw new DownloadTransportException(
                    $"清单条目 {name} 没有可用的 https 公网节点（共 {urls.Count} 个地址全部被拒）。", terminal: true);
            entries.Add(new MirrorFileEntry(name, file.Size, sha256.ToLowerInvariant(), allowed));
        }
        return new MirrorManifest(MirrorManifest.CurrentSchemaVersion, entries);
    }

    /// <summary>节点竞速：并发对每个 URL 发 bytes=0-0 计时，按时延升序排列；失败节点保持原顺序排在末尾。
    /// 全部失败时返回原顺序（交给引擎重试/failover 报真实错误）。</summary>
    public async Task<IReadOnlyList<string>> OrderUrlsByLatencyAsync(MirrorFileEntry entry,
        CancellationToken cancellationToken)
    {
        var urls = entry.Urls;
        if (urls.Count <= 1) return urls;

        var measurements = await Task.WhenAll(urls.Select(url => MeasureAsync(url, cancellationToken)))
            .ConfigureAwait(false);
        var ordered = measurements.Where(item => item.LatencyMilliseconds.HasValue)
                .OrderBy(item => item.LatencyMilliseconds!.Value)
                .Select(item => item.Url)
            .Concat(measurements.Where(item => !item.LatencyMilliseconds.HasValue).Select(item => item.Url))
            .ToArray();
        return ordered;
    }

    /// <summary>清单条目 → 下载选项：强制条目 sha256（镜像文件必验），镜像按竞速顺序挂接 failover。</summary>
    public DownloadOptions BuildOptions(MirrorFileEntry entry, string destinationDirectory,
        int? connections = null, long? maxRateKBps = null, bool startPaused = false,
        IReadOnlyList<string>? orderedUrls = null) =>
        new()
        {
            Url = (orderedUrls ?? entry.Urls)[0],
            MirrorUrls = (orderedUrls ?? entry.Urls).Skip(1).ToArray(),
            DestinationDirectory = destinationDirectory,
            Connections = connections,
            MaxRateKBps = maxRateKBps,
            ExpectedSha256 = entry.Sha256, // 清单分发强制校验
            StartPaused = startPaused,
            EnforcePublicHttps = true // 清单链路的全部请求（含重定向）保持 https 公网要求
        };

    private async Task<NodeMeasurement> MeasureAsync(string url, CancellationToken cancellationToken)
    {
        var started = _clock();
        try
        {
            await using var response = await _transport.SendAsync(
                    new TransportRequest(url, 0, 0, null, EnforcePublicHttps: true), cancellationToken)
                .WaitAsync(_nodeProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is < 200 or > 299) return new(url, null);
            var elapsed = _clock() - started;
            return new(url, elapsed);
        }
        // WaitAsync 超时抛 TimeoutException（半死节点：TCP 可连但 5 秒不回头）——同样视为该节点失败，
        // 不能让单个节点的超时把整个"导入所选"批处理打断。
        // 注：OCE 在此按"该节点失败"优雅降级（沉底）；当前调用方传 CancellationToken.None，
        // 若未来引入真实取消令牌需先让 OCE 上抛而非吞掉。
        catch (Exception error) when (error is OperationCanceledException or DownloadTransportException
            or TimeoutException)
        {
            return new(url, null);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[cap];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (chunk == 0) break;
            read += chunk;
        }
        if (read == buffer.Length)
            throw new DownloadTransportException($"清单超过 {cap / (1024 * 1024)} MB 上限，疑似地址错误。", terminal: true);
        return buffer[..read];
    }

    private sealed record NodeMeasurement(string Url, long? LatencyMilliseconds);

    private static class Stopwatch
    {
        public static long GetTimestampMilliseconds() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }
}
