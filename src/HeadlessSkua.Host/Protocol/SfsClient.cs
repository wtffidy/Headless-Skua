using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skua.Headless.UI;

namespace HeadlessSkua.Protocol;

/// <summary>
/// Low-level TCP client that frames SmartFoxServer 1.x null-byte-delimited protocol messages.
/// </summary>
public sealed class SfsClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoopTask;
    private int _disconnectState;
    private long _lastSendTime;
    private long _lastGarTime;

    /// <summary>
    /// Raised when a complete null-delimited message is received from the SFS server.
    /// </summary>
    public event Action<string>? MessageReceived;

    /// <summary>
    /// Raised when the TCP connection is closed or lost.
    /// </summary>
    public event Action? Disconnected;

    public bool IsConnected => _tcpClient is { Connected: true } && Volatile.Read(ref _disconnectState) == 0;

    /// <summary>
    /// Connects to the SmartFoxServer TCP endpoint.
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _disconnectState = 0;
        _tcpClient = new TcpClient { NoDelay = true };

        await _tcpClient.ConnectAsync(host, port, cancellationToken);
        _stream = _tcpClient.GetStream();
        _connectionCts = new CancellationTokenSource();

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));
    }

    /// <summary>
    /// Sends a null-byte terminated message to the SFS server.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null)
            throw new InvalidOperationException("Not connected to SmartFoxServer.");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            long now = Environment.TickCount64;
            long elapsed = now - _lastSendTime;
            if (elapsed < 150)
            {
                await Task.Delay((int)(150 - elapsed), cancellationToken);
            }

            if (message.Contains("%gar%"))
            {
                long garElapsed = Environment.TickCount64 - _lastGarTime;
                if (garElapsed < 400)
                {
                    await Task.Delay((int)(400 - garElapsed), cancellationToken);
                }
                _lastGarTime = Environment.TickCount64;
            }

            byte[] payload = Encoding.UTF8.GetBytes(message + "\0");
            await _stream.WriteAsync(payload, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            _lastSendTime = Environment.TickCount64;

            HeadlessConsole.Debug($"[SFS OUT] {message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Continuously reads from the TCP stream and splits messages on null bytes.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[16 * 1024];
        using MemoryStream messageAccumulator = new();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                int bytesRead = await _stream.ReadAsync(readBuffer.AsMemory(), cancellationToken);
                if (bytesRead == 0)
                    break;

                int segmentStart = 0;
                for (int i = 0; i < bytesRead; i++)
                {
                    if (readBuffer[i] == 0) // Null-byte delimiter found
                    {
                        int segmentLength = i - segmentStart;
                        if (segmentLength > 0)
                            messageAccumulator.Write(readBuffer, segmentStart, segmentLength);

                        string message = Encoding.UTF8.GetString(messageAccumulator.ToArray());
                        messageAccumulator.SetLength(0);

                        segmentStart = i + 1;

                        if (!string.IsNullOrEmpty(message))
                        {
                            try
                            {
                                MessageReceived?.Invoke(message);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[SfsClient] Message handler error: {ex.Message}");
                            }
                        }
                    }
                }

                // If trailing bytes remain that did not terminate with \0, keep them in accumulator
                if (segmentStart < bytesRead)
                {
                    messageAccumulator.Write(readBuffer, segmentStart, bytesRead - segmentStart);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SfsClient] Receive loop terminated: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    /// <summary>
    /// Closes the TCP connection.
    /// </summary>
    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectState, 1) != 0)
            return;

        try
        {
            _connectionCts?.Cancel();
            _stream?.Close();
            _tcpClient?.Close();
        }
        catch
        {
        }

        try
        {
            Disconnected?.Invoke();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch
            {
            }
        }
        _sendLock.Dispose();
        _connectionCts?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}
