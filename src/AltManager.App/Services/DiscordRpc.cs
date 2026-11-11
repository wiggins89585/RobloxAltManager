using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AltManager.App.Services;

/// <summary>
/// Discord Rich Presence over the Discord app's local IPC pipe (no bot token, no library). Shows
/// "Alt Manager" with how many clients are running and a timer. Needs a Discord Application ID, which
/// the user creates for free at discord.com/developers. Nothing leaves the PC except to Discord itself.
///
/// Discord's replies are read on a background loop, so a rejected update or a dropped connection shows
/// up in <see cref="Status"/>. The presence is re-sent every minute even when nothing changed: that
/// notices a dead pipe (Discord restarted) and reconnects, instead of silently showing nothing.
/// </summary>
public sealed class DiscordRpc : IDisposable
{
    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(60);

    private NamedPipeClientStream? _pipe;
    private string? _appId;
    private readonly long _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _lastKey = "";
    private DateTime _lastSent;
    private DateTime _retryAfter;
    private CancellationTokenSource? _readCts;

    public string Status { get; private set; } = "Off";

    /// <summary>Raised when <see cref="Status"/> changes (from any thread).</summary>
    public event Action<string>? StatusChanged;

    private void SetStatus(string s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>What to show on the profile card. Empty strings are left out.</summary>
    public sealed record Activity(
        string Details,
        string State,
        string Type = "Watching",
        long? Start = null,
        string? LargeImage = null, string? LargeText = null,
        string? SmallImage = null, string? SmallText = null,
        int PartySize = 0, int PartyMax = 0,
        string? ButtonLabel = null, string? ButtonUrl = null);

    /// <summary>Sets the presence. Safe to call often; only changes (and a once-a-minute refresh) are sent. Never throws.</summary>
    public async Task UpdateAsync(string appId, Activity a)
    {
        if (!await _lock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_pipe is not { IsConnected: true } || _appId != appId)
            {
                Close();
                if (DateTime.UtcNow < _retryAfter) return;          // don't hammer Discord when it isn't there
                if (!await ConnectAsync(appId).ConfigureAwait(false)) { _retryAfter = DateTime.UtcNow.AddSeconds(15); return; }
                _lastKey = "";
            }

            var activity = BuildActivity(a);
            var key = JsonSerializer.Serialize(activity);
            if (key == _lastKey && DateTime.UtcNow - _lastSent < Resend) return;

            var payload = JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = Guid.NewGuid().ToString(),
            });
            await WriteAsync(1, payload).ConfigureAwait(false);
            _lastKey = key;
            _lastSent = DateTime.UtcNow;
        }
        catch
        {
            Close();
            SetStatus("Lost the connection to Discord — reconnecting…");
        }
        finally { _lock.Release(); }
    }

    /// <summary>Discord's activity object. Fields Discord would reject (too short/long, empty) are dropped.</summary>
    private Dictionary<string, object> BuildActivity(Activity a)
    {
        static string? Fit(string? s, int max) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Length < 2 ? s + " " : s.Length > max ? s[..(max - 1)] + "…" : s;

        var act = new Dictionary<string, object>
        {
            ["type"] = a.Type switch { "Playing" => 0, "Competing" => 5, _ => 3 },
            ["timestamps"] = new Dictionary<string, object> { ["start"] = a.Start ?? _start },
            ["instance"] = false,
        };
        if (Fit(a.Details, 128) is { } d) act["details"] = d;
        if (Fit(a.State, 128) is { } st) act["state"] = st;

        var assets = new Dictionary<string, object>();
        if (Fit(a.LargeImage, 300) is { } li) assets["large_image"] = li;
        if (Fit(a.LargeText, 128) is { } lt) assets["large_text"] = lt;
        if (Fit(a.SmallImage, 300) is { } si) assets["small_image"] = si;
        if (Fit(a.SmallText, 128) is { } stx) assets["small_text"] = stx;
        if (assets.Count > 0) act["assets"] = assets;

        if (a.PartySize > 0 && a.PartyMax >= a.PartySize)
            act["party"] = new Dictionary<string, object> { ["id"] = "alt-manager", ["size"] = new[] { a.PartySize, a.PartyMax } };

        if (Fit(a.ButtonLabel, 32) is { } bl && Uri.TryCreate(a.ButtonUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            act["buttons"] = new[] { new Dictionary<string, string> { ["label"] = bl, ["url"] = u.ToString() } };
        return act;
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true })
            {
                try
                {
                    await WriteAsync(1, JsonSerializer.Serialize(new
                    {
                        cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId }, nonce = Guid.NewGuid().ToString()
                    })).ConfigureAwait(false);
                }
                catch { }
            }
            Close();
            _lastKey = "";
            SetStatus("Off");
        }
        finally { _lock.Release(); }
    }

    private async Task<bool> ConnectAsync(string appId)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300).ConfigureAwait(false);
            }
            catch { pipe.Dispose(); continue; }

            try
            {
                _pipe = pipe;
                _appId = appId;
                await WriteAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = appId })).ConfigureAwait(false);
                using var cts = new CancellationTokenSource(3000);
                var (op, json) = await ReadFrameAsync(pipe, cts.Token).ConfigureAwait(false);
                if (op == 2) // Discord closed the handshake: usually a wrong Application ID
                {
                    Close();
                    SetStatus("Discord refused the connection: " + (Message(json) ?? "check the Application ID"));
                    return false;
                }
                SetStatus("Connected to Discord");
                _readCts = new CancellationTokenSource();
                _ = ReadLoopAsync(pipe, _readCts.Token);
                return true;
            }
            catch
            {
                Close();
            }
        }
        SetStatus("Discord isn't running (open the Discord desktop app)");
        return false;
    }

    /// <summary>Reads Discord's replies: confirms the presence, reports errors, answers pings, notices a closed pipe.</summary>
    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, json) = await ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                switch (op)
                {
                    case 1: // FRAME
                        using (var doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;
                            var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                            var cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                            if (evt == "ERROR")
                                SetStatus("Discord rejected the presence: " + (Message(json) ?? "unknown error"));
                            else if (cmd == "SET_ACTIVITY" && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                                SetStatus("Showing on your Discord profile");
                        }
                        break;
                    case 2: // CLOSE
                        SetStatus("Discord closed the connection: " + (Message(json) ?? "reconnecting…"));
                        CloseIf(pipe);
                        return;
                    case 3: // PING → PONG
                        await _lock.WaitAsync(ct).ConfigureAwait(false);
                        try { await WriteRawAsync(pipe, 4, json).ConfigureAwait(false); }
                        finally { _lock.Release(); }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!ct.IsCancellationRequested) SetStatus("Lost the connection to Discord — reconnecting…");
            CloseIf(pipe);
        }
    }

    private static string? Message(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("message", out var m)) return m.GetString();
            if (r.TryGetProperty("message", out var m2)) return m2.GetString();
        }
        catch { }
        return null;
    }

    private Task WriteAsync(int op, string json) => WriteRawAsync(_pipe!, op, json);

    private static async Task WriteRawAsync(Stream pipe, int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await pipe.WriteAsync(frame).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<(int Op, string Json)> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var op = BitConverter.ToInt32(header, 0);
        var len = BitConverter.ToInt32(header, 4);
        if (len is < 0 or > 1 << 20) throw new IOException("Bad frame from Discord.");
        var body = new byte[len];
        if (len > 0) await pipe.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return (op, Encoding.UTF8.GetString(body));
    }

    private void CloseIf(NamedPipeClientStream pipe)
    {
        if (ReferenceEquals(_pipe, pipe)) Close();
        else try { pipe.Dispose(); } catch { }
    }

    private void Close()
    {
        try { _readCts?.Cancel(); } catch { }
        _readCts = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose() => Close();
}
