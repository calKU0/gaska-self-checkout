using System.Buffers.Binary;
using System.Net.Sockets;
using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Devices.Polcard.Transport;

public interface IEcrConnection : IAsyncDisposable
{
    Task SendAsync(EcrMessage message, CancellationToken ct);

    /// <summary>Receives the next message; null when the terminal closed the connection.</summary>
    Task<EcrMessage?> ReceiveAsync(TimeSpan timeout, CancellationToken ct);
}

public interface IEcrConnectionFactory
{
    Task<IEcrConnection> ConnectAsync(CancellationToken ct);
}

/// <summary>
/// Ethernet connection: the terminal is the server, the ECR the client. Frame: [AML – 2 bytes, message length]|message|.
/// Every sent and received message is logged (Fiserv certification requirement – transaction logs).
/// </summary>
public sealed class EcrTcpConnection : IEcrConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ILogger _protocolLog;

    private EcrTcpConnection(TcpClient client, ILogger protocolLog)
    {
        _client = client;
        _stream = client.GetStream();
        _protocolLog = protocolLog;
    }

    public static async Task<EcrTcpConnection> ConnectAsync(string host, int port, TimeSpan timeout, ILogger protocolLog, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            protocolLog.LogWarning("CONNECT {Host}:{Port} FAILED: no answer within {Timeout}", host, port, timeout);
            throw new TimeoutException($"No connection to the terminal {host}:{port}.");
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ex is not OperationCanceledException)
                protocolLog.LogWarning("CONNECT {Host}:{Port} FAILED: {Error}", host, port, ex.Message);
            throw;
        }

        protocolLog.LogInformation("CONNECT {Host}:{Port}", host, port);
        return new EcrTcpConnection(client, protocolLog);
    }

    public async Task SendAsync(EcrMessage message, CancellationToken ct)
    {
        var wire = message.ToWire();
        var payload = EcrEncoding.Text.GetBytes(wire);
        var frame = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame, 2);
        _protocolLog.LogInformation("ECR -> POS {Message}", wire);
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }

    public async Task<EcrMessage?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var header = new byte[2];
            if (!await ReadExactly(header, cts.Token))
            {
                _protocolLog.LogInformation("POS closed connection");
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            var payload = new byte[length];
            if (!await ReadExactly(payload, cts.Token))
            {
                _protocolLog.LogWarning("POS closed connection in the middle of a frame");
                return null;
            }

            var wire = EcrEncoding.Text.GetString(payload);
            _protocolLog.LogInformation("POS -> ECR {Message}", wire);
            return EcrMessage.Parse(wire);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Terminal response timeout.");
        }
    }

    private async Task<bool> ReadExactly(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        _protocolLog.LogInformation("DISCONNECT");
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
