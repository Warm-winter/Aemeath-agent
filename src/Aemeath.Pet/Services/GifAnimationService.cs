using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using AvaloniaImage = Avalonia.Controls.Image;

namespace Aemeath.Pet.Services;

public sealed class GifAnimationService : IDisposable
{
    private const int MaxDecodedFrameSize = 256;

    private readonly AvaloniaImage _petImage;
    private readonly ConcurrentDictionary<PetState, List<GifFrame>> _stateFrames = new();
    private DispatcherTimer? _animationTimer;
    private PetState _currentState = PetState.Idle;
    private int _currentFrameIndex;
    private bool _isPlaying;

    public PetState CurrentState => _currentState;

    public GifAnimationService(AvaloniaImage petImage)
    {
        _petImage = petImage;
    }

    public async Task LoadGifAsync(string gifPath, PetState state)
    {
        var decoded = await Task.Run(() => DecodeGif(gifPath));
        if (decoded is null || decoded.Count == 0)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var frames = new List<GifFrame>(decoded.Count);
            try
            {
                foreach (var (png, delay) in decoded)
                {
                    using var stream = new MemoryStream(png);
                    frames.Add(new GifFrame(new Bitmap(stream), delay));
                }

                if (frames.Count > 0)
                {
                    _stateFrames[state] = frames;
                }
            }
            catch (Exception ex)
            {
                foreach (var frame in frames)
                {
                    frame.Bitmap.Dispose();
                }
                System.Diagnostics.Debug.WriteLine($"构造 GIF 帧失败 {gifPath}: {ex.Message}");
            }
        });
    }

    public void AliasState(PetState source, PetState target)
    {
        if (_stateFrames.TryGetValue(source, out var frames) && frames.Count > 0)
        {
            _stateFrames[target] = frames;
        }
    }

    public void SetState(PetState state, bool restart = false)
    {
        if (!_stateFrames.TryGetValue(state, out var frames) || frames.Count == 0)
        {
            return;
        }

        var stateChanged = _currentState != state;
        if (!restart && !stateChanged && _petImage.Source is not null)
        {
            return;
        }

        _currentState = state;
        if (restart || stateChanged || _currentFrameIndex >= frames.Count)
        {
            _currentFrameIndex = 0;
        }
        RenderCurrentFrame(frames);
    }

    public void Start()
    {
        if (_isPlaying)
        {
            return;
        }

        _isPlaying = true;
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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
        var disposed = new HashSet<Bitmap>();
        foreach (var frames in _stateFrames.Values)
        {
            foreach (var frame in frames)
            {
                if (disposed.Add(frame.Bitmap))
                {
                    frame.Bitmap.Dispose();
                }
            }
        }
        _stateFrames.Clear();
        _petImage.Source = null;
    }

    private static List<(byte[] Png, TimeSpan Delay)>? DecodeGif(string gifPath)
    {
        try
        {
            using Stream stream = gifPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                ? AssetLoader.Open(new Uri(gifPath))
                : File.OpenRead(gifPath);
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
            var decoded = new List<(byte[], TimeSpan)>(image.Frames.Count);
            for (var index = 0; index < image.Frames.Count; index++)
            {
                using var frameImage = image.Frames.CloneFrame(index);
                if (frameImage.Width > MaxDecodedFrameSize || frameImage.Height > MaxDecodedFrameSize)
                {
                    var scale = Math.Min(
                        MaxDecodedFrameSize / (double)frameImage.Width,
                        MaxDecodedFrameSize / (double)frameImage.Height);
                    frameImage.Mutate(context => context.Resize(
                        Math.Max(1, (int)Math.Round(frameImage.Width * scale)),
                        Math.Max(1, (int)Math.Round(frameImage.Height * scale))));
                }

                using var output = new MemoryStream();
                frameImage.SaveAsPng(output);
                var delayCentiseconds = image.Frames[index].Metadata.GetGifMetadata().FrameDelay;
                var delay = TimeSpan.FromMilliseconds(Math.Max(20, delayCentiseconds <= 0 ? 100 : delayCentiseconds * 10));
                decoded.Add((output.ToArray(), delay));
            }
            return decoded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载 GIF 失败 {gifPath}: {ex.Message}");
            return null;
        }
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
        var frame = frames[Math.Clamp(_currentFrameIndex, 0, frames.Count - 1)];
        _petImage.Source = frame.Bitmap;
        if (_animationTimer is not null)
        {
            _animationTimer.Interval = frame.Delay;
        }
    }

    private sealed record GifFrame(Bitmap Bitmap, TimeSpan Delay);
}
