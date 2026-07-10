using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Aemeath.Pet.Effects;

/// <summary>
/// Single-control particle field. Each frame updates lightweight data and redraws without creating Avalonia child controls.
/// </summary>
public sealed class ParticleField : Control
{
    public static readonly StyledProperty<IBrush> ParticleBrush1Property =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ParticleBrush1), Brushes.White);
    public static readonly StyledProperty<IBrush> ParticleBrush2Property =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ParticleBrush2), Brushes.White);
    public static readonly StyledProperty<IBrush> ParticleBrush3Property =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ParticleBrush3), Brushes.White);
    public static readonly StyledProperty<IBrush> ParticleBrush4Property =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ParticleBrush4), Brushes.White);
    public static readonly StyledProperty<IBrush> ParticleBrush5Property =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ParticleBrush5), Brushes.White);
    public static readonly StyledProperty<IBrush> ConnectionBrushProperty =
        AvaloniaProperty.Register<ParticleField, IBrush>(nameof(ConnectionBrush), Brushes.White);

    private readonly List<Particle> _particles = [];
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private Pen _connectionPen;
    private int _targetCount;
    private bool _isRunning;

    static ParticleField()
    {
        AffectsRender<ParticleField>(
            ParticleBrush1Property,
            ParticleBrush2Property,
            ParticleBrush3Property,
            ParticleBrush4Property,
            ParticleBrush5Property,
            ConnectionBrushProperty);
    }

    public ParticleField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _connectionPen = new Pen(ConnectionBrush, 0.55);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    public IBrush ParticleBrush1
    {
        get => GetValue(ParticleBrush1Property);
        set => SetValue(ParticleBrush1Property, value);
    }

    public IBrush ParticleBrush2
    {
        get => GetValue(ParticleBrush2Property);
        set => SetValue(ParticleBrush2Property, value);
    }

    public IBrush ParticleBrush3
    {
        get => GetValue(ParticleBrush3Property);
        set => SetValue(ParticleBrush3Property, value);
    }

    public IBrush ParticleBrush4
    {
        get => GetValue(ParticleBrush4Property);
        set => SetValue(ParticleBrush4Property, value);
    }

    public IBrush ParticleBrush5
    {
        get => GetValue(ParticleBrush5Property);
        set => SetValue(ParticleBrush5Property, value);
    }

    public IBrush ConnectionBrush
    {
        get => GetValue(ConnectionBrushProperty);
        set => SetValue(ConnectionBrushProperty, value);
    }

    public void Start(int particleCount)
    {
        _targetCount = Math.Clamp(particleCount, 0, 64);
        _isRunning = _targetCount > 0;
        EnsureParticles();
        if (_isRunning)
        {
            _timer.Start();
        }
        InvalidateVisual();
    }

    public void Stop()
    {
        _isRunning = false;
        _timer.Stop();
        _particles.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_particles.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        for (var first = 0; first < _particles.Count; first++)
        {
            for (var second = first + 1; second < _particles.Count; second++)
            {
                var dx = _particles[first].X - _particles[second].X;
                var dy = _particles[first].Y - _particles[second].Y;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= 120 * 120)
                {
                    continue;
                }

                var distance = Math.Sqrt(distanceSquared);
                using (context.PushOpacity((1 - distance / 120d) * 0.18))
                {
                    context.DrawLine(
                        _connectionPen,
                        new Point(_particles[first].X, _particles[first].Y),
                        new Point(_particles[second].X, _particles[second].Y));
                }
            }
        }

        foreach (var particle in _particles)
        {
            var brush = GetParticleBrush(particle.ColorIndex);
            var center = new Point(particle.X, particle.Y);
            using (context.PushOpacity(particle.Opacity * 0.22))
            {
                context.DrawEllipse(brush, null, center, particle.Radius * 2.4, particle.Radius * 2.4);
            }
            using (context.PushOpacity(particle.Opacity))
            {
                context.DrawEllipse(brush, null, center, particle.Radius, particle.Radius);
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ConnectionBrushProperty)
        {
            _connectionPen = new Pen(ConnectionBrush, 0.55);
        }
    }

    private IBrush GetParticleBrush(int index)
    {
        return index switch
        {
            0 => ParticleBrush1,
            1 => ParticleBrush2,
            2 => ParticleBrush3,
            3 => ParticleBrush4,
            _ => ParticleBrush5
        };
    }

    private void Tick()
    {
        if (!_isRunning || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        EnsureParticles();
        foreach (var particle in _particles)
        {
            particle.X += particle.VelocityX;
            particle.Y += particle.VelocityY;
            particle.Opacity += particle.OpacityDelta;
            if (particle.Opacity <= 0.16 || particle.Opacity >= 0.78)
            {
                particle.OpacityDelta *= -1;
                particle.Opacity = Math.Clamp(particle.Opacity, 0.16, 0.78);
            }

            if (particle.X < -8 || particle.X > Bounds.Width + 8 || particle.Y < -8 || particle.Y > Bounds.Height + 8)
            {
                ResetParticle(particle);
            }
        }

        InvalidateVisual();
    }

    private void EnsureParticles()
    {
        if (!_isRunning || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        while (_particles.Count > _targetCount)
        {
            _particles.RemoveAt(_particles.Count - 1);
        }
        while (_particles.Count < _targetCount)
        {
            var particle = new Particle();
            ResetParticle(particle);
            _particles.Add(particle);
        }
    }

    private void ResetParticle(Particle particle)
    {
        particle.X = _random.NextDouble() * Math.Max(1, Bounds.Width);
        particle.Y = _random.NextDouble() * Math.Max(1, Bounds.Height);
        particle.VelocityX = (_random.NextDouble() - 0.5) * 0.75;
        particle.VelocityY = (_random.NextDouble() - 0.5) * 0.75;
        particle.Radius = _random.NextDouble() * 2.4 + 0.7;
        particle.Opacity = _random.NextDouble() * 0.42 + 0.28;
        particle.OpacityDelta = (_random.NextDouble() - 0.5) * 0.018;
        particle.ColorIndex = _random.Next(5);
    }

    private sealed class Particle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double Radius { get; set; }
        public double Opacity { get; set; }
        public double OpacityDelta { get; set; }
        public int ColorIndex { get; set; }
    }
}
