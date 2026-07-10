using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Aemeath.Desktop.Services;
using Aemeath.Pet.Effects;

namespace Aemeath.Desktop.Tests;

public sealed class ParticleFieldTests
{
    [AvaloniaFact]
    public void Start_AnimatedField_DoesNotCreateVisualChildren()
    {
        var field = new ParticleField();
        field.Measure(new Size(640, 480));
        field.Arrange(new Rect(0, 0, 640, 480));

        field.Start(48);

        Assert.Empty(field.GetVisualChildren());
        field.Stop();
    }
    [AvaloniaFact]
    public void ThemeStyle_AttachedField_UsesSharedPalette()
    {
        var field = new ParticleField();
        var window = new Window { Content = field, Width = 320, Height = 240 };
        window.Show();
        try
        {
            var brush = Assert.IsType<SolidColorBrush>(field.ParticleBrush1);
            Assert.Equal(Color.Parse(AemiUi.Halo), brush.Color);
        }
        finally
        {
            field.Stop();
            window.Close();
        }
    }

}
