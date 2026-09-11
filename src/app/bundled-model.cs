using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Local files only. The distribution contains the real bytes, never LFS pointers.
public static class BundledModel
{
    private sealed class Entry
    {
        public string Hash;
        public long Length;
        public string Name;
    }

    public static void Ensure(string path, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Model path is required.", "path");
        if (!IsHash(expectedHash)) throw new ArgumentException("A SHA-256 hash is required.", "expectedHash");
        path = Path.GetFullPath(path);
        if (IsValid(path, expectedHash)) return;
        string key;
        using (var hash = SHA256.Create())
            key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
        using (var mutex = new Mutex(false, "Local\\pub-transcribe-bundle-" + key.Substring(0, 24)))
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromMinutes(10)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new TimeoutException("Another window is still preparing this bundled model: " + path);
                if (IsValid(path, expectedHash)) return;
                Reconstruct(path, expectedHash);
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
    }

    private static void Reconstruct(string path, string expectedHash)
    {
        string manifest = path + ".manifest";
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Bundled model is incomplete. Restore the complete application folder; no online model retrieval is performed: " + manifest, manifest);
        string[] lines = File.ReadAllLines(manifest, Encoding.ASCII);
        if (lines.Length < 2 || lines.Length > 1001) throw new InvalidDataException("Invalid bundled model manifest: " + manifest);
        Entry whole = Parse(lines[0]);
        string name = Path.GetFileName(path);
        if (whole.Name != name || !whole.Hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundled model manifest does not match the expected model: " + manifest);
        var parts = new List<Entry>();
        long length = 0;
        for (int index = 1; index < lines.Length; index++)
        {
            Entry part = Parse(lines[index]);
            // Requiring this exact name/order also rejects rooted paths and traversal.
            if (part.Name != name + ".part" + index.ToString("D2", CultureInfo.InvariantCulture) || part.Length > 75L * 1024 * 1024)
                throw new InvalidDataException("Invalid bundled model part name, order or length: " + manifest);
            checked { length += part.Length; }
            parts.Add(part);
        }
        if (length != whole.Length) throw new InvalidDataException("Bundled model manifest has inconsistent lengths: " + manifest);
        string directory = Path.GetDirectoryName(path);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (Entry part in parts)
                {
                    string partPath = Path.Combine(directory, part.Name);
                    if (!File.Exists(partPath))
                        throw new FileNotFoundException("Bundled model part is missing. Restore the complete application folder: " + partPath, partPath);
                    // Keep the verified part open without write-sharing until it is copied.
                    using (var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length != part.Length || !HashStream(input).Equals(part.Hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Bundled model part checksum mismatch: " + partPath);
                        input.Position = 0;
                        input.CopyTo(output, 1024 * 1024);
                    }
                }
                output.Flush(true);
            }
            if (new FileInfo(temp).Length != whole.Length || !IsValid(temp, expectedHash))
                throw new InvalidDataException("Reconstructed model checksum mismatch: " + path);
            // Do not delete a previous file until the complete replacement is verified.
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Cannot prepare the bundled model here. Move the complete application folder to a writable location: " + directory, ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static Entry Parse(string line)
    {
        string[] fields = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        long length;
        if (fields.Length != 3 || !IsHash(fields[0]) ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length <= 0)
            throw new InvalidDataException("Invalid bundled model manifest entry.");
        return new Entry { Hash = fields[0], Length = length, Name = fields[2] };
    }

    private static bool IsHash(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    private static bool IsValid(string path, string expectedHash)
    {
        if (!File.Exists(path)) return false;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return HashStream(input).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashStream(Stream input)
    {
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
    }
}
