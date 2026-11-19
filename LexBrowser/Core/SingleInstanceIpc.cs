using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core;

/// <summary>
/// Gestiona la instancia única del navegador y la comunicación IPC mediante Named Pipes.
/// Cuando el usuario hace clic en un enlace externo (WhatsApp, Discord, etc.) con el navegador
/// ya abierto, la nueva instancia envía la URL a la instancia existente y se cierra
/// para evitar abrir procesos o ventanas duplicadas.
/// </summary>
public static class SingleInstanceIpc
{
    public const int MaxIpcPayloadBytes = 16 * 1024;
    private const int ClientReadTimeoutMs = 2500;
    private static readonly string MutexName = "Global\\LexJunior_SingleInstance_Mutex_" + Environment.UserName;
    private static readonly string PipeName = "LexJunior_NavPipe_" + Environment.UserName;
    private static Mutex? _instanceMutex;
    private static CancellationTokenSource? _cts;

    internal static async Task<string> ReadBoundedPayloadAsync(Stream stream, int maxBytes = MaxIpcPayloadBytes, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[1024];
        while (ms.Length < maxBytes)
        {
            int remaining = maxBytes - (int)ms.Length;
            int toRead = Math.Min(buffer.Length, remaining);
            int bytesRead = await stream.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
                break;

            ms.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(ms.ToArray()).Trim();
    }

    public static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    public static async Task<bool> SendUrlToExistingInstanceAsync(string? url, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(cts.Token);

            string payload = url ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await client.WriteAsync(bytes, 0, bytes.Length, cts.Token);
            await client.FlushAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartIpcServer(Action<string> onUrlReceived)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(token);

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(ClientReadTimeoutMs);

                    string received = await ReadBoundedPayloadAsync(server, MaxIpcPayloadBytes, readCts.Token);
                    try
                    {
                        onUrlReceived(received);
                    }
                    catch { }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(200, token); } catch { }
                }
            }
        }, token);
    }

    public static void Release()
    {
        try { _cts?.Cancel(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        _instanceMutex = null;
    }
}

