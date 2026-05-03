using NAudio.Wave;
using Pv;

namespace Aemeath.Speech;

public sealed class WakeWordDetectedEventArgs : EventArgs
{
    public WakeWordDetectedEventArgs(string keywordLabel, DateTimeOffset detectedAt)
    {
        KeywordLabel = keywordLabel;
        DetectedAt = detectedAt;
    }

    public string KeywordLabel { get; }
    public DateTimeOffset DetectedAt { get; }
}

public sealed class WakeWordService : IDisposable
{
    private static readonly TimeSpan DetectionCooldown = TimeSpan.FromSeconds(3);
    private readonly object _sync = new();
    private readonly List<short> _pendingSamples = [];
    private WaveInEvent? _waveIn;
    private Porcupine? _porcupine;
    private EventHandler<WaveInEventArgs>? _dataAvailableHandler;
    private DateTimeOffset _lastDetectedAt = DateTimeOffset.MinValue;

    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public bool Start(string accessKey)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(accessKey))
        {
            LastError = "Picovoice AccessKey 为空";
            return false;
        }

        try
        {
            var modelPath = ResolveAssetPath("porcupine_params_zh.pv");
            var keywordPath = ResolveAssetPath("小爱小爱_zh_windows_v4_0_0.ppn");

            _porcupine = Porcupine.FromKeywordPaths(
                accessKey,
                new List<string> { keywordPath },
                modelPath: modelPath,
                sensitivities: new List<float> { 0.6f });

            _dataAvailableHandler = OnDataAvailable;
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(_porcupine.SampleRate, 16, 1),
                BufferMilliseconds = Math.Max(16, (int)Math.Ceiling(_porcupine.FrameLength * 1000d / _porcupine.SampleRate))
            };
            _waveIn.DataAvailable += _dataAvailableHandler;
            _waveIn.StartRecording();

            LastError = null;
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        WaveInEvent? waveIn;
        Porcupine? porcupine;
        EventHandler<WaveInEventArgs>? dataAvailableHandler;

        lock (_sync)
        {
            IsRunning = false;
            waveIn = _waveIn;
            porcupine = _porcupine;
            dataAvailableHandler = _dataAvailableHandler;
            _waveIn = null;
            _porcupine = null;
            _dataAvailableHandler = null;
            _pendingSamples.Clear();
        }

        if (waveIn is not null)
        {
            if (dataAvailableHandler is not null)
            {
                waveIn.DataAvailable -= dataAvailableHandler;
            }

            try
            {
                waveIn.StopRecording();
            }
            catch
            {
            }

            waveIn.Dispose();
        }

        porcupine?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        List<WakeWordDetectedEventArgs>? detections = null;

        lock (_sync)
        {
            if (!IsRunning || _porcupine is null)
            {
                return;
            }

            AppendPcm16Samples(e.Buffer, e.BytesRecorded, _pendingSamples);

            while (_pendingSamples.Count >= _porcupine.FrameLength)
            {
                var frame = _pendingSamples.GetRange(0, _porcupine.FrameLength).ToArray();
                _pendingSamples.RemoveRange(0, _porcupine.FrameLength);

                var keywordIndex = _porcupine.Process(frame);
                if (keywordIndex < 0)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (now - _lastDetectedAt < DetectionCooldown)
                {
                    continue;
                }

                _lastDetectedAt = now;
                detections ??= [];
                detections.Add(new WakeWordDetectedEventArgs("小爱小爱", now));
            }
        }

        if (detections is null)
        {
            return;
        }

        foreach (var detection in detections)
        {
            WakeWordDetected?.Invoke(this, detection);
        }
    }

    private static void AppendPcm16Samples(byte[] buffer, int bytesRecorded, List<short> samples)
    {
        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            samples.Add(BitConverter.ToInt16(buffer, i));
        }
    }

    private static string ResolveAssetPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "voice", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "voice", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException($"未找到语音唤醒资源文件：{fileName}");
    }

    public void Dispose()
    {
        Stop();
    }
}
