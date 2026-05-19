using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ObsClipSidecar;

namespace ObsClipPanel;

public sealed record ObsHello(string ObsStudioVersion, string ObsWebSocketVersion, int RpcVersion, string? Challenge, string? Salt);
public sealed record ObsEventEnvelope(string EventType, Dictionary<string, object?> EventData);
public sealed record ObsRequestResult(bool Success, string Comment, Dictionary<string, object?> ResponseData);

public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private ClientWebSocket? socket;
    private Task? receiveLoop;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource<ObsRequestResult>> pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? lifecycleCts;

    public bool IsConnected => socket?.State == WebSocketState.Open;
    public bool IsIdentified { get; private set; }
    public ObsHello? Hello { get; private set; }

    public event Func<ObsEventEnvelope, Task>? EventReceived;

    public async Task ConnectAsync(Uri uri, string password, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("obswebsocket.json");
        await socket.ConnectAsync(uri, cancellationToken);

        var helloMessage = await ReceiveRawAsync(socket, cancellationToken);
        Hello = ParseHello(helloMessage);
        var identifyPayload = BuildIdentifyPayload(Hello, password);
        await SendAsync(new
        {
            op = 1,
            d = identifyPayload
        }, cancellationToken);

        var identified = await ReceiveRawAsync(socket, cancellationToken);
        EnsureIdentified(identified);
        IsIdentified = true;
        receiveLoop = Task.Run(() => ReceiveLoopAsync(lifecycleCts.Token));
    }

    public async Task<ObsRequestResult> RequestAsync(string requestType, object? requestData, CancellationToken cancellationToken)
    {
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("OBS websocket is not connected.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ObsRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending[requestId] = tcs;
        }

        await SendAsync(new
        {
            op = 6,
            d = new
            {
                requestType,
                requestId,
                requestData
            }
        }, cancellationToken);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(requestId);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            lifecycleCts?.Cancel();
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
            socket = null;
            IsIdentified = false;
            Hello = null;
            FailPending("OBS websocket disconnected.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        sendLock.Dispose();
        lifecycleCts?.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open } liveSocket)
            {
                var json = await ReceiveRawAsync(liveSocket, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var op = doc.RootElement.GetProperty("op").GetInt32();
                var data = doc.RootElement.GetProperty("d");
                switch (op)
                {
                    case 5:
                        var eventType = data.GetProperty("eventType").GetString() ?? "unknown";
                        var eventData = data.TryGetProperty("eventData", out var eventDataElement)
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(eventDataElement.GetRawText(), JsonDefaults.Options) ?? new Dictionary<string, object?>()
                            : new Dictionary<string, object?>();
                        if (EventReceived is not null)
                        {
                            await EventReceived(new ObsEventEnvelope(eventType, eventData));
                        }
                        break;
                    case 7:
                        HandleRequestResponse(data);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailPending(ex.Message);
        }
        finally
        {
            IsIdentified = false;
        }
    }

    private void HandleRequestResponse(JsonElement data)
    {
        var requestId = data.GetProperty("requestId").GetString() ?? "";
        var requestStatus = data.GetProperty("requestStatus");
        var success = requestStatus.GetProperty("result").GetBoolean();
        var comment = requestStatus.TryGetProperty("comment", out var commentElement) ? commentElement.GetString() ?? "" : "";
        var responseData = data.TryGetProperty("responseData", out var responseElement)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(responseElement.GetRawText(), JsonDefaults.Options) ?? new Dictionary<string, object?>()
            : new Dictionary<string, object?>();

        TaskCompletionSource<ObsRequestResult>? tcs = null;
        lock (gate)
        {
            pending.TryGetValue(requestId, out tcs);
        }
        tcs?.TrySetResult(new ObsRequestResult(success, comment, responseData));
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (socket is null)
        {
            throw new InvalidOperationException("OBS websocket is not connected.");
        }

        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<string> ReceiveRawAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("OBS websocket closed the connection.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    private static ObsHello ParseHello(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("d");
        string? challenge = null;
        string? salt = null;
        if (data.TryGetProperty("authentication", out var auth))
        {
            challenge = auth.GetProperty("challenge").GetString();
            salt = auth.GetProperty("salt").GetString();
        }

        return new ObsHello(
            data.TryGetProperty("obsStudioVersion", out var obsStudioVersion) ? obsStudioVersion.GetString() ?? "" : "",
            data.TryGetProperty("obsWebSocketVersion", out var obsWebSocketVersion) ? obsWebSocketVersion.GetString() ?? "" : "",
            data.TryGetProperty("rpcVersion", out var rpcVersion) ? rpcVersion.GetInt32() : 1,
            challenge,
            salt);
    }

    private static object BuildIdentifyPayload(ObsHello hello, string password)
    {
        var auth = string.IsNullOrWhiteSpace(hello.Challenge) || string.IsNullOrWhiteSpace(hello.Salt)
            ? null
            : BuildAuthenticationString(password, hello.Salt, hello.Challenge);

        return new
        {
            rpcVersion = Math.Max(1, hello.RpcVersion),
            authentication = auth,
            eventSubscriptions = 1 << 6
        };
    }

    private static string BuildAuthenticationString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static void EnsureIdentified(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("op").GetInt32() != 2)
        {
            throw new InvalidOperationException("OBS websocket did not acknowledge Identify.");
        }
    }

    private void FailPending(string message)
    {
        lock (gate)
        {
            foreach (var item in pending.Values)
            {
                item.TrySetException(new InvalidOperationException(message));
            }

            pending.Clear();
        }
    }
}
