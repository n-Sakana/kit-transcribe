using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NAudio.Wave;
using SherpaOnnx;

// A session owns its native model and VAD. Tests inject a decoder without loading models.
public interface ISpeechDecoder : IDisposable
{
    List<string> Feed(float[] samples);
    List<string> Flush();
}

public sealed class WhisperSpeechDecoder : ISpeechDecoder
{
    private OfflineRecognizer recognizer;
    private VoiceActivityDetector vad;
    private readonly List<float> history = new List<float>();
    private int historyStart;

    public static OfflineRecognizerConfig CreateConfig(string modelDirectory, bool refinement)
    {
        string name = refinement ? "turbo" : "small";
        string directory = Path.Combine(modelDirectory, refinement ? "whisper-large-v3-turbo" : "whisper-small");
        // Explicitly set nested struct defaults: Windows PowerShell uses the C# 5 compiler.
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = refinement ? 128 : 80;
        config.ModelConfig.Whisper.Encoder = Path.Combine(directory, name + "-encoder.int8.onnx");
        config.ModelConfig.Whisper.Decoder = Path.Combine(directory, name + "-decoder.int8.onnx");
        config.ModelConfig.Whisper.Language = "ja";
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Whisper.TailPaddings = 300;
        config.ModelConfig.Tokens = Path.Combine(directory, name + "-tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
        return config;
    }

    public static VadModelConfig CreateVadConfig(string modelDirectory, bool refinement)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = Path.Combine(modelDirectory, "silero_vad.onnx");
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.WindowSize = 512;
        config.SileroVad.MaxSpeechDuration = refinement ? 25.0f : 6.0f;
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";
        return config;
    }

    public WhisperSpeechDecoder(string modelDirectory, bool refinement)
    {
        var config = CreateConfig(modelDirectory, refinement);
        string encoderHash = refinement
            ? "b02dcdf54f348741e93fe732b67d933c8dcb6735655f710640143081db38878b"
            : "4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9";
        string decoderHash = refinement
            ? "20accd02388482eb3a46bd615631adfdc85e1eb2c7db9ea3f02a40ffe6b81547"
            : "acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee";
        BundledModel.Ensure(config.ModelConfig.Whisper.Encoder, encoderHash);
        BundledModel.Ensure(config.ModelConfig.Whisper.Decoder, decoderHash);
        VerifyFile(config.ModelConfig.Tokens, refinement
            ? "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"
            : "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126");
        VerifyTokens(config.ModelConfig.Tokens);
        VerifyFile(Path.Combine(modelDirectory, "silero_vad.onnx"),
            "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6");
        try
        {
            recognizer = new OfflineRecognizer(config);
            CheckHandle(recognizer);
            vad = new VoiceActivityDetector(CreateVadConfig(modelDirectory, refinement), 40.0f);
            CheckHandle(vad);
            // Keep the initial VAD offset and pre-roll history in the same sample timeline.
            var silence = new float[8000];
            history.AddRange(silence);
            vad.AcceptWaveform(silence);
        }
        catch { Dispose(); throw; }
    }

    public List<string> Feed(float[] samples)
    {
        if (vad == null) throw new ObjectDisposedException("WhisperSpeechDecoder");
        if (samples == null) throw new ArgumentNullException("samples");
        history.AddRange(samples);
        vad.AcceptWaveform(samples);
        return Drain();
    }

    public List<string> Flush()
    {
        if (vad == null) throw new ObjectDisposedException("WhisperSpeechDecoder");
        vad.Flush();
        return Drain();
    }

