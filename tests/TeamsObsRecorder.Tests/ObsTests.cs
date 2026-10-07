using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using TeamsObsRecorder;
using Xunit;

namespace TeamsObsRecorder.Tests;

/// <summary>A fake OBS that speaks the obs-websocket v5 handshake and recording requests.</summary>
public sealed class FakeObsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _password;
    public int Port { get; }
    public bool Recording { get; set; }
    public List<string> Requests { get; } = new();

    public FakeObsServer(string password)
    {
        _password = password;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        const string salt = "c2FsdA==", challenge = "Y2hhbGxlbmdl";
        await Send(ws, new JsonObject
        {
            ["op"] = 0,
            ["d"] = new JsonObject
            {
                ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1,
                ["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt },
            },
        });

        var identify = await Receive(ws);
        if (identify?["d"]?["authentication"]?.GetValue<string>() != ObsWebSocket.AuthString(_password, salt, challenge))
        {
            await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", default);
            return;
        }
        await Send(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });

        while (await Receive(ws) is { } msg)
        {
            var type = msg["d"]!["requestType"]!.GetValue<string>();
            lock (Requests) Requests.Add(type);
            var data = new JsonObject();
            switch (type)
            {
                case "GetRecordStatus": data["outputActive"] = Recording; break;
                case "StartRecord": Recording = true; break;
                case "StopRecord": Recording = false; break;
                case "GetVersion": data["obsVersion"] = "30.2.0"; break;
            }
            // Events are interleaved with responses in real OBS; the client must skip them.
            await Send(ws, new JsonObject { ["op"] = 5, ["d"] = new JsonObject { ["eventType"] = "Noise" } });
            await Send(ws, new JsonObject
            {
                ["op"] = 7,
                ["d"] = new JsonObject
                {
                    ["requestType"] = type,
                    ["requestId"] = msg["d"]!["requestId"]!.GetValue<string>(),
                    ["requestStatus"] = new JsonObject { ["result"] = true, ["code"] = 100 },
                    ["responseData"] = data,
                },
            });
        }
    }

    private static Task Send(WebSocket ws, JsonObject msg) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, default);

    private static async Task<JsonObject?> Receive(WebSocket ws)
    {
        var buffer = new byte[65536];
        try
        {
            var result = await ws.ReceiveAsync(buffer, default);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)) as JsonObject;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    public void Dispose() => _listener.Close();
}

public class ObsTests
{
    private static ObsConfig Config(int port, string password) => new()
    {
        Host = "127.0.0.1", Port = port, Password = password, LaunchIfNotRunning = false,
    };

    [Fact]
    public void StartsAndStopsRecording()
    {
        using var server = new FakeObsServer("hunter2");
        var recorder = new ObsRecorder(Config(server.Port, "hunter2"));

        Assert.True(recorder.StartRecording());
        Assert.True(server.Recording);
        Assert.False(recorder.StartRecording()); // already recording

        recorder.StopRecording();
        Assert.False(server.Recording);
        Assert.Equal(new[] { "GetRecordStatus", "StartRecord", "GetRecordStatus", "GetRecordStatus", "StopRecord" },
            server.Requests);
    }

    [Fact]
    public void WrongPasswordIsReported()
    {
        using var server = new FakeObsServer("hunter2");
        var recorder = new ObsRecorder(Config(server.Port, "wrong"));

        var ex = Assert.Throws<InvalidOperationException>(() => recorder.StartRecording());
        Assert.Contains("4009", ex.Message);
    }

    [Fact]
    public void GetVersion()
    {
        using var server = new FakeObsServer("pw");
        using var obs = new ObsRecorder(Config(server.Port, "pw")).Connect();
        Assert.Equal("30.2.0", obs.Request("GetVersion")["obsVersion"]!.GetValue<string>());
    }
}
