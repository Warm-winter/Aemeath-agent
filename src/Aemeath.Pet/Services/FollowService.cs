using Avalonia;
using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace Aemeath.Pet.Services;

public class FollowService
{
    private readonly Window _petWindow;
    private readonly double _easeFactor = 0.04;
    private readonly double _stopThreshold = 8.0;

    public FollowService(Window petWindow)
    {
        _petWindow = petWindow;
    }

    public void UpdateFollowPosition()
    {
        try
        {
            if (!GetCursorPos(out var cursor))
            {
                return;
            }
            var mousePos = new PixelPoint(cursor.X, cursor.Y);
            var petPos = _petWindow.Position;
            var targetX = mousePos.X - (int)(_petWindow.Width / 2);
            var targetY = mousePos.Y - (int)(_petWindow.Height / 2);

            var deltaX = targetX - petPos.X;
            var deltaY = targetY - petPos.Y;
            var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

            if (distance > _stopThreshold)
            {
                var newX = petPos.X + deltaX * _easeFactor;
                var newY = petPos.Y + deltaY * _easeFactor;

                var screen = _petWindow.Screens.ScreenFromPoint(mousePos);
                if (screen != null)
                {
                    var minX = screen.Bounds.X;
                    var maxX = screen.Bounds.X + screen.Bounds.Width - _petWindow.Width;
                    var minY = screen.Bounds.Y;
                    var maxY = screen.Bounds.Y + screen.Bounds.Height - _petWindow.Height;
                    newX = Math.Max(minX, Math.Min(newX, maxX));
                    newY = Math.Max(minY, Math.Min(newY, maxY));
                }

                _petWindow.Position = new PixelPoint((int)newX, (int)newY);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"跟随更新失败：{ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
