using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public static class BundleRegressionTests
{
    private static int passed;
    private static string root;
    private static string Hash(byte[] data)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "Invalid bundle was accepted.");
    }
    private static string Fixture(out string hash)
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.onnx");
        byte[] first = Encoding.ASCII.GetBytes("first part ");
        byte[] second = Encoding.ASCII.GetBytes("second part");
        byte[] whole = Encoding.ASCII.GetBytes("first part second part");
        hash = Hash(whole);
        File.WriteAllBytes(path + ".part01", first);
        File.WriteAllBytes(path + ".part02", second);
        File.WriteAllLines(path + ".manifest", new string[] {
            hash + " " + whole.Length + " fixture.onnx",
            Hash(first) + " " + first.Length + " fixture.onnx.part01",
            Hash(second) + " " + second.Length + " fixture.onnx.part02"
        }, Encoding.ASCII);
        return path;
    }
    private static void CleanTemp(string path)
    {
        Assert(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp.*").Length == 0, "Temporary output leaked.");
    }
    private static void Pass(string label) { passed++; Console.WriteLine("PASS " + label); }
    public static void Run(string directory)
    {
        root = directory; passed = 0; Directory.CreateDirectory(root);
        string hash;
        string path = Fixture(out hash);
        BundledModel.Ensure(path, hash);
        Assert(Hash(File.ReadAllBytes(path)) == hash, "Wrong reconstructed bytes.");
        CleanTemp(path); Pass("Offline reconstruction and complete SHA-256");

        DateTime timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        File.Delete(path + ".part01");
        BundledModel.Ensure(path, hash);
        Assert(File.GetLastWriteTimeUtc(path) == timestamp, "Valid model was rewritten.");
        Pass("Existing verified model reused without reassembly");

        path = Fixture(out hash); File.Delete(path + ".part02");
        Reject(delegate { BundledModel.Ensure(path, hash); });
        Assert(!File.Exists(path), "Incomplete model was published."); CleanTemp(path);
        Pass("Missing part rejected and temporary output removed");

        path = Fixture(out hash);
        byte[] damaged = File.ReadAllBytes(path + ".part01"); damaged[0] ^= 1;
        File.WriteAllBytes(path + ".part01", damaged);
        Reject(delegate { BundledModel.Ensure(path, hash); });
        Assert(!File.Exists(path), "Corrupt model was published."); CleanTemp(path);
        using (File.Open(path + ".part01", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Pass("Same-length corruption rejected and part handle released");

        path = Fixture(out hash); File.WriteAllText(path, "previous incomplete file");
        BundledModel.Ensure(path, hash);
        Assert(Hash(File.ReadAllBytes(path)) == hash, "Local repair failed."); CleanTemp(path);
        Pass("Corrupt assembled model repaired atomically from local parts");

        path = Fixture(out hash); File.WriteAllText(path, "keep this file");
        File.Delete(path + ".part02");
        Reject(delegate { BundledModel.Ensure(path, hash); });
        Assert(File.ReadAllText(path) == "keep this file", "Previous file was lost on failure."); CleanTemp(path);
        Pass("Failed repair preserves existing file");

        path = Fixture(out hash);
        string[] lines = File.ReadAllLines(path + ".manifest");
        string swap = lines[1]; lines[1] = lines[2]; lines[2] = swap;
        File.WriteAllLines(path + ".manifest", lines);
        Reject(delegate { BundledModel.Ensure(path, hash); }); CleanTemp(path);
        Pass("Out-of-order parts rejected");

        path = Fixture(out hash); lines = File.ReadAllLines(path + ".manifest");
        lines[1] = lines[1].Replace("fixture.onnx.part01", "../outside.part01");
        File.WriteAllLines(path + ".manifest", lines);
        Reject(delegate { BundledModel.Ensure(path, hash); }); CleanTemp(path);
        Pass("Manifest path traversal rejected");

        path = Fixture(out hash); lines = File.ReadAllLines(path + ".manifest");
        lines[0] = hash + " 999 fixture.onnx"; File.WriteAllLines(path + ".manifest", lines);
        Reject(delegate { BundledModel.Ensure(path, hash); }); CleanTemp(path);
        Pass("Inconsistent manifest lengths rejected");

        path = Fixture(out hash); lines = File.ReadAllLines(path + ".manifest");
        string otherHash = Hash(Encoding.ASCII.GetBytes("different model"));
        lines[0] = lines[0].Replace(hash, otherHash); File.WriteAllLines(path + ".manifest", lines);
        Reject(delegate { BundledModel.Ensure(path, hash); });
        Pass("Manifest cannot override the pinned model hash");
        File.WriteAllText(path, "keep previous");
        Reject(delegate { BundledModel.Ensure(path, otherHash); });
        Assert(File.ReadAllText(path) == "keep previous", "Whole-file checksum failure replaced output."); CleanTemp(path);
        Pass("Whole-file verification rejects tampered combination");

        path = Fixture(out hash); File.Delete(path + ".manifest");
        Reject(delegate { BundledModel.Ensure(path, hash); });
        Assert(!File.Exists(path), "Missing manifest accepted.");
        Pass("Missing manifest fails locally without retrieval");

        path = Fixture(out hash);
        string sharedPath = path; string sharedHash = hash;
        Exception firstError = null, secondError = null;
        var firstThread = new Thread(delegate() { try { BundledModel.Ensure(sharedPath, sharedHash); } catch (Exception ex) { firstError = ex; } });
        var secondThread = new Thread(delegate() { try { BundledModel.Ensure(sharedPath, sharedHash); } catch (Exception ex) { secondError = ex; } });
        firstThread.Start(); secondThread.Start();
        Assert(firstThread.Join(30000) && secondThread.Join(30000), "Concurrent reconstruction timed out.");
        if (firstError != null) throw firstError;
        if (secondError != null) throw secondError;
        Assert(Hash(File.ReadAllBytes(path)) == hash, "Concurrent reconstruction corrupted output."); CleanTemp(path);
        Pass("Concurrent preparation produces a single verified model");
        Console.WriteLine("BUNDLE_TESTS_OK " + passed);
    }
}