    private List<string> Drain()
    {
        var results = new List<string>();
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            try
            {
                int start = Math.Max(historyStart, segment.Start - 4000);
                int preCount = Math.Max(0, segment.Start - start);
                int index = start - historyStart;
                if (index < 0 || index + preCount > history.Count) preCount = 0;
                var audio = new float[preCount + segment.Samples.Length];
                if (preCount > 0) history.CopyTo(index, audio, 0, preCount);
                Array.Copy(segment.Samples, 0, audio, preCount, segment.Samples.Length);
                using (var stream = recognizer.CreateStream())
                {
                    CheckHandle(stream);
                    stream.AcceptWaveform(16000, audio);
                    recognizer.Decode(stream);
                    string text = stream.Result.Text;
                    if (!string.IsNullOrWhiteSpace(text)) results.Add(text.Trim());
                }
            }
            finally { vad.Pop(); }
        }
        if (history.Count > 16000 * 40)
        {
            int remove = history.Count - 16000 * 35;
            history.RemoveRange(0, remove);
            historyStart += remove;
        }
        return results;
    }

    public void Dispose()
    {
        try { if (vad != null) vad.Dispose(); }
        finally
        {
            vad = null;
            if (recognizer != null) { recognizer.Dispose(); recognizer = null; }
        }
    }

    // The bundled 1.13.4 managed wrapper does not throw for a null native handle.
    // Check before the first native call rather than risking an access violation.
    private static void CheckHandle(object instance)
    {
        var field = instance.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("Unsupported sherpa-onnx wrapper; restore the bundled DLLs.");
        object value = field.GetValue(instance);
        IntPtr handle = value is HandleRef ? ((HandleRef)value).Handle : (IntPtr)value;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Native speech engine initialization failed. Check models, DLLs and available memory.");
    }

    public static void VerifyFile(string path, string expectedHash)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Bundled model file missing. Restore the complete application folder: " + path, path);
        using (var input = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundled model checksum mismatch. Restore the complete application folder: " + path);
        }
    }

    public static void VerifyTokens(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bundled token file missing. Restore the complete application folder: " + path, path);
        int expectedId = 0;
        using (var reader = File.OpenText(path))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                int split = line.LastIndexOf(' ');
                int id;
                if (split <= 0 || !int.TryParse(line.Substring(split + 1), out id) || id != expectedId++)
                    throw new InvalidDataException("Invalid Whisper token table: " + path);
                string token = line.Substring(0, split);
                // sherpa's token table uses '=' for the final empty token, not strict Base64.
                if (id == 50256)
                {
                    if (token != "=") throw new InvalidDataException("Invalid Whisper empty-token marker: " + path);
                }
                else
                {
                    try { Convert.FromBase64String(token); }
                    catch (FormatException) { throw new InvalidDataException("Invalid Whisper token encoding: " + path); }
                }
            }
        }
        if (expectedId != 50257) throw new InvalidDataException("Unexpected Whisper token count: " + path);
    }
}

// Disk-backed audio queue. Inference never runs in the microphone callback and cannot
// overflow a bounded in-memory queue. The header is refreshed after every write.
public sealed class PcmSpool : IDisposable
{
    private readonly object gate = new object();
    private FileStream writer;
    private FileStream reader;
    private readonly AutoResetEvent available = new AutoResetEvent(false);
    private long length;
    private bool complete;
    private bool disposed;
    public string FilePath { get; private set; }
    public long DataLength { get { lock (gate) return length; } }
    public bool IsComplete { get { lock (gate) return complete; } }

    public PcmSpool(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            writer = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            using (var header = new MemoryStream())
            using (var binary = new BinaryWriter(header))
            {
                binary.Write(Encoding.ASCII.GetBytes("RIFF")); binary.Write(36);
                binary.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); binary.Write(16);
                binary.Write((short)1); binary.Write((short)1); binary.Write(16000);
                binary.Write(32000); binary.Write((short)2); binary.Write((short)16);
                binary.Write(Encoding.ASCII.GetBytes("data")); binary.Write(0);
                writer.Write(header.ToArray(), 0, 44);
            }
            writer.Flush();
            reader = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader.Position = 44;
        }
        catch { if (writer != null) writer.Dispose(); available.Dispose(); throw; }
    }

    public bool Append(byte[] pcm, int count, float gain, out float level)
    {
        float[] samples = TranscriberEngine.ConvertPcm16(pcm, count, gain, out level);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            int value = Math.Max(-32768, Math.Min(32767, (int)Math.Round(samples[i] * 32768.0)));
            bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8);
        }
        lock (gate)
        {
            if (complete) return false;
            // Bound RIFF sizes and the VAD's signed 32-bit sample offset to 24 hours.
            if (length + bytes.Length > 16000L * 2 * 60 * 60 * 24)
                throw new IOException("The 24-hour recording limit was reached. Start a new recording.");
            writer.Position = 44 + length;
            writer.Write(bytes, 0, bytes.Length);
            length += bytes.Length;
            UpdateHeader();
            available.Set();
            return true;
        }
    }

    private void UpdateHeader()
    {
        writer.Position = 4;
        byte[] size = BitConverter.GetBytes((uint)(36 + length)); writer.Write(size, 0, 4);
        writer.Position = 40;
        size = BitConverter.GetBytes((uint)length); writer.Write(size, 0, 4);
        writer.Flush();
    }

    public float[] ReadSamples()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException("PcmSpool");
            int count = (int)Math.Min(1024, 44 + length - reader.Position);
            if (count <= 0) return new float[0];
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = reader.Read(buffer, read, count - read);
                if (n == 0) throw new EndOfStreamException("Recorded audio was truncated.");
                read += n;
            }
            float ignored;
            return TranscriberEngine.ConvertPcm16(buffer, count, 1.0f, out ignored);
        }
    }

    public void WaitForData() { available.WaitOne(50); }
    public void Complete()
    {
        lock (gate)
        {
            if (complete) return;
            complete = true;
            try { UpdateHeader(); }
            finally { writer.Dispose(); writer = null; available.Set(); }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { Complete(); }
            finally { reader.Dispose(); available.Dispose(); disposed = true; }
        }
    }
}

