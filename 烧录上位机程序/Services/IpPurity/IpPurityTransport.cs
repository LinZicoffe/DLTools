using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

public interface IIpPurityTransport
{
    /// <summary>失败变成结果。只有调用方取消才抛出 OperationCanceledException。</summary>
    Task<IpPurityResponse> SendAsync(IpPurityRequest request, CancellationToken cancellationToken);
}

/// <summary>检测专用 HTTP。跟随系统代理，使用诚实的 UA，不携带 Cookie，不跟随重定向。
/// 任何传输异常都收成“无结果”，避免一条坏链路中断整次检测。</summary>
public sealed class IpPurityTransport : IIpPurityTransport, IDisposable
{
    public const string UserAgent = "DLTools/1.0";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(9);
    public const int MaxBodyBytes = 1_048_576;

    private readonly HttpClient _client;

    public IpPurityTransport()
        : this(CreateHandler())
    {
    }

    public IpPurityTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
    }

    public async Task<IpPurityResponse> SendAsync(IpPurityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = IpPurityEndpoints.EnsureAllowed(request.Url);
        var timeout = request.Timeout is { } given && given > TimeSpan.Zero ? given : DefaultTimeout;
        var started = Stopwatch.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Version = HttpVersion.Version11;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            if (request.Headers is not null)
            {
                foreach (var header in request.Headers)
                {
                    if (IsReservedHeader(header.Key)) continue;
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var text = await ReadBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
            var latency = Elapsed(started);
            if (text is null)
            {
                return Fail(status, "NETWORK", latency, "返回内容过大");
            }

            JsonElement? data = null;
            string? errorCode = null;
            if (request.ExpectJson)
            {
                if (text.Length == 0)
                {
                    data = null;
                }
                else
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        data = document.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        errorCode = status is >= 200 and <= 299 ? "PARSE" : "HTTP_" + status;
                    }
                }
            }

            if (errorCode is null && status is < 200 or > 299) errorCode = "HTTP_" + status;
            var ok = status is >= 200 and <= 299 && errorCode is null;
            return new IpPurityResponse(
                status,
                text,
                data,
                latency,
                errorCode is null ? null : new IpPurityError(errorCode, IpPurityParse.DescribeError(errorCode)),
                ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(0, "TIMEOUT", Elapsed(started));
        }
        catch (Exception)
        {
            return Fail(0, "NETWORK", Elapsed(started));
        }
    }

    public void Dispose() => _client.Dispose();

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (memory.Length <= MaxBodyBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
        }
        if (memory.Length > MaxBodyBytes) return null;
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IpPurityResponse Fail(int status, string code, long latency, string? message = null) =>
        new(status, null, null, latency, new IpPurityError(code, message ?? IpPurityParse.DescribeError(code)), false);

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static bool IsReservedHeader(string name) =>
        name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(9),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
}
