using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

public sealed class FakeSpeechDecoder : ISpeechDecoder
{
    public long Samples;
    public bool Fail;
    public bool Disposed;
    public ManualResetEvent Block;
    public List<string> Feed(float[] samples)
    {
        if (Block != null && !Block.WaitOne(5000)) throw new TimeoutException("Test gate timed out.");
        if (Fail) throw new InvalidOperationException("Injected decode failure.");
        Samples += samples.Length;
        return new List<string>();
    }
    public List<string> Flush() { return new List<string> { "decoded:" + Samples }; }
    public void Dispose() { Disposed = true; }
}

public static class EngineRegressionTests
{
    private static int passed;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Case(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void WaitReady(TranscriberEngine engine)
    {
        var watch = Stopwatch.StartNew();
        while (engine.State != "Recording" && watch.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert(engine.State == "Recording", "Recording did not become ready: " + engine.State);
    }
    private static void Close(TranscriberEngine engine)
    {
        engine.Cancel(); Assert(engine.WaitForStop(10000), "Worker did not stop."); engine.Dispose();
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static byte[] Audio(int samples)
    {
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++) { bytes[i * 2] = 0; bytes[i * 2 + 1] = 32; }
        return bytes;
    }

    public static void Run(string directory)
    {
        passed = 0;
        Case("Whisper model roles, Japanese and feature dimensions", delegate {
            var small = WhisperSpeechDecoder.CreateConfig(directory, false);
            var turbo = WhisperSpeechDecoder.CreateConfig(directory, true);
            Assert(small.ModelConfig.Whisper.Encoder.EndsWith("whisper-small" + Path.DirectorySeparatorChar + "small-encoder.int8.onnx"), "Wrong small model.");
            Assert(turbo.ModelConfig.Whisper.Encoder.EndsWith("whisper-large-v3-turbo" + Path.DirectorySeparatorChar + "turbo-encoder.int8.onnx"), "Wrong turbo model.");
            Assert(small.FeatConfig.FeatureDim == 80 && turbo.FeatConfig.FeatureDim == 128, "Wrong mel dimensions.");
            Assert(small.ModelConfig.Whisper.Language == "ja" && turbo.ModelConfig.Whisper.Task == "transcribe", "Wrong language/task.");
        });
        Case("Explicit VAD defaults and bounded speech segments", delegate {
            var live = WhisperSpeechDecoder.CreateVadConfig(directory, false);
            var final = WhisperSpeechDecoder.CreateVadConfig(directory, true);
            Assert(live.SileroVad.WindowSize == 512 && live.SileroVad.MinSpeechDuration == 0.25f, "Missing struct defaults.");
            Assert(live.SileroVad.MaxSpeechDuration == 6 && final.SileroVad.MaxSpeechDuration == 25, "Wrong segmentation.");
            Assert(live.Provider == "cpu" && live.NumThreads == 1, "Invalid VAD provider/threads.");
        });
        Case("PCM clipping and finite levels", delegate {
            float level; var values = TranscriberEngine.ConvertPcm16(new byte[] { 0, 128, 255, 127 }, 4, 4, out level);
            Assert(values[0] == -1 && values[1] == 1 && level >= 0 && level <= 1, "Clipping failed.");
            Assert(TranscriberEngine.ConvertPcm16(new byte[0], 0, 1, out level).Length == 0 && level == 0, "Empty PCM failed.");
        });
        Case("PCM rejects odd counts and non-finite gain", delegate {
            float level;
            Throws<ArgumentOutOfRangeException>(delegate { TranscriberEngine.ConvertPcm16(new byte[2], 1, 1, out level); });
            Throws<ArgumentOutOfRangeException>(delegate { TranscriberEngine.ConvertPcm16(new byte[2], 2, float.NaN, out level); });
            Throws<ArgumentOutOfRangeException>(delegate { TranscriberEngine.ConvertPcm16(new byte[2], 4, 1, out level); });
        });
        Case("WAV header remains valid during recording and after completion", delegate {
            string path;
            using (var spool = new PcmSpool(directory)) {
                path = spool.FilePath; float level;
                spool.Append(Audio(1600), 3200, 1, out level);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    file.Position = 40; var size = new byte[4]; file.Read(size, 0, 4);
                    Assert(BitConverter.ToInt32(size, 0) == 3200, "Live WAV header is stale.");
                }
                spool.Complete(); Assert(!spool.Append(Audio(1), 2, 1, out level), "Late callback accepted audio.");
                int samples = 0; float[] block;
                while ((block = spool.ReadSamples()).Length > 0) samples += block.Length;
                Assert(samples == 1600, "Spool lost samples.");
            }
            using (var wav = new WaveFileReader(path)) Assert(wav.Length == 3200, "Final WAV is invalid.");
        });
        Case("Construction is lazy and refinement requires a recording", delegate {
            int calls = 0;
            var engine = new TranscriberEngine(directory, delegate(bool final) { calls++; return new FakeSpeechDecoder(); });
            try {
                Assert(calls == 0 && !engine.IsBusy, "Constructor loaded a model.");
                Throws<InvalidOperationException>(delegate { engine.StartRefinement(); });
                Throws<ArgumentOutOfRangeException>(delegate { engine.Gain = float.PositiveInfinity; });
            } finally { Close(engine); }
        });
        Case("Slow inference preserves more than the old ten-second queue", delegate {
            using (var block = new ManualResetEvent(false)) {
                var decoder = new FakeSpeechDecoder { Block = block };
                var engine = new TranscriberEngine(directory, delegate(bool final) { return decoder; });
                try {
                    engine.StartTestSession(); WaitReady(engine);
                    var audio = Audio(1600);
                    for (int i = 0; i < 120; i++) engine.FeedPcm16ForTest(audio, audio.Length);
                    engine.Stop(); block.Set(); Assert(engine.WaitForStop(10000), "Stop timed out.");
                    Assert(decoder.Samples == 192000, "Slow recognition lost audio.");
                    Assert(decoder.Disposed && engine.HasRecording, "Session resources were not finalized.");
                    using (var wav = new WaveFileReader(engine.LastRecordingPath)) Assert(wav.Length == 384000, "Saved audio was truncated.");
                    string text; Assert(engine.TryGetText(out text) && text == "decoded:192000", "Final flush is missing.");
                } finally { block.Set(); Close(engine); }
            }
        });
        Case("Manual refinement re-reads the full recording with the final model", delegate {
            int finalCalls = 0;
            var engine = new TranscriberEngine(directory, delegate(bool final) { if (final) finalCalls++; return new FakeSpeechDecoder(); });
            try {
                engine.StartTestSession(); WaitReady(engine); engine.FeedPcm16ForTest(Audio(3210), 6420);
                engine.Stop(); Assert(engine.WaitForStop(5000), "Stop timed out.");
                Assert(finalCalls == 0, "Refinement started automatically.");
                engine.StartRefinement(); Assert(engine.WaitForStop(5000), "Refinement timed out.");
                string text; Assert(engine.TryGetRefinedText(out text) && text.Trim() == "decoded:3210", "Refinement did not read all audio.");
                Assert(finalCalls == 1 && engine.Progress == 1, "Wrong model dispatch/progress.");
            } finally { Close(engine); }
        });
        Case("Refinement failure preserves recording and publishes no partial result", delegate {
            var engine = new TranscriberEngine(directory, delegate(bool final) { return new FakeSpeechDecoder { Fail = final }; });
            try {
                engine.StartTestSession(); WaitReady(engine); engine.FeedPcm16ForTest(Audio(1000), 2000); engine.Stop();
                Assert(engine.WaitForStop(5000), "Stop timed out."); string path = engine.LastRecordingPath;
                engine.StartRefinement(); Assert(engine.WaitForStop(5000), "Refinement timed out.");
                string text; Assert(!engine.TryGetRefinedText(out text), "Partial result was published.");
                Assert(engine.State == "Error" && engine.TryGetError(out text), "Error was hidden.");
                Assert(engine.LastRecordingPath == path && File.Exists(path), "Recording was lost.");
            } finally { Close(engine); }
        });
        Case("Double start rejected and stopping during model load is safe", delegate {
            using (var block = new ManualResetEvent(false)) {
                var engine = new TranscriberEngine(directory, delegate(bool final) {
                    if (!block.WaitOne(5000)) throw new TimeoutException(); return new FakeSpeechDecoder();
                });
                try {
                    engine.StartTestSession();
                    Throws<InvalidOperationException>(delegate { engine.StartTestSession(); });
                    engine.Stop(); block.Set(); Assert(engine.WaitForStop(5000), "Loading cancellation hung.");
                    Assert(engine.SessionVersion == 0 && !engine.HasRecording, "Cancelled load started recording.");
                } finally { block.Set(); Close(engine); }
            }
        });
        Case("Cancelled refinement never overwrites previous results", delegate {
            using (var block = new ManualResetEvent(false)) {
                var engine = new TranscriberEngine(directory, delegate(bool final) { return new FakeSpeechDecoder { Block = final ? block : null }; });
                try {
                    engine.StartTestSession(); WaitReady(engine); engine.FeedPcm16ForTest(Audio(1600), 3200); engine.Stop();
                    Assert(engine.WaitForStop(5000), "Stop timed out.");
                    engine.StartRefinement(); engine.Cancel(); block.Set(); Assert(engine.WaitForStop(5000), "Cancel timed out.");
                    string text; Assert(!engine.TryGetRefinedText(out text) && engine.HasRecording, "Cancel destroyed data.");
                } finally { block.Set(); Close(engine); }
            }
        });
        Case("Repeated sessions create distinct files and release workers", delegate {
            var engine = new TranscriberEngine(directory, delegate(bool final) { return new FakeSpeechDecoder(); });
            try {
                var paths = new HashSet<string>();
                for (int i = 0; i < 8; i++) {
                    engine.StartTestSession(); WaitReady(engine); engine.FeedPcm16ForTest(Audio(800), 1600); engine.Stop();
                    Assert(engine.WaitForStop(5000), "Repeated stop timed out.");
                    Assert(paths.Add(engine.LastRecordingPath), "Recording path was reused.");
                }
                Assert(engine.SessionVersion == 8, "Wrong session version.");
            } finally { Close(engine); }
        });
        Case("Model load failure can be retried without restarting the app", delegate {
            int attempts = 0;
            var engine = new TranscriberEngine(directory, delegate(bool final) {
                if (++attempts == 1) throw new IOException("Injected load error."); return new FakeSpeechDecoder();
            });
            try {
                engine.StartTestSession(); Assert(engine.WaitForStop(5000), "Failed load hung.");
                Assert(engine.State == "Error", "Load failure was hidden.");
                engine.StartTestSession(); WaitReady(engine); engine.Stop(); Assert(engine.WaitForStop(5000), "Retry hung.");
                Assert(engine.State == "Idle", "Retry did not recover.");
            } finally { Close(engine); }
        });
        Case("Corrupt and missing model assets fail before native initialization", delegate {
            string file = Path.Combine(directory, "bad-model"); File.WriteAllText(file, "invalid");
            Throws<InvalidDataException>(delegate { WhisperSpeechDecoder.VerifyFile(file, new string('0', 64)); });
            Throws<FileNotFoundException>(delegate { WhisperSpeechDecoder.VerifyFile(file + ".missing", new string('0', 64)); });
            Throws<InvalidDataException>(delegate { WhisperSpeechDecoder.VerifyTokens(file); });
        });
        Console.WriteLine("ENGINE_TESTS_OK " + passed);
    }
}