public sealed class TranscriberEngine : IDisposable
{
    private readonly object gate = new object();
    private readonly Func<bool, ISpeechDecoder> decoderFactory;
    private readonly string recordingDirectory;
    private readonly ConcurrentQueue<string> texts = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> refined = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> errors = new ConcurrentQueue<string>();
    private Thread worker;
    private WaveInEvent capture;
    private PcmSpool activeSpool;
    private bool testSession;
    private volatile bool stopRequested;
    private volatile bool cancelRequested;
    private long stopTicks;
    private bool disposed;
    private int failed;
    private string state = "Idle";
    private string lastRecording = "";
    private string currentRecording = "";
    private float gain = 1.0f;
    private float latestLevel;
    private double progress;
    private double backlogSeconds;
    private int sessionVersion;

    public TranscriberEngine(string modelDirectory, string outputDirectory)
        : this(outputDirectory, delegate(bool final) { return new WhisperSpeechDecoder(modelDirectory, final); }) { }

    public TranscriberEngine(string outputDirectory, Func<bool, ISpeechDecoder> factory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Output directory is required.");
        if (factory == null) throw new ArgumentNullException("factory");
        recordingDirectory = Path.GetFullPath(outputDirectory);
        decoderFactory = factory;
    }

    public bool IsBusy { get { lock (gate) return worker != null && worker.IsAlive; } }
    public bool IsRunning { get { return IsBusy; } }
    public bool IsCancellationRequested { get { return cancelRequested; } }
    public string State { get { lock (gate) return state; } }
    public float LatestLevel { get { lock (gate) return latestLevel; } }
    public double Progress { get { lock (gate) return progress; } }
    public double BacklogSeconds { get { lock (gate) return backlogSeconds; } }
    public string LastRecordingPath { get { lock (gate) return lastRecording; } }
    public string CurrentRecordingPath { get { lock (gate) return currentRecording; } }
    public int SessionVersion { get { lock (gate) return sessionVersion; } }
    public bool HasRecording { get { return File.Exists(LastRecordingPath); } }
    public float Gain
    {
        get { lock (gate) return gain; }
        set
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException("value");
            lock (gate) gain = Math.Max(0.5f, Math.Min(4.0f, value));
        }
    }

    public void Start() { StartRecordingJob(false); }
    public void StartTestSession() { StartRecordingJob(true); }
    private void StartRecordingJob(bool test)
    {
        StartJob("LoadingSmall", delegate { Record(test); });
    }
    public void StartRefinement()
    {
        string path = LastRecordingPath;
        if (!File.Exists(path)) throw new InvalidOperationException("No completed recording is available.");
        StartJob("LoadingTurbo", delegate { Refine(path); });
    }
    private void StartJob(string initialState, ThreadStart action)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException("TranscriberEngine");
            if (worker != null && worker.IsAlive) throw new InvalidOperationException("An operation is already running.");
            stopRequested = false; cancelRequested = false; stopTicks = 0; failed = 0;
            progress = 0; backlogSeconds = 0; state = initialState;
            worker = new Thread(delegate()
            {
                try { action(); }
                catch (Exception ex) { ReportError(ex); }
                finally { lock (gate) { latestLevel = 0; state = failed != 0 ? "Error" : "Idle"; } }
            });
            worker.IsBackground = true;
            worker.Name = "pub-transcribe-worker";
            worker.Start();
        }
    }

    public void FeedPcm16ForTest(byte[] buffer, int count)
    {
        PcmSpool spool;
        lock (gate)
        {
            if (!testSession || activeSpool == null || state != "Recording")
                throw new InvalidOperationException("A test recording is not ready.");
            spool = activeSpool;
        }
        float level;
        if (!spool.Append(buffer, count, Gain, out level)) throw new InvalidOperationException("Recording has stopped.");
    }

    public void Stop()
    {
        WaveInEvent input; PcmSpool spool;
        lock (gate)
        {
            if (!stopRequested) Interlocked.Exchange(ref stopTicks, DateTime.UtcNow.Ticks);
            stopRequested = true;
            input = capture; spool = activeSpool;
            if (state == "Recording" || state == "LoadingSmall") state = "Stopping";
        }
        try
        {
            if (input != null) input.StopRecording();
            else if (spool != null) spool.Complete();
        }
        catch (Exception ex)
        {
            ReportError(ex);
            try { if (spool != null) spool.Complete(); }
            catch (Exception cleanupError) { ReportError(cleanupError); }
        }
    }
    public void Cancel()
    {
        cancelRequested = true;
        Stop();
    }
    public bool WaitForStop(int milliseconds)
    {
        Thread thread; lock (gate) thread = worker;
        return thread == null || (Thread.CurrentThread != thread && thread.Join(milliseconds));
    }
    public bool TryGetText(out string text) { return texts.TryDequeue(out text); }
    public bool TryGetRefinedText(out string text) { return refined.TryDequeue(out text); }
    public bool TryGetError(out string error) { return errors.TryDequeue(out error); }

    private void Record(bool test)
    {
        ISpeechDecoder decoder = null;
        PcmSpool spool = null;
        WaveInEvent input = null;
        EventHandler<WaveInEventArgs> onData = null;
        EventHandler<StoppedEventArgs> onStopped = null;
        using (var stopped = new ManualResetEvent(test))
        {
            try
            {
                if (!test && WaveInEvent.DeviceCount < 1) throw new InvalidOperationException("No microphone input device was found.");
                decoder = decoderFactory(false);
                if (cancelRequested || stopRequested) return;
                spool = new PcmSpool(recordingDirectory);
                lock (gate) { activeSpool = spool; testSession = test; }
                if (!test)
                {
                    input = new WaveInEvent();
                    input.WaveFormat = new WaveFormat(16000, 16, 1);
                    input.BufferMilliseconds = 100; input.NumberOfBuffers = 3;
                    onData = delegate(object sender, WaveInEventArgs e)
                    {
                        try
                        {
                            float level;
                            if (spool.Append(e.Buffer, e.BytesRecorded, Gain, out level))
                                lock (gate) latestLevel = level;
                        }
                        catch (Exception ex) { ReportError(ex); Stop(); }
                    };
                    onStopped = delegate(object sender, StoppedEventArgs e)
                    {
                        try
                        {
                            if (e.Exception != null) ReportError(e.Exception);
                            spool.Complete();
                        }
                        catch (Exception ex) { ReportError(ex); }
                        finally { try { stopped.Set(); } catch (ObjectDisposedException) { } }
                    };
                    input.DataAvailable += onData; input.RecordingStopped += onStopped;
                    lock (gate) capture = input;
                    input.StartRecording();
                }
                lock (gate)
                {
                    currentRecording = spool.FilePath; lastRecording = ""; sessionVersion++;
                    state = stopRequested ? "Stopping" : "Recording";
                }
                if (stopRequested) Stop();
                long processed = 0;
                while (!cancelRequested)
                {
                    if (stopRequested && !spool.IsComplete &&
                        DateTime.UtcNow.Ticks - Interlocked.Read(ref stopTicks) > TimeSpan.FromSeconds(5).Ticks)
                    {
                        ReportError(new TimeoutException("Microphone did not signal that recording stopped."));
                        spool.Complete();
                    }
                    float[] samples = spool.ReadSamples();
                    if (samples.Length > 0)
                    {
                        foreach (string text in decoder.Feed(samples)) texts.Enqueue(text);
                        processed += samples.Length;
                        lock (gate) backlogSeconds = Math.Max(0, spool.DataLength / 32000.0 - processed / 16000.0);
                    }
                    else if (spool.IsComplete) break;
                    else spool.WaitForData();
                }
                if (!cancelRequested) foreach (string text in decoder.Flush()) texts.Enqueue(text);
            }
            finally
            {
                // Detach callbacks and release capture before exposing the completed session.
                lock (gate) { capture = null; activeSpool = null; testSession = false; }
                if (input != null)
                {
                    try
                    {
                        input.StopRecording();
                        if (!stopped.WaitOne(5000)) ReportError(new TimeoutException("Microphone stop timed out."));
                    }
                    catch (Exception ex) { ReportError(ex); }
                    input.DataAvailable -= onData; input.RecordingStopped -= onStopped;
                    SafeDispose(input);
                }
                if (spool != null)
                {
                    try { spool.Complete(); }
                    catch (Exception ex) { ReportError(ex); }
                    if (spool.DataLength > 0) { lock (gate) lastRecording = spool.FilePath; }
                    SafeDispose(spool);
                }
                SafeDispose(decoder);
                lock (gate) { capture = null; activeSpool = null; testSession = false; }
            }
        }
    }

    private void Refine(string path)
    {
        var output = new StringBuilder();
        using (ISpeechDecoder decoder = decoderFactory(true))
        using (var reader = new WaveFileReader(path))
        {
            ValidateWave(reader);
            lock (gate) state = "Refining";
            var bytes = new byte[1024];
            int count;
            while (!cancelRequested && (count = reader.Read(bytes, 0, bytes.Length)) > 0)
            {
                float ignored;
                foreach (string text in decoder.Feed(ConvertPcm16(bytes, count, 1.0f, out ignored))) output.AppendLine(text);
                lock (gate) progress = reader.Length == 0 ? 1 : (double)reader.Position / reader.Length;
            }
            if (cancelRequested) return;
            foreach (string text in decoder.Flush()) output.AppendLine(text);
            if (cancelRequested) return;
        }
        // Commit only a complete result: failed/cancelled reruns never replace previous text.
        if (cancelRequested) return;
        refined.Enqueue(output.ToString());
        lock (gate) progress = 1;
    }

    public static string[] DecodeFileSegments(string modelDirectory, string wavPath, bool refinement)
    {
        var output = new List<string>();
        using (var decoder = new WhisperSpeechDecoder(modelDirectory, refinement))
        using (var reader = new WaveFileReader(wavPath))
        {
            ValidateWave(reader);
            var buffer = new byte[1024]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                float ignored;
                output.AddRange(decoder.Feed(ConvertPcm16(buffer, count, 1.0f, out ignored)));
            }
            output.AddRange(decoder.Flush());
        }
        return output.ToArray();
    }
    private static void ValidateWave(WaveFileReader reader)
    {
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 16000 || format.Channels != 1 || format.BitsPerSample != 16 || (reader.Length & 1) != 0)
            throw new InvalidDataException("WAV must be 16 kHz mono 16-bit PCM.");
    }
    private void ReportError(Exception error)
    {
        Interlocked.Exchange(ref failed, 1);
        errors.Enqueue(error.ToString());
    }
    private void SafeDispose(IDisposable item)
    {
        if (item == null) return;
        try { item.Dispose(); } catch (Exception ex) { ReportError(ex); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; }
        Cancel();
        if (!WaitForStop(0)) throw new InvalidOperationException("Cancel and wait for the worker before disposing the engine.");
        lock (gate) disposed = true;
    }
    public static float[] ConvertPcm16(byte[] buffer, int count, float gainValue, out float level)
    {
        if (buffer == null) throw new ArgumentNullException("buffer");
        if (count < 0 || count > buffer.Length || (count & 1) != 0) throw new ArgumentOutOfRangeException("count");
        if (float.IsNaN(gainValue) || float.IsInfinity(gainValue)) throw new ArgumentOutOfRangeException("gainValue");
        gainValue = Math.Max(0.5f, Math.Min(4.0f, gainValue));
        var samples = new float[count / 2]; double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float value = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8)) / 32768.0f * gainValue;
            samples[i] = Math.Max(-1.0f, Math.Min(1.0f, value));
            sum += samples[i] * samples[i];
        }
        double rms = samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
        double db = rms <= 0.000001 ? -120 : 20 * Math.Log10(rms);
        level = (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
        return samples;
    }
}
