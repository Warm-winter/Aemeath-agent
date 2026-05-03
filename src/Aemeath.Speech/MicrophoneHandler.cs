namespace Aemeath.Speech;

public class MicrophoneHandler
{
    private bool _isRecording;
    private readonly object _lock = new();

    public bool IsRecording => _isRecording;

    public Task StartRecordingAsync()
    {
        lock (_lock)
        {
            if (_isRecording)
            {
                throw new InvalidOperationException("已在录音中");
            }
            _isRecording = true;
        }

        return Task.CompletedTask;
    }

    public void StopRecording()
    {
        lock (_lock)
        {
            _isRecording = false;
        }
    }

    public async Task<byte[]?> RecordAsync(int durationSeconds = 5)
    {
        await StartRecordingAsync();
        
        try
        {
            await Task.Delay(durationSeconds * 1000);
            return null;
        }
        finally
        {
            StopRecording();
        }
    }
}
