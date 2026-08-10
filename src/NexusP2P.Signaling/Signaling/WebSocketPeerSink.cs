using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NexusP2P.Signaling.Rooms;

namespace NexusP2P.Signaling.Signaling;

/// <summary>
/// 把 <see cref="IPeerSink"/> 落到一条 WebSocket 上。
///
/// <para>发送必须串行化：<see cref="WebSocket.SendAsync"/> 不允许并发调用，
/// 而这里天然会有并发 —— 对端转发来的信令和服务器自己的通知
/// 来自不同的执行流。</para>
/// </summary>
public sealed class WebSocketPeerSink(WebSocket socket) : IPeerSink, IDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _disposed;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        if (_disposed || socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 对端已经走了。信令连接断开是常态，不值得往上抛。
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task SendAsync(ServerMessage message, CancellationToken cancellationToken) =>
        SendAsync(JsonSerializer.Serialize(message, JsonOptions), cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendGate.Dispose();
    }
}
