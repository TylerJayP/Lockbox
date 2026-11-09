namespace Lockbox.App.Services;

/// <summary>
/// Overwrites a file with zeros, then deletes it (skipping the Recycle Bin). On SSDs the drive may keep
/// old copies of the data internally, so this reduces exposure rather than guaranteeing erasure.
/// </summary>
public static class FileShredder
{
    public static async Task OverwriteAndDeleteAsync(string path)
    {
        var len = new FileInfo(path).Length;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var zeros = new byte[Math.Min(Math.Max(len, 1), 1 << 20)];
            for (long written = 0; written < len; written += zeros.Length)
                await fs.WriteAsync(zeros.AsMemory(0, (int)Math.Min(zeros.Length, len - written)));
            await fs.FlushAsync();
            fs.Flush(flushToDisk: true);
        }
        File.Delete(path);
    }
}
