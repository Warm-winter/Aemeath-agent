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
        // GIF 解码（ImageSharp）很吃 CPU，放后台线程；Avalonia Bitmap 必须在 UI 线程创建，
        // 所以后台只解到 PNG 字节，再切回 UI 线程构造 Bitmap。避免启动时卡 UI 数秒。
        return Task.Run(() =>
        {
            List<(byte[] png, TimeSpan delay)> decoded;
            try
            {
                using Stream stream = gifPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                    ? AssetLoader.Open(new Uri(gifPath))
                    : File.OpenRead(gifPath);

                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
                decoded = new List<(byte[], TimeSpan)>(image.Frames.Count);
                for (int i = 0; i < image.Frames.Count; i++)
                {
                    using var frameImage = image.Frames.CloneFrame(i);
                    using var ms = new MemoryStream();
                    frameImage.SaveAsPng(ms);
                    var delayCentiseconds = image.Frames[i].Metadata.GetGifMetadata().FrameDelay;
                    var delay = TimeSpan.FromMilliseconds(Math.Max(20, delayCentiseconds <= 0 ? 100 : delayCentiseconds * 10));
                    decoded.Add((ms.ToArray(), delay));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载 GIF 失败 {gifPath}: {ex.Message}");
                return;
            }

            // 切回 UI 线程构造 Bitmap（Avalonia 要求）
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var frames = new List<GifFrame>(decoded.Count);
                    foreach (var (png, delay) in decoded)
                    {
                        using var ms = new MemoryStream(png);
                        frames.Add(new GifFrame(new Bitmap(ms), delay));
                    }

                    if (frames.Count > 0)
                    {
                        _stateFrames[state] = frames;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"构造 GIF 帧失败 {gifPath}: {ex.Message}");
                }
            });
        });
    }

    public void SetState(PetState state, bool restart = false)
    {
        if (!_stateFrames.TryGetValue(state, out var frames) || frames.Count == 0)
        {
            return;
        }

        var isStateChanged = _currentState != state;
        if (!restart && !isStateChanged && _petImage.Source is not null)
        {
            return;
        }

        _currentState = state;
        if (restart || isStateChanged || _currentFrameIndex >= frames.Count)
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
