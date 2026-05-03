using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Media.SpeechRecognition;
using Whisper.net;
using Whisper.net.Ggml;

namespace Aemeath.Speech;

public class SpeechService : IDisposable
{
    private enum CaptureEngine
    {
        None,
        WindowsNative,
        Whisper
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName, uint dwMapFlags,
        string lpSrcStr, int cchSrc,
        [Out] char[] lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    private static readonly SemaphoreSlim ModelLock = new(1, 1);
    private static string? _cachedModelPath;
    private readonly string _modelDirectory;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _captureWavPath;
    private bool _isCapturing;
    private CaptureEngine _activeCaptureEngine;
    private SpeechRecognizer? _speechRecognizer;
    private TypedEventHandler<SpeechContinuousRecognitionSession, SpeechContinuousRecognitionResultGeneratedEventArgs>? _resultGeneratedHandler;
    private readonly object _segmentLock = new();
    private readonly List<string> _segments = [];

    public SpeechService(string? subscriptionKey = null, string? region = null)
    {
        _modelDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aemeath", "whisper");
        Directory.CreateDirectory(_modelDirectory);
    }

    public async Task<string?> RecognizeSpeechAsync(CancellationToken cancellationToken = default)
    {
        var nativeResult = await TryRecognizeSpeechWithWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(nativeResult))
        {
            return nativeResult;
        }

        string? wavPath = null;
        try
        {
            wavPath = await RecordOnceAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            {
                return null;
            }
            return await TranscribeWavAsync(wavPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
            {
                try
                {
                    File.Delete(wavPath);
                }
                catch
                {
                }
            }
        }
    }

    public async IAsyncEnumerable<string> RecognizeSpeechContinuousAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await RecognizeSpeechAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    public bool IsConfigured => true;

    public async Task StartCaptureAsync()
    {
        if (_isCapturing)
        {
            return;
        }

        if (await TryStartWindowsCaptureAsync(cancellationToken: default).ConfigureAwait(false))
        {
            _activeCaptureEngine = CaptureEngine.WindowsNative;
            _isCapturing = true;
            return;
        }

        _captureWavPath = Path.Combine(Path.GetTempPath(), $"aemeath-stt-hold-{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 1),
            BufferMilliseconds = 100
        };
        _writer = new WaveFileWriter(_captureWavPath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += (_, e) =>
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _writer?.Flush();
        };
        _waveIn.StartRecording();
        _activeCaptureEngine = CaptureEngine.Whisper;
        _isCapturing = true;
    }

    public async Task<string?> StopCaptureAndRecognizeAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing)
        {
            return null;
        }

        _isCapturing = false;

        if (_activeCaptureEngine == CaptureEngine.WindowsNative)
        {
            _activeCaptureEngine = CaptureEngine.None;
            return await StopWindowsCaptureAndCollectAsync(cancellationToken).ConfigureAwait(false);
        }

        _activeCaptureEngine = CaptureEngine.None;

        try
        {
            _waveIn?.StopRecording();
        }
        catch
        {
        }
        _waveIn?.Dispose();
        _waveIn = null;
        _writer?.Dispose();
        _writer = null;

        if (string.IsNullOrWhiteSpace(_captureWavPath) || !File.Exists(_captureWavPath))
        {
            return null;
        }

