using System.Buffers.Binary;
using System.Text;

namespace Lockbox.Core.Web;

/// <summary>
/// The browsers' native-messaging framing: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// Lockbox uses the same framing on its local pipe, so the helper can pass messages straight through.
/// </summary>
public static class NativeMessage
{
    /// <summary>Browsers refuse messages to an extension over 1 MB, so nothing bigger is ever sent or accepted.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Reads one message, or null when the other side has closed the stream.</summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxBytes) throw new InvalidDataException($"Message length {length} is out of range.");
        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, ct)) throw new EndOfStreamException("Message ended early.");
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxBytes) throw new InvalidDataException("Message is too large.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("Message ended early.");
            }
            read += n;
        }
        return true;
    }
}
