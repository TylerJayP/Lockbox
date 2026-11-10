using System.IO.Pipes;
using System.Text.Json.Nodes;
using Lockbox.Core.Web;

namespace Lockbox.App.Services;

/// <summary>
/// Listens on the local pipe for the browser helper. Only this Windows user can connect (PipeOptions.CurrentUserOnly).
/// Each request is handed to <see cref="_handle"/> and answered with the same "id"; requests on one connection
/// can be answered out of order (a fill waiting for the master password doesn't hold up a status check).
/// </summary>
public sealed class BrowserBridgeServer(Func<JsonObject, Task<JsonObject>> handle) : IDisposable
{
    private readonly Func<JsonObject, Task<JsonObject>> _handle = handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<NamedPipeServerStream> _open = [];

    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(BrowserBridge.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);   // name briefly unavailable; try again
                continue;
            }

            try { await server.WaitForConnectionAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { server.Dispose(); return; }
            catch (IOException) { server.Dispose(); continue; }

            lock (_open) _open.Add(server);
            _ = ServeAsync(server, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        try
        {
            while (await NativeMessage.ReadAsync(pipe, ct).ConfigureAwait(false) is { } text)
            {
                if (JsonNode.Parse(text) is not JsonObject req) continue;
                _ = Task.Run(async () =>
                {
                    JsonObject reply;
                    try { reply = await _handle(req).ConfigureAwait(false); }
                    catch (Exception ex) { reply = Reply.Error(req["id"], "error", ex.Message); }
                    reply["id"] = req["id"]?.DeepClone();
                    await writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try { await NativeMessage.WriteAsync(pipe, reply.ToJsonString(), ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                    finally { writeLock.Release(); }
                }, ct);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException
                                      or InvalidDataException or System.Text.Json.JsonException) { }
        finally
        {
            lock (_open) _open.Remove(pipe);
            pipe.Dispose();
        }
    }

    /// <summary>Stops listening and closes every connection (the helpers then exit).</summary>
    public void Dispose()
    {
        _cts.Cancel();
        lock (_open)
        {
            foreach (var p in _open) p.Dispose();
            _open.Clear();
        }
    }
}
