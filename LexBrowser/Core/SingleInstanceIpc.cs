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
/// ya abierto, la nueva instancia envía la URL a la instancia existente y se cierra en ~5ms,
/// evitando abrir procesos duplicados y ahorrando 100% de memoria RAM innecesaria.
/// </summary>
public static class SingleInstanceIpc
{
    private static readonly string MutexName = "Global\\LexJunior_SingleInstance_Mutex_" + Environment.UserName;
    private static readonly string PipeName = "LexJunior_NavPipe_" + Environment.UserName;
    private static Mutex? _instanceMutex;
    private static CancellationTokenSource? _cts;

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
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var ms = new MemoryStream();
                    byte[] buffer = new byte[1024];
                    int bytesRead;
                    while ((bytesRead = await server.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                        if (bytesRead < buffer.Length) break;
                    }

                    string received = Encoding.UTF8.GetString(ms.ToArray()).Trim();
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

