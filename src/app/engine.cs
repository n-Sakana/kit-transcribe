using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NAudio.Wave;
using SherpaOnnx;

public sealed class TranscriberEngine : IDisposable
{
    private const int SampleRate = 16000;
    private const int PreRollSamples = SampleRate / 4;
    private const int HistoryKeepSamples = SampleRate * 35;
    private const int HistoryTrimAtSamples = SampleRate * 40;
    private const string EncoderName = "encoder-epoch-99-avg-1.int8.onnx";
    private const string EncoderHash = "2C7BD08A8A99F9DDD0D9E458456577B1F6279214E51426F114F9ECED44C54E1D";
    private const long EncoderLength = 154670139L;
    private static readonly string[] EncoderParts =
    {
        "encoder-epoch-99-avg-1.int8.onnx.part01",
        "encoder-epoch-99-avg-1.int8.onnx.part02"
    };
    private static readonly string[] EncoderPartHashes =
    {
        "48895C41020DA39B020128252B9053152E0C5BB6CA555C49DFD5756811223517",
        "9B06F691E3505A5EE5760E57EB559E40A5C95CCF9FB08881717DD5EB74EC4F87"
    };

    private OfflineRecognizer recognizer;
    private VoiceActivityDetector vad;
    private readonly List<float> sampleHistory = new List<float>();
    private readonly object stateLock = new object();
    private readonly ConcurrentQueue<string> textQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> errorQueue = new ConcurrentQueue<string>();
    private readonly ManualResetEvent captureStoppedEvent = new ManualResetEvent(true);
    private int historyStart;
    private float gain = 1.0f;
    private float latestLevel;
    private bool running;
    private bool stopRequested;
    private bool microphoneSession;
    private BlockingCollection<float[]> audioQueue;
    private Thread workerThread;
    private WaveInEvent waveIn;
    private bool disposed;

    public float Gain
    {
        get
        {
            lock (stateLock) return gain;
        }
        set
        {
            float next = value;
            if (next < 0.5f) next = 0.5f;
            if (next > 4.0f) next = 4.0f;
            lock (stateLock) gain = next;
        }
    }

    public float LatestLevel
    {
        get
        {
            lock (stateLock) return latestLevel;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (stateLock) return running;
        }
    }

