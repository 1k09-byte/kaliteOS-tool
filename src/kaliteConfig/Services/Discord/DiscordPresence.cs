using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Services.Navigation;

namespace kaliteConfig.Services.Discord;

internal static class DiscordPresence
{
    private const string ClientId = "1564284317859979304";

    public static event Action? Changed;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
    private static string PresencePath => Path.Combine(DataDir, "discord-presence.txt");

    private const string InviteUrl = "https://discord.gg/faQgV6yayY";

    private static readonly int ProcessPid = Environment.ProcessId;
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending = new();
    private static readonly object PipeGate = new();
    private static readonly object ReaderGate = new();

    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static volatile bool _live;
    private static CancellationTokenSource? _monitorCts;
    private static NamedPipeClientStream? _pipe;
    private static Task? _reader;
    private static NamedPipeClientStream? _readerPipe;
    private static DateTimeOffset _lastSendUtc = DateTimeOffset.MinValue;
    private static Timer? _heartbeat;

    public static bool Enabled { get; private set; }
    public static string Status { get; private set; } = "Off";
    public static bool Configured => ClientId.Trim().Length > 2 && !ClientId.Contains("PASTE");

    static DiscordPresence()
    {
        try
        {
            if (File.Exists(PresencePath) && File.ReadAllText(PresencePath).Trim() == "1")
                Enabled = true;
            try { File.Delete(Path.Combine(DataDir, "discord-user.json")); } catch { }
            try { File.Delete(Path.Combine(DataDir, "discord-appid.txt")); } catch { }
            try { File.Delete(Path.Combine(DataDir, "discord-secret.txt")); } catch { }
        }
        catch { }
        if (Enabled)
        {
            SetStatus(Configured ? "Connecting..." : "Discord integration isn't configured yet.");
            StartMonitor();
        }
    }

    public static void UpdateActivity(PageKey key)
    {
        _ = Task.Run(SendPresenceAsync);
    }