        try
        {
            return await TranscribeWavAsync(_captureWavPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(_captureWavPath);
            }
            catch
            {
            }

            _captureWavPath = null;
        }
    }

    private static bool IsWindowsSpeechAvailable()
    {
        return ApiInformation.IsTypePresent("Windows.Media.SpeechRecognition.SpeechRecognizer");
    }

    private async Task<string?> TryRecognizeSpeechWithWindowsAsync(CancellationToken cancellationToken)
    {
        if (!IsWindowsSpeechAvailable())
        {
            return null;
        }

        SpeechRecognizer? recognizer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            recognizer = new SpeechRecognizer();
            var compilation = await recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await recognizer.RecognizeAsync();
            if (result.Status != SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                return null;
            }

            return TraditionalToSimplified(result.Text.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            recognizer?.Dispose();
        }
    }

    private async Task<bool> TryStartWindowsCaptureAsync(CancellationToken cancellationToken)
    {
        if (!IsWindowsSpeechAvailable())
        {
            return false;
        }

        SpeechRecognizer? recognizer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            recognizer = new SpeechRecognizer();
            recognizer.Constraints.Clear();
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));

            var compilation = await recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                recognizer.Dispose();
                return false;
            }

            lock (_segmentLock)
            {
                _segments.Clear();
            }

            _resultGeneratedHandler = (_, args) =>
            {
                if (args.Result.Status != SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(args.Result.Text))
                {
                    return;
                }

                lock (_segmentLock)
                {
                    _segments.Add(args.Result.Text.Trim());
                }
            };

            recognizer.ContinuousRecognitionSession.ResultGenerated += _resultGeneratedHandler;
            await recognizer.ContinuousRecognitionSession.StartAsync();

            _speechRecognizer = recognizer;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (recognizer is not null)
            {
                if (_resultGeneratedHandler is not null)
                {
                    recognizer.ContinuousRecognitionSession.ResultGenerated -= _resultGeneratedHandler;
                }

                recognizer.Dispose();
            }

            _resultGeneratedHandler = null;
            _speechRecognizer = null;
            lock (_segmentLock)
            {
                _segments.Clear();
            }

            return false;
        }
    }

    private async Task<string?> StopWindowsCaptureAndCollectAsync(CancellationToken cancellationToken)
    {
        if (_speechRecognizer is null)
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _speechRecognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
        finally
        {
            if (_resultGeneratedHandler is not null)
            {
                _speechRecognizer.ContinuousRecognitionSession.ResultGenerated -= _resultGeneratedHandler;
            }

            _resultGeneratedHandler = null;
            _speechRecognizer.Dispose();
            _speechRecognizer = null;
        }

        string text;
        lock (_segmentLock)
        {
            text = string.Join(' ', _segments).Trim();
            _segments.Clear();
        }

        return string.IsNullOrWhiteSpace(text) ? null : TraditionalToSimplified(text);
    }

    private async Task<string?> TranscribeWavAsync(string wavPath, CancellationToken cancellationToken)
    {
        var modelPath = await EnsureBaseModelAsync(cancellationToken).ConfigureAwait(false);
        using var whisperFactory = WhisperFactory.FromPath(modelPath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("zh")
            .Build();

        using var fileStream = File.OpenRead(wavPath);
        var sb = new StringBuilder();
        await foreach (var result in processor.ProcessAsync(fileStream))
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                sb.Append(result.Text.Trim()).Append(' ');
            }
        }

        var text = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Convert any Traditional Chinese characters to Simplified Chinese
        text = TraditionalToSimplified(text);
        return text;
    }

    private static string TraditionalToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // First call: get required buffer length
        var length = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, null!, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (length <= 0) return text;

        var buffer = new char[length];
        var result = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, buffer, length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return result > 0 ? new string(buffer, 0, result) : text;
    }

    private async Task<string> EnsureBaseModelAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedModelPath) && File.Exists(_cachedModelPath))
        {
            return _cachedModelPath;
        }

        await ModelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedModelPath) && File.Exists(_cachedModelPath))
            {
                return _cachedModelPath;
            }

            var modelPath = Path.Combine(_modelDirectory, "ggml-base.bin");
            if (!File.Exists(modelPath))
            {
                using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base).ConfigureAwait(false);
                using var fileWriter = File.OpenWrite(modelPath);
                await modelStream.CopyToAsync(fileWriter, cancellationToken).ConfigureAwait(false);
            }

            _cachedModelPath = modelPath;
            return modelPath;
        }
        finally
        {
            ModelLock.Release();
        }
    }

    private static async Task<string> RecordOnceAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"aemeath-stt-{Guid.NewGuid():N}.wav");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 1),
            BufferMilliseconds = 100
        };
        using var writer = new WaveFileWriter(wavPath, waveIn.WaveFormat);

        waveIn.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            writer.Flush();
        };
        waveIn.RecordingStopped += (_, _) => completion.TrySetResult(true);

        waveIn.StartRecording();
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                waveIn.StopRecording();
            }
            catch
            {
            }
        }

        await completion.Task.ConfigureAwait(false);
        return wavPath;
    }

    public void Dispose()
    {
        try
        {
            _waveIn?.StopRecording();
        }
        catch
        {
        }

        _waveIn?.Dispose();
        _writer?.Dispose();
        _waveIn = null;
        _writer = null;
        _isCapturing = false;
        _activeCaptureEngine = CaptureEngine.None;

        if (_speechRecognizer is not null)
        {
            if (_resultGeneratedHandler is not null)
            {
                _speechRecognizer.ContinuousRecognitionSession.ResultGenerated -= _resultGeneratedHandler;
                _resultGeneratedHandler = null;
            }

            _speechRecognizer.Dispose();
            _speechRecognizer = null;
        }

        lock (_segmentLock)
        {
            _segments.Clear();
        }

        if (!string.IsNullOrWhiteSpace(_captureWavPath) && File.Exists(_captureWavPath))
        {
            try
            {
                File.Delete(_captureWavPath);
            }
            catch
            {
            }
        }

        _captureWavPath = null;
    }
}

public static class TaskExtensions
{
    public static async Task<T?> TimeoutAfter<T>(this Task<T> task, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, cts.Token);
        var completed = await Task.WhenAny(task, delayTask);
        if (completed == task)
        {
            cts.Cancel();
            return await task;
        }
        return default;
    }
}
