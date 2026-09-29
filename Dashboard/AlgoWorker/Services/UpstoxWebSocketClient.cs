using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AlgoWorker.Models;
using AlgoWorker.Services.Decoding;

namespace AlgoWorker.Services;

/// <summary>
/// Maintains a live WebSocket connection to Upstox's Market Data Feed V3,
/// subscribes to the given instrument keys, and raises <see cref="TickReceived"/>
/// for every decoded price update. Auto-reconnects with exponential backoff.
/// </summary>
public sealed class UpstoxWebSocketClient : IAsyncDisposable
{
    private readonly UpstoxRestClient _restClient;
    private readonly IMarketDataDecoder _decoder;
    private readonly ILogger<UpstoxWebSocketClient> _logger;

    private ClientWebSocket? _socket;
    private readonly HashSet<string> _subscribedKeys = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<MarketTick>? TickReceived;
    public event Action? Connected;
    public event Action<Exception?>? Disconnected;

    public UpstoxWebSocketClient(
        UpstoxRestClient restClient,
        IMarketDataDecoder decoder,
        ILogger<UpstoxWebSocketClient> logger)
    {
        _restClient = restClient;
        _decoder = decoder;
        _logger = logger;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Connects (or reconnects) and subscribes to the given instrument keys. Runs until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(IReadOnlyCollection<string> instrumentKeys, CancellationToken ct)
    {
        _subscribedKeys.UnionWith(instrumentKeys);
        var backoff = TimeSpan.FromSeconds(5);
        var maxBackoff = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndSubscribeAsync(_subscribedKeys.ToArray(), ct);
                backoff = TimeSpan.FromSeconds(5); // reset after a clean connect
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSocket connection lost/failed. Reconnecting in {Backoff}s...", backoff.TotalSeconds);
                Disconnected?.Invoke(ex);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff.TotalSeconds));
            }
        }
    }

    /// <summary>Adds an instrument (e.g. the option contract for a freshly-opened virtual position) to the live subscription.</summary>
    public async Task SubscribeAsync(string instrumentKey, CancellationToken ct)
    {
        _subscribedKeys.Add(instrumentKey);
        if (IsConnected)
            await SendSubMessageAsync("sub", new[] { instrumentKey }, ct);
    }

    /// <summary>Removes an instrument from the live subscription (e.g. once a virtual position closes).</summary>
    public async Task UnsubscribeAsync(string instrumentKey, CancellationToken ct)
    {
        _subscribedKeys.Remove(instrumentKey);
        if (IsConnected)
            await SendSubMessageAsync("unsub", new[] { instrumentKey }, ct);
    }

    private async Task ConnectAndSubscribeAsync(IReadOnlyCollection<string> instrumentKeys, CancellationToken ct)
    {
        var wsUrl = await _restClient.GetAuthorizedWebSocketUrlAsync(ct);

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(wsUrl), ct);
        _logger.LogInformation("Upstox WebSocket connected.");
        Connected?.Invoke();

        if (instrumentKeys.Count > 0)
            await SendSubMessageAsync("sub", instrumentKeys, ct);
    }

    private async Task SendSubMessageAsync(string method, IReadOnlyCollection<string> instrumentKeys, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open })
            return;

        var payload = JsonSerializer.Serialize(new
        {
            guid = Guid.NewGuid().ToString("N"),
            method,
            data = new { mode = "full", instrumentKeys }
        });

        var bytes = Encoding.UTF8.GetBytes(payload);

        await _sendLock.WaitAsync(ct);
        try
        {
            // >>> FIXED: Upstox's docs explicitly say the subscribe/unsubscribe
            // request "should be sent in binary format, not as a text message"
            // -- this was wrongly WebSocketMessageType.Text before.
            await _socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        _logger.LogInformation("{Method} -> {Count} instrument(s): {Keys}", method, instrumentKeys.Count, string.Join(", ", instrumentKeys));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (_socket!.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("Upstox closed the WebSocket: {Status} {Description}", result.CloseStatus, result.CloseStatusDescription);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var frame = ms.ToArray();
            foreach (var tick in _decoder.Decode(frame))
                TickReceived?.Invoke(tick);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None);
            }
            catch
            {
                // best-effort close, don't crash shutdown over it
            }
        }
        _socket?.Dispose();
    }
}