    public TranscriberEngine(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir))
            throw new ArgumentException("Model directory is required.", "modelDir");

        string encoder = EnsureModel(modelDir);
        string decoder = RequireFile(modelDir, "decoder-epoch-99-avg-1.int8.onnx");
        string joiner = RequireFile(modelDir, "joiner-epoch-99-avg-1.int8.onnx");
        string tokens = RequireFile(modelDir, "tokens.txt");
        string silero = RequireFile(modelDir, "silero_vad.onnx");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        recognizer = new OfflineRecognizer(config);

        var vadConfig = new VadModelConfig();
        var sileroConfig = vadConfig.SileroVad;
        sileroConfig.Model = silero;
        vadConfig.SileroVad = sileroConfig;
        vadConfig.SampleRate = SampleRate;
        vadConfig.NumThreads = 1;
        vadConfig.Debug = 0;
        vad = new VoiceActivityDetector(vadConfig, 30.0f);
        ResetRecognitionSession();
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (WaveInEvent.DeviceCount < 1)
            throw new InvalidOperationException("No microphone input device was found.");
        StartWorker(true);
    }

    public void StartTestSession()
    {
        ThrowIfDisposed();
        StartWorker(false);
    }

    public void FeedPcm16ForTest(byte[] buffer, int bytesRecorded)
    {
        lock (stateLock)
        {
            if (!running || microphoneSession)
                throw new InvalidOperationException("A test session is not running.");
        }
        QueuePcm(buffer, bytesRecorded, true);
    }

    public void Stop()
    {
        WaveInEvent capture;
        bool isMicrophone;
        lock (stateLock)
        {
            if (!running) return;
            stopRequested = true;
            capture = waveIn;
            isMicrophone = microphoneSession;
        }

        if (!isMicrophone)
        {
            CompleteAudioQueue();
            return;
        }
        if (capture != null)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                EnqueueError("Microphone stop failed: " + ex.Message);
                ForceDisposeCapture(capture);
                CompleteAudioQueue();
            }
        }
    }

    public bool WaitForStop(int timeoutMilliseconds)
    {
        Thread thread;
        lock (stateLock) thread = workerThread;
        if (thread == null) return true;
        if (Thread.CurrentThread == thread) return false;
        return thread.Join(timeoutMilliseconds);
    }

    public bool TryGetText(out string text)
    {
        return textQueue.TryDequeue(out text);
    }

    public bool TryGetError(out string error)
    {
        return errorQueue.TryDequeue(out error);
    }

    public static float[] ConvertPcm16(byte[] buffer, int bytesRecorded, float gainValue, out float normalizedLevel)
    {
        if (buffer == null) throw new ArgumentNullException("buffer");
        if (bytesRecorded < 0 || bytesRecorded > buffer.Length || (bytesRecorded & 1) != 0)
            throw new ArgumentOutOfRangeException("bytesRecorded", "PCM byte count must be even and inside the buffer.");

        if (gainValue < 0.5f) gainValue = 0.5f;
        if (gainValue > 4.0f) gainValue = 4.0f;
        int count = bytesRecorded / 2;
        var samples = new float[count];
        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            float value = BitConverter.ToInt16(buffer, i * 2) / 32768.0f * gainValue;
            if (value > 1.0f) value = 1.0f;
            else if (value < -1.0f) value = -1.0f;
            samples[i] = value;
            sum += value * value;
        }

        double rms = count == 0 ? 0.0 : Math.Sqrt(sum / count);
        double db = rms <= 0.000001 ? -120.0 : 20.0 * Math.Log10(rms);
        double level = (db + 60.0) / 60.0;
        if (level < 0.0) level = 0.0;
        if (level > 1.0) level = 1.0;
        normalizedLevel = (float)level;
        return samples;
    }

    public static string EnsureModel(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir))
            throw new ArgumentException("Model directory is required.", "modelDir");
        if (!Directory.Exists(modelDir))
            throw new DirectoryNotFoundException("Model directory not found: " + modelDir);

        string fullModelDir = Path.GetFullPath(modelDir);
        string encoderPath = Path.Combine(fullModelDir, EncoderName);
        if (IsValidEncoder(encoderPath)) return encoderPath;

        string mutexName = GetModelMutexName(fullModelDir);
        using (var mutex = new Mutex(false, mutexName))
        {
            bool ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(5));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                if (!ownsMutex)
                    throw new TimeoutException("Timed out while waiting to prepare the encoder model.");

                if (IsValidEncoder(encoderPath)) return encoderPath;

                for (int i = 0; i < EncoderParts.Length; i++)
                {
                    string partPath = RequireFile(fullModelDir, EncoderParts[i]);
                    string actualHash = ComputeSha256(partPath);
                    if (!actualHash.Equals(EncoderPartHashes[i], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Encoder model part checksum mismatch: " + EncoderParts[i]);
                }

                string tempPath = encoderPath + ".tmp." +
                    System.Diagnostics.Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        for (int i = 0; i < EncoderParts.Length; i++)
                        {
                            string partPath = Path.Combine(fullModelDir, EncoderParts[i]);
                            using (var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                    output.Write(buffer, 0, read);
                            }
                        }
                    }

                    var info = new FileInfo(tempPath);
                    if (info.Length != EncoderLength)
                        throw new InvalidDataException("Reconstructed encoder model has the wrong length.");
                    string mergedHash = ComputeSha256(tempPath);
                    if (!mergedHash.Equals(EncoderHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Reconstructed encoder model checksum mismatch.");

                    if (File.Exists(encoderPath)) File.Delete(encoderPath);
                    File.Move(tempPath, encoderPath);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                return encoderPath;
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
        }
    }

    public List<string> Feed(float[] samples)
    {
        ThrowIfDisposed();
        var results = new List<string>();
        if (samples == null || samples.Length == 0) return results;
        AppendHistory(samples);
        vad.AcceptWaveform(samples);
        DrainVad(results);
        return results;
    }

    public List<string> Flush()
    {
        ThrowIfDisposed();
        var results = new List<string>();
        vad.Flush();
        DrainVad(results);
        return results;
    }

    public static string[] DecodeFileSegments(string modelDir, string wavPath)
    {
        float[] samples = ReadWav(wavPath);
        var results = new List<string>();
        using (var engine = new TranscriberEngine(modelDir))
        {
            const int chunkSize = 512;
            for (int offset = 0; offset < samples.Length; offset += chunkSize)
            {
                int count = Math.Min(chunkSize, samples.Length - offset);
                var chunk = new float[count];
                Array.Copy(samples, offset, chunk, 0, count);
                results.AddRange(engine.Feed(chunk));
            }
            results.AddRange(engine.Flush());
        }
        return results.ToArray();
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed) return;
        }
        Stop();
        if (!WaitForStop(30000))
            throw new TimeoutException("Timed out while stopping the transcription worker.");
        lock (stateLock) disposed = true;
        if (vad != null)
        {
            vad.Dispose();
            vad = null;
        }
        if (recognizer != null)
        {
            recognizer.Dispose();
            recognizer = null;
        }
        captureStoppedEvent.Dispose();
        GC.SuppressFinalize(this);
    }

    private void StartWorker(bool useMicrophone)
    {
        lock (stateLock)
        {
            if (running) throw new InvalidOperationException("Transcription is already running.");
            microphoneSession = useMicrophone;
            stopRequested = false;
            latestLevel = 0.0f;
            audioQueue = new BlockingCollection<float[]>(100);
            running = true;
            workerThread = new Thread(WorkerMain);
            workerThread.IsBackground = true;
            workerThread.Name = "toolrack-transcribe-worker";
            workerThread.Start();
        }
    }

    private void WorkerMain()
    {
        try
        {
            ResetRecognitionSession();
            bool useMicrophone;
            lock (stateLock) useMicrophone = microphoneSession;
            if (useMicrophone) StartWaveInput();

            BlockingCollection<float[]> queue;
            lock (stateLock) queue = audioQueue;
            foreach (float[] samples in queue.GetConsumingEnumerable())
                EnqueueResults(Feed(samples));
            EnqueueResults(Flush());
        }
        catch (Exception ex)
        {
            EnqueueError("Transcription failed: " + ex.Message);
            CompleteAudioQueue();
            StopCaptureAfterFailure();
        }
        finally
        {
            lock (stateLock)
            {
                latestLevel = 0.0f;
                running = false;
                workerThread = null;
            }
        }
    }

    private void StartWaveInput()
    {
        var capture = new WaveInEvent();
        capture.WaveFormat = new WaveFormat(SampleRate, 16, 1);
        capture.BufferMilliseconds = 100;
        capture.NumberOfBuffers = 3;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        captureStoppedEvent.Reset();
        lock (stateLock) waveIn = capture;

        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            lock (stateLock)
            {
                if (Object.ReferenceEquals(waveIn, capture)) waveIn = null;
            }
            capture.Dispose();
            captureStoppedEvent.Set();
            throw;
        }

        bool stopNow;
        lock (stateLock) stopNow = stopRequested;
        if (stopNow) capture.StopRecording();
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        try
        {
            QueuePcm(e.Buffer, e.BytesRecorded, false);
        }
        catch (Exception ex)
        {
            EnqueueError("Microphone input failed: " + ex.Message);
            Stop();
        }
    }

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
    {
        var capture = (WaveInEvent)sender;
        if (e.Exception != null)
            EnqueueError("Microphone recording stopped: " + e.Exception.Message);
        ForceDisposeCapture(capture);
        captureStoppedEvent.Set();
        CompleteAudioQueue();
    }

    private void QueuePcm(byte[] buffer, int bytesRecorded, bool throwOnFull)
    {
        float gainValue;
        BlockingCollection<float[]> queue;
        lock (stateLock)
        {
            gainValue = gain;
            queue = audioQueue;
        }
        float level;
        float[] samples = ConvertPcm16(buffer, bytesRecorded, gainValue, out level);
        lock (stateLock) latestLevel = level;

        bool added = false;
        if (queue != null && !queue.IsAddingCompleted)
        {
            try
            {
                added = queue.TryAdd(samples);
            }
            catch (InvalidOperationException)
            {
                added = false;
            }
        }
        if (!added)
        {
            if (throwOnFull) throw new InvalidOperationException("The audio queue is not accepting samples.");
            EnqueueError("Audio processing fell behind; recording was stopped to avoid missing audio.");
            Stop();
        }
    }

    private void CompleteAudioQueue()
    {
        BlockingCollection<float[]> queue;
        lock (stateLock) queue = audioQueue;
        if (queue == null || queue.IsAddingCompleted) return;
        try
        {
            queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void StopCaptureAfterFailure()
    {
        WaveInEvent capture;
        lock (stateLock) capture = waveIn;
        if (capture == null) return;
        try
        {
            capture.StopRecording();
            if (!captureStoppedEvent.WaitOne(5000))
                EnqueueError("Microphone did not stop within five seconds.");
        }
        catch (Exception ex)
        {
            EnqueueError("Microphone cleanup failed: " + ex.Message);
            ForceDisposeCapture(capture);
            captureStoppedEvent.Set();
        }
    }

    private void ForceDisposeCapture(WaveInEvent capture)
    {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        lock (stateLock)
        {
            if (Object.ReferenceEquals(waveIn, capture)) waveIn = null;
        }
        try
        {
            capture.Dispose();
        }
        catch (Exception ex)
        {
            EnqueueError("Microphone cleanup failed: " + ex.Message);
        }
    }

    private void EnqueueResults(List<string> results)
    {
        for (int i = 0; i < results.Count; i++) textQueue.Enqueue(results[i]);
    }

    private void EnqueueError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message)) errorQueue.Enqueue(message);
    }

    private void ResetRecognitionSession()
    {
        vad.Reset();
        sampleHistory.Clear();
        historyStart = 0;
        var initialSilence = new float[SampleRate / 2];
        AppendHistory(initialSilence);
        vad.AcceptWaveform(initialSilence);
    }

    private void DrainVad(List<string> results)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            try
            {
                string text = Decode(AddPreRoll(segment));
                if (!string.IsNullOrWhiteSpace(text)) results.Add(text);
            }
            finally
            {
                vad.Pop();
            }
        }
        TrimHistory();
    }

    private float[] AddPreRoll(SpeechSegment segment)
    {
        int segmentStart = segment.Start;
        int wantedStart = Math.Max(historyStart, segmentStart - PreRollSamples);
        int preCount = Math.Max(0, segmentStart - wantedStart);
        int historyIndex = wantedStart - historyStart;
        if (historyIndex < 0 || historyIndex + preCount > sampleHistory.Count)
            preCount = 0;

        float[] segmentSamples = segment.Samples;
        var combined = new float[preCount + segmentSamples.Length];
        if (preCount > 0) sampleHistory.CopyTo(historyIndex, combined, 0, preCount);
        Array.Copy(segmentSamples, 0, combined, preCount, segmentSamples.Length);
        return combined;
    }

    private void AppendHistory(float[] samples)
    {
        sampleHistory.AddRange(samples);
    }

    private void TrimHistory()
    {
        if (sampleHistory.Count <= HistoryTrimAtSamples) return;
        int removeCount = sampleHistory.Count - HistoryKeepSamples;
        sampleHistory.RemoveRange(0, removeCount);
        historyStart += removeCount;
    }

    private string Decode(float[] samples)
    {
        var stream = recognizer.CreateStream();
        try
        {
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            return stream.Result.Text;
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static float[] ReadWav(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("WAV path is required.", "path");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("WAV must be a RIFF/WAVE file.");

        int formatTag = -1;
        int channels = -1;
        int sampleRate = -1;
        int bitsPerSample = -1;
        int dataOffset = -1;
        int dataLength = -1;
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, offset, 4);
            int size = BitConverter.ToInt32(bytes, offset + 4);
            if (size < 0 || (long)offset + 8L + size > bytes.Length)
                throw new InvalidDataException("WAV contains an invalid chunk length.");
            int content = offset + 8;
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
                formatTag = BitConverter.ToInt16(bytes, content);
                channels = BitConverter.ToInt16(bytes, content + 2);
                sampleRate = BitConverter.ToInt32(bytes, content + 4);
                bitsPerSample = BitConverter.ToInt16(bytes, content + 14);
            }
            else if (id == "data")
            {
                dataOffset = content;
                dataLength = size;
            }
            offset = content + size + (size & 1);
        }

        if (formatTag != 1 || channels != 1 || sampleRate != SampleRate || bitsPerSample != 16)
            throw new InvalidDataException("WAV must be 16 kHz mono 16-bit PCM.");
        if (dataOffset < 0 || dataLength < 0 || (dataLength & 1) != 0)
            throw new InvalidDataException("WAV data chunk is missing or invalid.");

        var samples = new float[dataLength / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / 32768.0f;
        return samples;
    }

    private static string RequireFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Required model file not found: " + name, path);
        return path;
    }

    private static bool IsValidEncoder(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != EncoderLength) return false;
        return ComputeSha256(path).Equals(EncoderHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }

    private static string GetModelMutexName(string modelDir)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(modelDir.ToUpperInvariant());
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(pathBytes);
            string shortHash = BitConverter.ToString(hash, 0, 8).Replace("-", "");
            return "Local\\toolrack-transcribe-model-" + shortHash;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException("TranscriberEngine");
    }
}
