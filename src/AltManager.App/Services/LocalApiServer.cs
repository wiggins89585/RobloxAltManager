using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltManager.App.Services;

/// <summary>
/// A tiny loopback-only HTTP API for external automation (Stream Deck, scripts, hotkey tools).
/// It binds to 127.0.0.1 only — nothing off this machine can reach it — and every data or
/// command endpoint requires a bearer token, so other local apps need the token too.
/// Implemented over a raw <see cref="TcpListener"/> so it needs no HTTP.sys URL reservation
/// (and therefore no admin rights) to run.
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string AppVersion =
        typeof(LocalApiServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly ILocalApiBackend _backend;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    public LocalApiServer(ILocalApiBackend backend) => _backend = backend;

    /// <summary>Starts (or restarts) the server. Throws if the port is already in use.</summary>
    public void Start(int port, string token)
    {
        Stop();
        _token = token ?? "";
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        _cts = cts;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener, cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; } // listener stopped / cancelled
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct);
                if (request is null) return;
                var (status, body) = await RouteAsync(request);
                await WriteResponseAsync(stream, status, body, ct);
            }
            catch { /* never let a malformed client crash the app */ }
        }
    }

    private async Task<(int status, object body)> RouteAsync(HttpRequest req)
    {
        var path = req.Path.Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";

        // /ping is unauthenticated so a client can confirm the app is up before sending the token.
        if (req.Method == "GET" && path == "/ping")
            return (200, new { ok = true, app = "RobloxAltManager", version = AppVersion });

        if (!Authorized(req))
            return (401, new { ok = false, error = "Unauthorized — send header 'Authorization: Bearer <token>'." });

        try
        {
            switch (req.Method, path)
            {
                case ("GET", "/accounts"):
                    return (200, new { ok = true, accounts = await _backend.GetAccountsAsync() });

                case ("GET", "/groups"):
                    return (200, new { ok = true, groups = await _backend.GetGroupsAsync() });

                case ("POST", "/launch"):
                {
                    ApiLaunchRequest? body;
                    try { body = JsonSerializer.Deserialize<ApiLaunchRequest>(req.Body, Json); }
                    catch { return (400, new { ok = false, error = "Invalid JSON body." }); }
                    var result = await _backend.LaunchAsync(body ?? new ApiLaunchRequest(null, null, null));
                    return (result.Ok ? 200 : 400, new { ok = result.Ok, launched = result.Count, message = result.Message });
                }

                default:
                    return (404, new { ok = false, error = "Unknown endpoint. Try GET /ping, GET /accounts, GET /groups, POST /launch." });
            }
        }
        catch (Exception ex)
        {
            return (500, new { ok = false, error = ex.Message });
        }
    }

    private bool Authorized(HttpRequest req)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        if (!req.Headers.TryGetValue("Authorization", out var auth)) return false;
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var provided = Encoding.UTF8.GetBytes(auth[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(_token);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    // ---------------- Minimal HTTP/1.1 parsing (loopback, small requests) ----------------

    private sealed record HttpRequest(string Method, string Path,
        Dictionary<string, string> Headers, string Body);

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var header = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return null;
            header.Write(buffer, 0, read);
            headerEnd = IndexOfDoubleCrlf(header.GetBuffer(), (int)header.Length);
            if (header.Length > 64 * 1024) return null; // oversized header
        }

        var raw = header.GetBuffer();
        var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx > 0) headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n)
            ? Math.Clamp(n, 0, 1024 * 1024)
            : 0;

        using var bodyBuf = new MemoryStream();
        var bodyStart = headerEnd + 4;
        var already = (int)header.Length - bodyStart;
        if (already > 0) bodyBuf.Write(raw, bodyStart, Math.Min(already, contentLength));
        while (bodyBuf.Length < contentLength)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            bodyBuf.Write(buffer, 0, Math.Min(read, contentLength - (int)bodyBuf.Length));
        }

        var body = Encoding.UTF8.GetString(bodyBuf.GetBuffer(), 0, (int)bodyBuf.Length);
        return new HttpRequest(requestLine[0].ToUpperInvariant(), requestLine[1], headers, body);
    }

    private static int IndexOfDoubleCrlf(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, object body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized",
            404 => "Not Found", 500 => "Internal Server Error", _ => "OK"
        };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   "Content-Type: application/json\r\n" +
                   $"Content-Length: {json.Length}\r\n" +
                   "Connection: close\r\n" +
                   "Cache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(json, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Generates a random URL-safe token for first-time setup.</summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