    public static void SetEnabled(bool value)
    {
        Enabled = value;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(PresencePath, value ? "1" : "0");
        }
        catch { }
        if (!value)
        {
            StopMonitor();
            SetStatus("Off");
            _ = Task.Run(ClearAndCloseAsync);
            return;
        }
        if (!Configured)
        {
            SetStatus("Discord integration isn't configured yet.");
            return;
        }
        SetStatus("Connecting...");
        StartMonitor();
        _ = Task.Run(SendPresenceAsync);
        lock (PipeGate)
        {
            _heartbeat ??= new Timer(_ => ReassertActivity(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            _heartbeat.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
    }

    public static void ReassertActivity()
    {
        if (!Enabled || !Configured)
            return;
        if (DateTimeOffset.UtcNow - _lastSendUtc < TimeSpan.FromSeconds(30))
            return;
        _ = Task.Run(SendPresenceAsync);
    }

    private static void StartMonitor()
    {
        lock (PipeGate)
        {
            try { _monitorCts?.Cancel(); } catch { }
            _monitorCts = new CancellationTokenSource();
            CancellationToken ct = _monitorCts.Token;
            _ = Task.Run(() => MonitorLoopAsync(ct));
        }
    }

    private static void StopMonitor()
    {
        lock (PipeGate)
        {
            try { _monitorCts?.Cancel(); } catch { }
            _monitorCts = null;
        }
    }

    private static async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Enabled && Configured && !_live)
                    await SendPresenceAsync().ConfigureAwait(false);
            }
            catch { }
            try
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            catch { break; }
        }
    }

    private static async Task SendPresenceAsync()
    {
        if (!Enabled)
            return;
        if (!Configured)
        {
            _live = false;
            SetStatus("Discord integration isn't configured yet.");
            return;
        }
        if (!await SendGate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            _lastSendUtc = DateTimeOffset.UtcNow;
            if (!await EnsurePipeAsync().ConfigureAwait(false))
            {
                _live = false;
                SetStatus("Discord app not running.");
                return;
            }
            try
            {
                object activity = new
                {
                    state = "using kaliteOS",
                    buttons = new[] { new { label = "Join our Discord", url = InviteUrl } },
                };
                JsonElement reply = await SendCommandAsync("SET_ACTIVITY",
                    new { pid = ProcessPid, activity }, 15000).ConfigureAwait(false);
                if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("application_id", out _))
                {
                    _live = true;
                    SetStatus("Live on your profile.");
                }
                else
                {
                    _live = false;
                    SetStatus("Discord didn't accept the activity.");
                }
            }
            catch (Exception ex)
            {
                _live = false;
                SetStatus("Couldn't update (" + ex.Message + ").");
            }
        }
        finally
        {
            try { SendGate.Release(); } catch { }
        }
    }

    private static async Task ClearAndCloseAsync()
    {
        _live = false;
        try
        {
            if (Configured && await EnsurePipeAsync().ConfigureAwait(false))
                await SendCommandAsync("SET_ACTIVITY", new { pid = ProcessPid, activity = (object?)null }, 8000).ConfigureAwait(false);
        }
        catch { }
        lock (PipeGate)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
        foreach (string key in Pending.Keys)
            if (Pending.TryRemove(key, out var waiting))
                waiting.TrySetException(new IOException("Presence turned off."));
    }

    private static async Task<bool> EnsurePipeAsync()
    {
        if (!Configured)
            return false;
        lock (PipeGate)
        {
            if (_pipe is not null && _pipe.IsConnected)
                return true;
        }
        await ConnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (PipeGate)
            {
                if (_pipe is not null && _pipe.IsConnected)
                    return true;
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var cts = new CancellationTokenSource(900);
                    await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
                    string handshake = JsonSerializer.Serialize(new { v = 1, client_id = ClientId });
                    await WriteFrameAsync(pipe, 0, handshake).ConfigureAwait(false);
                    lock (PipeGate)
                        _pipe = pipe;
                    StartReaderFor(pipe);
                    return true;
                }
                catch
                {
                    try { pipe.Dispose(); } catch { }
                }
            }
            return false;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    private static void StartReaderFor(NamedPipeClientStream pipe)
    {
        lock (ReaderGate)
        {
            if (_reader is not null && !_reader.IsCompleted && ReferenceEquals(_readerPipe, pipe))
                return;
            _readerPipe = pipe;
            _reader = Task.Run(() => ReadLoopAsync(pipe));
        }
    }

    private static async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            var header = new byte[8];
            while (true)
            {
                lock (PipeGate)
                {
                    if (!ReferenceEquals(_pipe, pipe))
                        return;
                }
                await pipe.ReadExactlyAsync(header, 0, 8).ConfigureAwait(false);
                int length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 65536)
                    return;
                var payload = new byte[length];
                if (length > 0)
                    await pipe.ReadExactlyAsync(payload, 0, length).ConfigureAwait(false);
                DispatchFrame(Encoding.UTF8.GetString(payload));
            }
        }
        catch { }
        finally
        {
            lock (ReaderGate)
            {
                if (ReferenceEquals(_readerPipe, pipe))
                    _readerPipe = null;
            }
            lock (PipeGate)
            {
                if (ReferenceEquals(_pipe, pipe))
                    _pipe = null;
                try { pipe.Dispose(); } catch { }
            }
            foreach (string key in Pending.Keys)
                if (Pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(new IOException("Discord closed the connection."));
            if (Enabled && Configured)
            {
                _live = false;
                SetStatus("Disconnected. Reopens with Discord.");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000).ConfigureAwait(false);
                        if (Enabled && Configured && !_live)
                            await SendPresenceAsync().ConfigureAwait(false);
                    }
                    catch { }
                });
            }
        }
    }

    private static void DispatchFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("nonce", out var nonceEl))
                return;
            string? nonce = nonceEl.GetString();
            if (nonce is null || !Pending.TryRemove(nonce, out var tcs))
                return;
            if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
            {
                string message = "Denied in Discord.";
                if (root.TryGetProperty("data", out var err)
                    && err.TryGetProperty("message", out var msg)
                    && msg.GetString() is string text
                    && text.Length > 0)
                    message = text;
                tcs.TrySetException(new DiscordRpcException(message));
                return;
            }
            JsonElement data = root.TryGetProperty("data", out var dataEl) ? dataEl.Clone() : default;
            tcs.TrySetResult(data);
        }
        catch { }
    }

    private static async Task<JsonElement> SendCommandAsync(string cmd, object args, int timeoutMs)
    {
        if (!await EnsurePipeAsync().ConfigureAwait(false))
            throw new IOException("Discord app not running.");
        NamedPipeClientStream? pipe;
        lock (PipeGate)
            pipe = _pipe;
        if (pipe is null)
            throw new IOException("Discord app not running.");
        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[nonce] = tcs;
        try
        {
            string payload = JsonSerializer.Serialize(new { cmd, args, nonce });
            await WriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteFrameAsync(pipe, 1, payload).ConfigureAwait(false);
            }
            finally
            {
                WriteGate.Release();
            }
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
        }
        finally
        {
            Pending.TryRemove(nonce, out _);
        }
    }

    private static async Task WriteFrameAsync(NamedPipeClientStream pipe, int opcode, string payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        byte[] header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        await pipe.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        await pipe.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    private static void SetStatus(string value)
    {
        if (Status == value)
            return;
        Status = value;
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private sealed class DiscordRpcException : Exception
    {
        public DiscordRpcException(string message) : base(message) { }
    }
}
