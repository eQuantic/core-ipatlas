using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Fetches many small files from many hosts without leaning on any of them.
/// <para>
/// A global concurrency cap is not enough, because the URLs are not spread
/// evenly. One operator publishes 460 separate geofeeds on a single host, and
/// eleven hosts carry more than twenty each — so a cap of twenty-four in total
/// can still mean twenty-four simultaneous requests at one small server that
/// published a geofeed as a courtesy. The limit that matters is per host.
/// </para>
/// <para>
/// It also remembers what each URL last returned. These files barely change
/// between monthly runs, and a conditional request lets a publisher answer
/// "still the same" in a header instead of sending the body again.
/// </para>
/// </summary>
public sealed class PoliteFetcher : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client;
    private readonly string? _cache;
    private readonly int _perHostLimit;
    private readonly int _attempts;

    /// <summary>Bodies served from cache because the publisher said nothing changed.</summary>
    public int NotModified => _notModified;

    private int _notModified;

    /// <summary>Creates a fetcher.</summary>
    /// <param name="timeout">Per-request timeout.</param>
    /// <param name="attempts">How many times to ask before giving up.</param>
    /// <param name="perHostLimit">Simultaneous requests allowed to any one host.</param>
    /// <param name="cacheDirectory">Where to remember bodies and validators, or null to not.</param>
    /// <param name="accept">A media type to ask for, when the endpoint expects one.</param>
    public PoliteFetcher(TimeSpan timeout, int attempts, int perHostLimit, string? cacheDirectory, string? accept = null)
    {
        _client = new HttpClient { Timeout = timeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("eQuantic.IpAtlas.Compiler");
        if (accept is { Length: > 0 })
        {
            _client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        }

        _client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        _attempts = attempts;
        _perHostLimit = perHostLimit;
        _cache = cacheDirectory;

        if (_cache is not null)
        {
            Directory.CreateDirectory(_cache);
        }
    }

    /// <summary>Fetches one URL, or null when it could not be read.</summary>
    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var gate = _perHost.GetOrAdd(uri.Host, _ => new SemaphoreSlim(_perHostLimit, _perHostLimit));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var entry = ReadCache(url);

        for (var attempt = 1; attempt <= _attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (entry is { } known)
                {
                    if (known.ETag is { Length: > 0 } etag)
                    {
                        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                    }

                    if (known.LastModified is { Length: > 0 } modified)
                    {
                        request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
                    }
                }

                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && entry is { } cached)
                {
                    Interlocked.Increment(ref _notModified);
                    return cached.Body;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _attempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 5);
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                WriteCache(url, body, response);
                return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or UriFormatException)
            {
                if (attempt == _attempts || IsFinal(ex))
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a failure is worth asking about again. A refusal is an answer,
    /// and a hostname that does not resolve will not resolve on the third try.
    /// </summary>
    private static bool IsFinal(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500)
        {
            return true;
        }

        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                    or System.Net.Sockets.SocketError.NoData
                    or System.Net.Sockets.SocketError.ConnectionRefused
                    or System.Net.Sockets.SocketError.NetworkUnreachable;
            }
        }

        return false;
    }

    private readonly record struct CacheEntry(string? ETag, string? LastModified, string Body);

    private string? PathFor(string url)
    {
        if (_cache is null)
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(_cache, hash[..16]);
    }

    private CacheEntry? ReadCache(string url)
    {
        if (PathFor(url) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
            {
                return null;
            }

            string? etag = null, modified = null;
            foreach (var line in text[..split].Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    continue;
                }

                if (line.AsSpan(0, colon).Equals("etag", StringComparison.OrdinalIgnoreCase))
                {
                    etag = line[(colon + 1)..];
                }
                else if (line.AsSpan(0, colon).Equals("last-modified", StringComparison.OrdinalIgnoreCase))
                {
                    modified = line[(colon + 1)..];
                }
            }

            return new CacheEntry(etag, modified, text[(split + 2)..]);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteCache(string url, string body, HttpResponseMessage response)
    {
        if (PathFor(url) is not { } path)
        {
            return;
        }

        var headers = new StringBuilder();
        if (response.Headers.ETag?.Tag is { Length: > 0 } etag)
        {
            headers.Append("etag:").Append(etag).Append('\n');
        }

        if (response.Content.Headers.LastModified is { } modified)
        {
            headers.Append("last-modified:").Append(modified.ToString("R")).Append('\n');
        }

        try
        {
            File.WriteAllText(path, headers.Append('\n').Append(body).ToString());
        }
        catch (IOException)
        {
            // A cache that cannot be written is a slower run, not a failed one.
        }
    }

    /// <summary>Releases the underlying client and the per-host gates.</summary>
    public void Dispose()
    {
        _client.Dispose();
        foreach (var gate in _perHost.Values)
        {
            gate.Dispose();
        }
    }
}
