using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Lockbox.Core.Web;

/// <summary>
/// The browser helper's whole job: pass messages between the browser (stdin/stdout) and Lockbox (a local pipe).
/// It holds no passwords and makes no decisions; Lockbox checks everything. It stamps each request with the
/// caller the browser reported, so an extension can't claim to be another one, and it exits when Lockbox closes
/// (so an update can replace it).
/// </summary>
public static class NativeHostRelay
{
    public static async Task RunAsync(Stream fromBrowser, Stream toBrowser, string caller,
        Func<bool> launchLockbox, Action? beforeForward = null, CancellationToken ct = default)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        async Task Send(JsonNode msg)
        {
            await writeLock.WaitAsync(ct);
            try { await NativeMessage.WriteAsync(toBrowser, msg.ToJsonString(), ct); }
            finally { writeLock.Release(); }
        }

        NamedPipeClientStream? pipe = null;
        var lockboxClosed = new TaskCompletionSource();

        async Task PumpFromLockbox(NamedPipeClientStream p)
        {
            try
            {
                while (await NativeMessage.ReadAsync(p, ct) is { } reply)
                    if (JsonNode.Parse(reply) is JsonObject o) await Send(o);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Text.Json.JsonException or InvalidDataException) { }
            lockboxClosed.TrySetResult();
        }

        async Task ReadFromBrowser()
        {
            while (await NativeMessage.ReadAsync(fromBrowser, ct) is { } text)
            {
                if (JsonNode.Parse(text) is not JsonObject req) continue;
                JsonNode? id = req["id"]?.DeepClone();
                string type = req["type"]?.GetValue<string>() ?? "";

                if (type == "launch")
                {
                    bool started = launchLockbox();
                    await Send(started ? Reply.Ok(id) : Reply.Error(id, "launch-failed", "Couldn't start Lockbox."));
                    continue;
                }

                if (pipe is not { IsConnected: true })
                {
                    pipe?.Dispose();
                    pipe = await TryConnectAsync(ct);
                    if (pipe != null) _ = PumpFromLockbox(pipe);
                }
                if (pipe == null)
                {
                    await Send(Reply.Error(id, "not-running", "Lockbox isn't open."));
                    continue;
                }

                beforeForward?.Invoke();
                req["caller"] = caller;   // set here, from what the browser told us, never from the message
                try { await NativeMessage.WriteAsync(pipe, req.ToJsonString(), ct); }
                catch (IOException)
                {
                    await Send(Reply.Error(id, "not-running", "Lockbox closed."));
                    pipe.Dispose();
                    pipe = null;
                }
            }
        }

        try
        {
            var reading = ReadFromBrowser();
            await Task.WhenAny(reading, lockboxClosed.Task);   // browser hung up, or Lockbox closed
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException) { }
        finally { pipe?.Dispose(); }
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken ct)
    {
        var p = new NamedPipeClientStream(".", BrowserBridge.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await p.ConnectAsync(500, ct);
            return p;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            p.Dispose();
            return null;
        }
    }
}

/// <summary>Reply shapes shared by the helper and Lockbox: { id, ok: true, … } or { id, ok: false, error, message }.</summary>
public static class Reply
{
    public static JsonObject Ok(JsonNode? id, JsonObject? body = null)
    {
        var o = new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = true };
        if (body != null)
            foreach (var (k, v) in body) o[k] = v?.DeepClone();
        return o;
    }

    public static JsonObject Error(JsonNode? id, string code, string message) =>
        new() { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = code, ["message"] = message };
}
