using System.Security.Cryptography;

namespace AdoLens;

/// <summary>
/// Files from Azure DevOps saved on disk, streamed (any size, nothing held in memory, nothing sent to the model but the
/// path). Saved files are never opened or run.
/// </summary>
public static class Downloads
{
    /// <summary>The folder from the settings, else Downloads\ADO Lens.</summary>
    public static string BaseDir(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.DownloadDir) ? Environment.ExpandEnvironmentVariables(settings.DownloadDir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ADO Lens");

    /// <summary>A file name that stays a file name: no folders, no characters Windows refuses, not empty, not too long.</summary>
    public static string SafeName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('/', '\\'));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().Trim('.');
        if (n.Length == 0) n = "file";
        if (n.Length > 150) n = n[..(150 - Path.GetExtension(n).Length)] + Path.GetExtension(n);
        // Reserved device names (CON, NUL, COM1…) cannot be files.
        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(n), @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            n = "_" + n;
        return n;
    }

    /// <summary>A path in the folder that does not exist yet: "name.ext", then "name (2).ext"…</summary>
    public static string FreePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    /// <summary>
    /// Streams <paramref name="url"/> into <paramref name="folder"/>\&lt;sub&gt;\&lt;name&gt;; the SHA-256 is computed on the way.
    /// A partly written file is removed when the download fails.
    /// </summary>
    public static async Task<string> SaveAsync(AdoClient client, string url, string folder, string sub, string name, CancellationToken ct)
    {
        var root = Path.GetFullPath(folder);
        var dir = Path.GetFullPath(Path.Combine(root, SafeName(sub)));
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("the target folder is outside the download folder");
        Directory.CreateDirectory(dir);
        var path = FreePath(dir, SafeName(name));
        long size;
        string sha;
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            await using (var hashing = new HashingStream(file))
            {
                size = await client.DownloadToAsync(url, hashing, ct);
                sha = hashing.Hash();
            }
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
        return $"saved: {path} ({Documents.Size(new FileInfo(path).Length)}, sha256 {sha})";
    }

    /// <summary>Writes through to a file and hashes what passes.</summary>
    sealed class HashingStream(Stream inner) : Stream
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written;

        public string Hash() => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
            written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, ct);
            written += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
