using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using AvaloniaImage = Avalonia.Controls.Image;

namespace Aemeath.Pet.Services;

public class GifAnimationService
{
    private readonly AvaloniaImage _petImage;
    private readonly ConcurrentDictionary<PetState, List<GifFrame>> _stateFrames;
    private DispatcherTimer? _animationTimer;
    private PetState _currentState = PetState.Idle;
    private int _currentFrameIndex;
    private bool _isPlaying;

    public PetState CurrentState => _currentState;

    public GifAnimationService(AvaloniaImage petImage)
    {
        _petImage = petImage;
        _stateFrames = new ConcurrentDictionary<PetState, List<GifFrame>>();
    }

    public Task LoadGifAsync(string gifPath, PetState state)
    {
        try
        {
            using Stream stream = gifPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                ? AssetLoader.Open(new Uri(gifPath))
                : File.OpenRead(gifPath);

            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
            var frames = new List<GifFrame>();

            for (int i = 0; i < image.Frames.Count; i++)
            {
                using var frameImage = image.Frames.CloneFrame(i);
                using var ms = new MemoryStream();
                frameImage.SaveAsPng(ms);
                ms.Position = 0;

                var delayCentiseconds = image.Frames[i].Metadata.GetGifMetadata().FrameDelay;
                var delay = TimeSpan.FromMilliseconds(Math.Max(20, delayCentiseconds <= 0 ? 100 : delayCentiseconds * 10));
                var bitmap = new Bitmap(ms);
                frames.Add(new GifFrame(bitmap, delay));
            }

            if (frames.Count > 0)
            {
                _stateFrames[state] = frames;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 GIF 失败 {gifPath}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void SetState(PetState state)
    {
        if (!_stateFrames.TryGetValue(state, out var frames) || frames.Count == 0)
        {
            return;
        }

        _currentState = state;
        _currentFrameIndex = 0;
        RenderCurrentFrame(frames);
    }

    public void Start()
    {
        if (_isPlaying)
        {
            return;
        }

        _isPlaying = true;
        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        _animationTimer.Tick += (_, _) => AdvanceFrame();
        _animationTimer.Start();
    }

    public void Stop()
    {
        _isPlaying = false;
        _animationTimer?.Stop();
        _animationTimer = null;
    }

    public void Dispose()
    {
        Stop();
        foreach (var frames in _stateFrames.Values)
        {
            foreach (var frame in frames)
            {
                frame.Bitmap.Dispose();
            }
        }

        _stateFrames.Clear();
    }

    private void AdvanceFrame()
    {
        if (!_stateFrames.TryGetValue(_currentState, out var frames) || frames.Count == 0)
        {
            return;
        }

        _currentFrameIndex = (_currentFrameIndex + 1) % frames.Count;
        RenderCurrentFrame(frames);
    }

    private void RenderCurrentFrame(List<GifFrame> frames)
    {
        if (frames.Count == 0)
        {
            return;
        }

        var frame = frames[Math.Clamp(_currentFrameIndex, 0, frames.Count - 1)];
        _petImage.Source = frame.Bitmap;
        if (_animationTimer is not null)
        {
            _animationTimer.Interval = frame.Delay;
        }
    }

    private sealed record GifFrame(Bitmap Bitmap, TimeSpan Delay);
}
