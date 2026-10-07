using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamsObsRecorder;

/// <summary>Minimal obs-websocket v5 client (built into OBS 28+).</summary>
public sealed class ObsWebSocket : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private ObsWebSocket() { }

    public static ObsWebSocket Connect(string host, int port, string password)
    {
        var client = new ObsWebSocket();
        try
        {
            client.Handshake(new Uri($"ws://{host}:{port}"), password);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Handshake(Uri uri, string password)
    {
        using (var cts = new CancellationTokenSource(Timeout))
            _ws.ConnectAsync(uri, cts.Token).GetAwaiter().GetResult();

        var hello = Receive(); // op 0
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (hello["d"]?["authentication"] is JsonObject auth)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("OBS websocket needs a password; set it in Settings (obs.password)");
            identify["authentication"] = AuthString(password,
                auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        Send(new JsonObject { ["op"] = 1, ["d"] = identify });

        var identified = Receive();
        if (identified["op"]?.GetValue<int>() != 2)
            throw new InvalidOperationException("OBS did not accept the connection");
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge))</summary>
    public static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public JsonObject Request(string requestType, JsonObject? data = null)
    {
        var id = Guid.NewGuid().ToString();
        var d = new JsonObject { ["requestType"] = requestType, ["requestId"] = id };
        if (data is not null) d["requestData"] = data;
        Send(new JsonObject { ["op"] = 6, ["d"] = d });

        while (true)
        {
            var msg = Receive();
            if (msg["op"]?.GetValue<int>() != 7 || msg["d"]?["requestId"]?.GetValue<string>() != id) continue;
            var status = msg["d"]!["requestStatus"]!;
            if (status["result"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(
                    $"OBS {requestType} failed: {status["code"]} {status["comment"]}");
            return msg["d"]!["responseData"] as JsonObject ?? new JsonObject();
        }
    }

    private void Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        using var cts = new CancellationTokenSource(Timeout);
        _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();
    }

    private JsonObject Receive()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = _ws.ReceiveAsync(chunk, cts.Token).GetAwaiter().GetResult();
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"OBS closed the connection ({(int?)result.CloseStatus} {result.CloseStatusDescription})");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(buffer.ToArray()) as JsonObject
               ?? throw new InvalidOperationException("Unexpected message from OBS");
    }

    public void Dispose()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).GetAwaiter().GetResult();
            }
        }
        catch { /* closing is best effort */ }
        _ws.Dispose();
    }
}

/// <summary>Starts/stops OBS recording, launching OBS first when it isn't running.</summary>
public sealed class ObsRecorder : IRecorder
{
    private volatile ObsConfig _cfg;

    public ObsRecorder(ObsConfig cfg) => _cfg = cfg;

    public ObsConfig Config
    {
        get => _cfg;
        set => _cfg = value;
    }

    private ObsWebSocket Open()
    {
        var cfg = _cfg;
        return ObsWebSocket.Connect(cfg.Host, cfg.Port, cfg.Password);
    }

    public ObsWebSocket Connect()
    {
        var cfg = _cfg;
        try
        {
            return Open();
        }
        catch (Exception ex) when (cfg.LaunchIfNotRunning && ex is not InvalidOperationException)
        {
            Log.Info($"OBS not reachable ({ex.Message}); starting it");
        }

        Launch();
        var deadline = DateTime.UtcNow.AddSeconds(cfg.StartupWaitSeconds);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            try { return Open(); }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException(
            $"OBS started but its websocket did not answer ({last?.Message}). " +
            "Enable it in OBS: Tools > WebSocket Server Settings.");
    }

    private void Launch()
    {
        var exe = string.IsNullOrEmpty(_cfg.Executable) ? DefaultExecutable() : _cfg.Executable;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("OBS executable not found; set the OBS path in Settings (obs.executable)");
        Log.Info($"Launching OBS: {exe}");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            // OBS on Windows must start from its own bin folder to find its data files.
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        psi.ArgumentList.Add("--minimize-to-tray");
        psi.ArgumentList.Add("--disable-shutdown-check");
        Process.Start(psi);
    }

    private static string DefaultExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var baseDir = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(baseDir)) continue;
                var exe = Path.Combine(baseDir, "obs-studio", "bin", "64bit", "obs64.exe");
                if (File.Exists(exe)) return exe;
            }
            return "";
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(':').Select(d => Path.Combine(d, "obs")).FirstOrDefault(File.Exists) ?? "";
    }

    public bool StartRecording()
    {
        using var obs = Connect();
        if (IsRecording(obs))
        {
            Log.Info("OBS is already recording");
            return false;
        }
        obs.Request("StartRecord");
        Log.Info("OBS recording started");
        return true;
    }

    /// <summary>Stops recording. Returns the saved file's path, or null if OBS wasn't recording.</summary>
    public string? StopRecording()
    {
        using var obs = Open();
        if (!IsRecording(obs)) return null;
        var path = obs.Request("StopRecord")["outputPath"]?.GetValue<string>();
        Log.Info($"OBS recording stopped{(path is null ? "" : $": {path}")}");
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static bool IsRecording(ObsWebSocket obs) =>
        obs.Request("GetRecordStatus")["outputActive"]?.GetValue<bool>() == true;
}
