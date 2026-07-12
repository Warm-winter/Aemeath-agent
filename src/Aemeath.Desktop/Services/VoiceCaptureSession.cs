using Aemeath.Speech;

namespace Aemeath.Desktop.Services;

internal interface IVoiceCaptureSession : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<string?> StopAndRecognizeAsync(CancellationToken cancellationToken);
}

internal sealed class SpeechVoiceCaptureSession : IVoiceCaptureSession
{
    private readonly SpeechService _speechService = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _speechService.StartCaptureAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<string?> StopAndRecognizeAsync(CancellationToken cancellationToken)
        => _speechService.StopCaptureAndRecognizeAsync(cancellationToken);

    public void Dispose() => _speechService.Dispose();
}
