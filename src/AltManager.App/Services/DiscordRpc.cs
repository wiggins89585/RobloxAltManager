using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AltManager.App.Services;

/// <summary>
/// Discord Rich Presence over the Discord app's local IPC pipe (no bot token, no library). Shows
/// "Alt Manager" with how many clients are running and a timer. Needs a Discord Application ID, which
/// the user creates for free at discord.com/developers. Nothing leaves the PC except to Discord itself.
/// </summary>
public sealed class DiscordRpc : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private string? _appId;
    private readonly long _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _lastPayload = "";

    public string Status { get; private set; } = "Off";

    /// <summary>Sets the presence. Safe to call often; only changes are sent. Never throws.</summary>
    public async Task UpdateAsync(string appId, string details, string state)
    {
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            if (_pipe is null || !_pipe.IsConnected || _appId != appId)
            {
                Close();
                if (!await ConnectAsync(appId)) return;
                _lastPayload = "";
            }
            var activity = new
            {
                details,
                state,
                timestamps = new { start = _start },
                assets = new { large_text = "Alt Manager" },
            };
            var payload = JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = Guid.NewGuid().ToString(),
            });
            var key = details + "|" + state;
            if (key == _lastPayload) return;
            await WriteAsync(1, payload);
            _lastPayload = key;
            Status = "Connected";
        }
        catch { Close(); Status = "Discord not reachable"; }
        finally { _lock.Release(); }
    }

    public async Task ClearAsync()
    {
        if (_pipe is not { IsConnected: true }) return;
        try
        {
            await WriteAsync(1, JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId }, nonce = Guid.NewGuid().ToString()
            }));
        }
        catch { }
        Close();
        Status = "Off";
    }

    private async Task<bool> ConnectAsync(string appId)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300);
                _pipe = pipe;
                _appId = appId;
                await WriteAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = appId }));
                await ReadAsync(); // the READY reply
                return true;
            }
            catch { pipe.Dispose(); }
        }
        Status = "Discord isn't running";
        return false;
    }

    private async Task WriteAsync(int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame);
        await _pipe.FlushAsync();
    }

    private async Task ReadAsync()
    {
        var header = new byte[8];
        using var cts = new CancellationTokenSource(2000);
        await _pipe!.ReadExactlyAsync(header, cts.Token);
        var len = BitConverter.ToInt32(header, 4);
        if (len is > 0 and < 65536) await _pipe.ReadExactlyAsync(new byte[len], cts.Token);
    }

    private void Close()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose() => Close();
}
