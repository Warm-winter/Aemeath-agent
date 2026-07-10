using Avalonia.Platform;
using Avalonia.Headless.XUnit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace Aemeath.Desktop.Tests;

public sealed class PetAssetTests
{
    [AvaloniaFact]
    public void IdleGif_AllFramesRetainTransparentCornersAndCompactDimensions()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Aemeath.Pet/Assets/animations/pet/daiji.gif"));
        using var image = Image.Load<Rgba32>(stream);

        Assert.Equal(37, image.Frames.Count);
        Assert.True(image.Width <= 256 && image.Height <= 256);

        foreach (var frame in image.Frames)
        {
            Assert.Equal(0, frame[0, 0].A);
            Assert.Equal(0, frame[frame.Width - 1, 0].A);
            Assert.Equal(0, frame[0, frame.Height - 1].A);
            Assert.Equal(0, frame[frame.Width - 1, frame.Height - 1].A);

            var metadata = frame.Metadata.GetGifMetadata();
            Assert.Equal(4, metadata.FrameDelay);
            Assert.Equal(GifDisposalMethod.RestoreToBackground, metadata.DisposalMethod);
        }
    }
}
