namespace Aemeath.Pet.Effects;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public class ParticleEffect
{
    private readonly Canvas _canvas;
    private readonly List<Particle> _particles;
    private readonly DispatcherTimer _timer;
    private readonly Random _random;
    private readonly Color[] _palette;
    private bool _isRunning;

    public ParticleEffect(Canvas canvas)
    {
        _canvas = canvas;
        _particles = new List<Particle>();
        _random = new Random();
        _palette =
        [
            ParseColor("#EBFFDCDE"),
            ParseColor("#D1FFDCDE"),
            ParseColor("#B8FFDCDE"),
            ParseColor("#D9ADD8E6"),
            ParseColor("#BF87CEFA"),
            ParseColor("#B2B0E0E6"),
            ParseColor("#80FFFFFF")
        ];
        
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        
        _timer.Tick += UpdateParticles;
    }

    public void Start(int particleCount = 30)
    {
        if (_isRunning) return;
        
        _isRunning = true;
        CreateParticles(particleCount);
        _timer.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _timer.Stop();
        ClearParticles();
    }

    private void CreateParticles(int count)
    {
        _particles.Clear();
        _canvas.Children.Clear();

        for (int i = 0; i < count; i++)
        {
            var particle = new Particle
            {
                X = _random.NextDouble() * _canvas.Bounds.Width,
                Y = _random.NextDouble() * _canvas.Bounds.Height,
                VX = (_random.NextDouble() - 0.5) * 0.8,
                VY = (_random.NextDouble() - 0.5) * 0.8,
                Radius = _random.NextDouble() * 3 + 0.5,
                Opacity = _random.NextDouble() * 0.5 + 0.3,
                AlphaDelta = (_random.NextDouble() - 0.5) * 0.02,
                Color = _palette[_random.Next(_palette.Length)]
            };
            _particles.Add(particle);
        }
    }

    private void UpdateParticles(object? sender, EventArgs e)
    {
        if (!_isRunning || _particles.Count == 0)
        {
            return;
        }

        var width = _canvas.Bounds.Width;
        var height = _canvas.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _canvas.Children.Clear();

        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];

            particle.X += particle.VX;
            particle.Y += particle.VY;
            particle.Opacity += particle.AlphaDelta;
            if (particle.Opacity <= 0.1 || particle.Opacity >= 0.8)
            {
                particle.AlphaDelta *= -1;
                particle.Opacity = Math.Clamp(particle.Opacity, 0.1, 0.8);
            }

            if (particle.X < 0 || particle.X > width || particle.Y < 0 || particle.Y > height)
            {
                ResetParticle(particle, width, height);
                continue;
            }
        }

        DrawConnections();
        DrawParticles();
    }

    private void DrawParticles()
    {
        foreach (var particle in _particles)
        {
            var glow = new Ellipse
            {
                Width = particle.Radius * 4,
                Height = particle.Radius * 4,
                Fill = new SolidColorBrush(particle.Color, particle.Opacity * 0.3),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(glow, particle.X - particle.Radius);
            Canvas.SetTop(glow, particle.Y - particle.Radius);

            var core = new Ellipse
            {
                Width = particle.Radius * 2,
                Height = particle.Radius * 2,
                Fill = new SolidColorBrush(particle.Color, particle.Opacity),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(core, particle.X);
            Canvas.SetTop(core, particle.Y);

            _canvas.Children.Add(glow);
            _canvas.Children.Add(core);
        }
    }

    private void DrawConnections()
    {
        for (var i = 0; i < _particles.Count; i++)
        {
            for (var j = i + 1; j < _particles.Count; j++)
            {
                var dx = _particles[i].X - _particles[j].X;
                var dy = _particles[i].Y - _particles[j].Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance >= 120)
                {
                    continue;
                }

                var opacity = (1 - distance / 120d) * 0.2;
                var line = new Line
                {
                    StartPoint = new Point(_particles[i].X, _particles[i].Y),
                    EndPoint = new Point(_particles[j].X, _particles[j].Y),
                    Stroke = new SolidColorBrush(ParseColor("#80FFDCDE"), opacity),
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(line);
            }
        }
    }

    private void ResetParticle(Particle particle, double width, double height)
    {
        particle.X = _random.NextDouble() * width;
        particle.Y = _random.NextDouble() * height;
        particle.Radius = _random.NextDouble() * 3 + 0.5;
        particle.VX = (_random.NextDouble() - 0.5) * 0.8;
        particle.VY = (_random.NextDouble() - 0.5) * 0.8;
        particle.Opacity = _random.NextDouble() * 0.5 + 0.3;
        particle.AlphaDelta = (_random.NextDouble() - 0.5) * 0.02;
        particle.Color = _palette[_random.Next(_palette.Length)];
    }

    private static Color ParseColor(string hex)
    {
        if (!uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return Colors.White;
        }

        if (hex.Length == 9)
        {
            var a = (byte)((value & 0xFF000000) >> 24);
            var r = (byte)((value & 0x00FF0000) >> 16);
            var g = (byte)((value & 0x0000FF00) >> 8);
            var b = (byte)(value & 0x000000FF);
            return Color.FromArgb(a, r, g, b);
        }

        if (hex.Length == 7)
        {
            var r = (byte)((value & 0x00FF0000) >> 16);
            var g = (byte)((value & 0x0000FF00) >> 8);
            var b = (byte)(value & 0x000000FF);
            return Color.FromRgb(r, g, b);
        }

        return Colors.White;
    }

    private void ClearParticles()
    {
        _particles.Clear();
        _canvas.Children.Clear();
    }

    private class Particle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double VX { get; set; }
        public double VY { get; set; }
        public double Radius { get; set; }
        public double Opacity { get; set; }
        public double AlphaDelta { get; set; }
        public Color Color { get; set; }
    }
}
