using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Aemeath.Core.Configuration;
using Aemeath.Pet.Services;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Aemeath.Pet;

public partial class PetWindow : Window
{
    private const string AssetRoot = "avares://Aemeath.Pet/Assets/animations/pet";

    private readonly GifAnimationService _animationService;
    private readonly FollowService _followService;
    private readonly PetViewModel _viewModel;
    private readonly Action? _openChatAction;
    private readonly Action? _openConfigAction;
    private readonly Action? _exitAction;
    private readonly SettingsService? _settingsService;
    private bool _reduceMotion;
    private readonly Random _random = new();
    private readonly string[] _tapLines =
    [
        "小爱在这里哦~",
        "需要小爱帮忙吗？",
        "嘿嘿，收到摸摸啦！"
    ];
    private readonly string[] _idleLines =
    [
        "小爱待机中，有事随时叫我呀。",
        "休息一下也很重要哦。",
        "星海终端运行良好，小爱守着呢。"
    ];

    private DispatcherTimer? _followTimer;
    private DispatcherTimer? _bubbleTimer;
    private DispatcherTimer? _idleGreetingTimer;
    private CancellationTokenSource? _temporaryStateCts;
    private PetState? _activityState;
    private bool _isDragging;
    private bool _movedDuringPointerPress;
    private bool _isTemporaryState;
    private PetState _followState = PetState.Follow;
    private Point _dragOffset;
    private PixelPoint _pressCursorPoint;
    private DateTime _lastClickAt = DateTime.MinValue;
    private bool _singleClickPending;

    public PetWindow() : this(null, null, null, null, null)
    {
    }

    public PetWindow(Settings? settings = null, Action? openChatAction = null, Action? openConfigAction = null, SettingsService? settingsService = null, Action? exitAction = null)
    {
        InitializeComponent();

        _openChatAction = openChatAction;
        _openConfigAction = openConfigAction;
        _exitAction = exitAction;
        _settingsService = settingsService;
        _reduceMotion = settings?.ReduceMotion ?? false;

        _viewModel = new PetViewModel();
        DataContext = _viewModel;

        if (settings is not null)
        {
            ApplySize(settings.PetWidth, settings.PetHeight);
            _viewModel.IsFollowing = settings.IsPetFollowing;
            Topmost = settings.EnableAlwaysOnTop;
            Opacity = Math.Clamp(settings.PetOpacity, 0.65, 1.0);
        }
        else
        {
            Topmost = true;
        }

        _animationService = new GifAnimationService(PetImage);
        _followService = new FollowService(this);
        BubblePopup.PlacementTarget = PetGrid;
        BubblePopup.Topmost = Topmost;

        SetupEventHandlers();
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += OnSettingsChanged;
        }
        _ = LoadGifAssetsAsync();
        StartFollowLoop();
        StartBubbleLoop();
        StartIdleGreetingLoop();
        Opened += (_, _) => UpdateRuntimeActivity();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
            {
                UpdateRuntimeActivity();
            }
        };
    }

    public async Task PlayTemporaryStateAsync(PetState state, TimeSpan duration, string? bubble = null)
    {
        var cts = new CancellationTokenSource();
        var oldCts = _temporaryStateCts;
        _temporaryStateCts = cts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        _isTemporaryState = true;
        if (!string.IsNullOrWhiteSpace(bubble))
        {
            ShowBubble(bubble);
        }

        SetAnimationState(state, restart: true);

        try
        {
            await Task.Delay(duration, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_temporaryStateCts != cts)
        {
            return;
        }

        _isTemporaryState = false;
        _temporaryStateCts = null;
        cts.Dispose();
        RestoreBaseState();
    }

    public void SetActivityState(PetState? state, string? bubble = null)
    {
        _activityState = state;
        if (!string.IsNullOrWhiteSpace(bubble))
        {
            ShowBubble(bubble);
        }

        RestoreBaseState();
    }

    private void SetupEventHandlers()
    {
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        ContextRequested += OnContextRequested;
    }

    private async Task LoadGifAssetsAsync()
    {
        await _animationService.LoadGifAsync($"{AssetRoot}/daiji.gif", PetState.Idle);
        await _animationService.LoadGifAsync($"{AssetRoot}/yidong.gif", PetState.Follow);
        await _animationService.LoadGifAsync($"{AssetRoot}/dianji.gif", PetState.Click);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-waving.gif", PetState.Wave);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-jumping.gif", PetState.Jump);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-failed.gif", PetState.Failed);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-waiting.gif", PetState.Waiting);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-running-left.gif", PetState.FollowLeft);
        _animationService.AliasState(PetState.Idle, PetState.Running);
        await _animationService.LoadGifAsync($"{AssetRoot}/aemeath-mini-review.gif", PetState.Review);
        RestoreBaseState();
    }

    private void StartAnimationLoop()
    {
        _animationService.Start();
    }

    private void StartFollowLoop()
    {
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _followTimer.Tick += (_, _) =>
        {
            if (!_viewModel.IsFollowing || _isDragging)
            {
                return;
            }

            UpdateFollowAnimation(_followService.UpdateFollowPosition());
        };
    }

    private void StartBubbleLoop()
    {
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _bubbleTimer.Tick += (_, _) =>
        {
            BubblePopup.IsOpen = false;
            _bubbleTimer?.Stop();
        };
    }

    private void StartIdleGreetingLoop()
    {
        _idleGreetingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleGreetingTimer.Tick += (_, _) =>
        {
            if (_settingsService?.Current.EnablePetIdleGreeting != true ||
                _settingsService.Current.EnablePetBubbles != true ||
                _isDragging ||
                !IsVisible)
            {
                return;
            }

            if ((DateTime.Now - _viewModel.LastOperateTime).TotalSeconds >= 90)
            {
                ShowBubble(PickLine(_idleLines));
                _viewModel.LastOperateTime = DateTime.Now;
            }
        };
    }

    private void UpdateRuntimeActivity()
    {
        var shouldRun = IsVisible && WindowState != WindowState.Minimized;
        if (shouldRun)
        {
            if (_reduceMotion)
            {
                _animationService.Stop();
                _followTimer?.Stop();
            }
            else
            {
                StartAnimationLoop();
                _followTimer?.Start();
            }

            _idleGreetingTimer?.Start();
            return;
        }

        _animationService.Stop();
        _followTimer?.Stop();
        _idleGreetingTimer?.Stop();
        _bubbleTimer?.Stop();
        BubblePopup.IsOpen = false;
    }

    private void OnSettingsChanged()
    {
        if (_settingsService is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySettings(_settingsService.Current));
    }

    private void ApplySettings(Settings settings)
    {
        _reduceMotion = settings.ReduceMotion;
        ApplySize(settings.PetWidth, settings.PetHeight);
        _viewModel.IsFollowing = settings.IsPetFollowing;
        Topmost = settings.EnableAlwaysOnTop;
        BubblePopup.Topmost = Topmost;
        Opacity = Math.Clamp(settings.PetOpacity, 0.65, 1.0);
        if (!settings.EnablePetBubbles)
        {
            _bubbleTimer?.Stop();
            BubblePopup.IsOpen = false;
        }

        UpdateRuntimeActivity();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BubblePopup.IsOpen = false;
        _bubbleTimer?.Stop();
        _isDragging = true;
        _movedDuringPointerPress = false;
        e.Pointer?.Capture(this);

        if (TryGetCursorPos(out var cursor))
        {
            _dragOffset = new Point(cursor.X - Position.X, cursor.Y - Position.Y);
            _pressCursorPoint = cursor;
        }
        else
        {
            _dragOffset = e.GetPosition(this);
            _pressCursorPoint = Position;
        }

        _viewModel.LastOperateTime = DateTime.Now;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || !TryGetCursorPos(out var cursor))
        {
            return;
        }

        if (Math.Abs(cursor.X - _pressCursorPoint.X) > 3 || Math.Abs(cursor.Y - _pressCursorPoint.Y) > 3)
        {
            _movedDuringPointerPress = true;
        }

        var newX = (int)(cursor.X - _dragOffset.X);
        var newY = (int)(cursor.Y - _dragOffset.Y);
        Position = ClampToScreen(new PixelPoint(newX, newY), cursor);
        _viewModel.LastOperateTime = DateTime.Now;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            _isDragging = false;
            e.Pointer?.Capture(null);
            RestoreBaseState();
            return;
        }

        if (!_movedDuringPointerPress)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastClickAt).TotalMilliseconds <= 350)
            {
                _singleClickPending = false;
                _lastClickAt = DateTime.MinValue;
                OnDoubleClickInteraction();
            }
            else
            {
                _lastClickAt = now;
                _ = TriggerSingleClickInteractionDelayedAsync(now);
            }
        }

        _isDragging = false;
        e.Pointer?.Capture(null);
        SnapToEdgeIfNeeded();
        RestoreBaseState();
        _viewModel.LastOperateTime = DateTime.Now;
    }

    private async Task TriggerSingleClickInteractionDelayedAsync(DateTime clickAt)
    {
        _singleClickPending = true;
        await Task.Delay(360);
        if (!_singleClickPending || _lastClickAt != clickAt)
        {
            return;
        }

        await TriggerSingleClickInteractionAsync();
    }

    private async Task TriggerSingleClickInteractionAsync()
    {
        try
        {
            _singleClickPending = false;
            await PlayTemporaryStateAsync(PetState.Click, TimeSpan.FromMilliseconds(700), PickLine(_tapLines));
            _viewModel.LastOperateTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"单击互动触发失败：{ex.Message}");
        }
    }

    private void OnDoubleClickInteraction()
    {
        ShowBubble("小爱打开通讯终端啦。");
        _openChatAction?.Invoke();
        _viewModel.LastOperateTime = DateTime.Now;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var menu = BuildContextMenu();
        menu.Open(this);
        e.Handled = true;
    }

    internal ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var chatItem = new MenuItem { Header = "打开对话" };
        chatItem.Click += (_, _) => _openChatAction?.Invoke();

        var interactItem = new MenuItem { Header = "摸摸小爱" };
        interactItem.Click += async (_, _) => await TriggerSingleClickInteractionAsync();

        var greetingItem = new MenuItem { Header = "随机问候" };
        greetingItem.Click += async (_, _) => await PlayTemporaryStateAsync(
            PetState.Wave,
            TimeSpan.FromSeconds(1),
            PickLine(_idleLines.Concat(_tapLines).ToArray()));

        var configItem = new MenuItem { Header = "打开设置" };
        configItem.Click += (_, _) => _openConfigAction?.Invoke();

        var windowMenu = new MenuItem { Header = "窗口行为" };
        var dockItem = new MenuItem { Header = "回到屏幕边缘" };
        dockItem.Click += (_, _) => DockToNearestEdge(showBubble: true);
        var followItem = new MenuItem
        {
            Header = "跟随鼠标",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _viewModel.IsFollowing
        };
        followItem.Click += (_, _) => ToggleFollow(followItem);
        var topmostItem = new MenuItem
        {
            Header = "窗口置顶",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Topmost
        };
        topmostItem.Click += (_, _) => ToggleTopmost(topmostItem);
        var edgeSnapItem = BuildToggleMenuItem("边缘吸附", _settingsService?.Current.EnablePetEdgeSnap ?? true, value =>
        {
            if (_settingsService is null)
            {
                return;
            }
            _settingsService.Current.EnablePetEdgeSnap = value;
            _settingsService.Save();
        });
        windowMenu.Items.Add(dockItem);
        windowMenu.Items.Add(followItem);
        windowMenu.Items.Add(topmostItem);
        windowMenu.Items.Add(edgeSnapItem);

        var appearanceMenu = new MenuItem { Header = "外观" };
        var bubbleItem = BuildToggleMenuItem("气泡台词", _settingsService?.Current.EnablePetBubbles ?? true, value =>
        {
            if (_settingsService is null)
            {
                return;
            }
            _settingsService.Current.EnablePetBubbles = value;
            if (!value)
            {
                BubblePopup.IsOpen = false;
            }
            _settingsService.Save();
        });
        var idleGreetingItem = BuildToggleMenuItem("闲置问候", _settingsService?.Current.EnablePetIdleGreeting ?? true, value =>
        {
            if (_settingsService is null)
            {
                return;
            }
            _settingsService.Current.EnablePetIdleGreeting = value;
            _settingsService.Save();
        });
        var sizeMenu = new MenuItem { Header = "大小" };
        sizeMenu.Items.Add(BuildSizeMenuItem("小巧", "small", 96));
        sizeMenu.Items.Add(BuildSizeMenuItem("标准", "normal", 125));
        sizeMenu.Items.Add(BuildSizeMenuItem("醒目", "large", 160));
        var opacityMenu = new MenuItem { Header = "透明度" };
        opacityMenu.Items.Add(BuildOpacityMenuItem("70%", 0.70));
        opacityMenu.Items.Add(BuildOpacityMenuItem("85%", 0.85));
        opacityMenu.Items.Add(BuildOpacityMenuItem("100%", 1.0));
        appearanceMenu.Items.Add(bubbleItem);
        appearanceMenu.Items.Add(idleGreetingItem);
        appearanceMenu.Items.Add(sizeMenu);
        appearanceMenu.Items.Add(opacityMenu);

        var trayItem = new MenuItem { Header = "收纳到系统托盘" };
        trayItem.Click += (_, _) => Hide();

        var exitItem = new MenuItem { Header = "退出爱弥斯助手" };
        exitItem.Click += (_, _) =>
        {
            if (_exitAction is not null)
            {
                _exitAction.Invoke();
                return;
            }
            Close();
        };

        menu.Items.Add(chatItem);
        menu.Items.Add(interactItem);
        menu.Items.Add(greetingItem);
        menu.Items.Add(configItem);
        menu.Items.Add(windowMenu);
        menu.Items.Add(appearanceMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(trayItem);
        menu.Items.Add(exitItem);
        return menu;
    }

    private MenuItem BuildToggleMenuItem(string header, bool isChecked, Action<bool> onChanged)
    {
        var current = isChecked;
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = current
        };
        item.Click += (_, _) =>
        {
            current = !current;
            item.IsChecked = current;
            onChanged(current);
        };
        return item;
    }

    private MenuItem BuildSizeMenuItem(string header, string preset, int size)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            ApplySize(size, size);
            if (_settingsService is not null)
            {
                _settingsService.Current.PetSizePreset = preset;
                _settingsService.Current.PetWidth = size;
                _settingsService.Current.PetHeight = size;
                _settingsService.Save();
            }

            ShowBubble($"小爱变成{header}尺寸啦。");
        };
        return item;
    }

    private MenuItem BuildOpacityMenuItem(string header, double opacity)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            Opacity = opacity;
            if (_settingsService is not null)
            {
                _settingsService.Current.PetOpacity = opacity;
                _settingsService.Save();
            }

            ShowBubble($"透明度调整到 {header}。");
        };
        return item;
    }

    private void ToggleFollow(MenuItem followItem)
    {
        _viewModel.IsFollowing = !_viewModel.IsFollowing;
        followItem.IsChecked = _viewModel.IsFollowing;
        if (_settingsService is not null)
        {
            _settingsService.Current.IsPetFollowing = _viewModel.IsFollowing;
            _settingsService.Save();
        }

        RestoreBaseState();
        ShowBubble(_viewModel.IsFollowing ? "小爱跟上你啦。" : "小爱在这里等你。");
    }

    private void ToggleTopmost(MenuItem topmostItem)
    {
        Topmost = !Topmost;
        BubblePopup.Topmost = Topmost;
        topmostItem.IsChecked = Topmost;
        if (_settingsService is not null)
        {
            _settingsService.Current.EnableAlwaysOnTop = Topmost;
            _settingsService.Save();
        }
    }

    private void ShowBubble(string text)
    {
        if (_settingsService?.Current.EnablePetBubbles == false || string.IsNullOrWhiteSpace(text) || !IsVisible)
        {
            return;
        }

        BubbleText.Text = text;
        UpdateBubblePlacement();
        BubblePopup.Topmost = Topmost;
        BubblePopup.IsOpen = true;
        _bubbleTimer?.Stop();
        _bubbleTimer?.Start();
    }

    private void UpdateBubblePlacement()
    {
        var screen = Screens.ScreenFromPoint(Position);
        if (screen is null)
        {
            BubblePopup.Placement = PlacementMode.Top;
            BubblePopup.VerticalOffset = -8;
            return;
        }

        var spaceAbove = Position.Y - screen.Bounds.Y;
        var spaceBelow = screen.Bounds.Bottom - (Position.Y + Height);
        var placeAbove = spaceAbove >= 110 || spaceAbove >= spaceBelow;
        BubblePopup.Placement = placeAbove ? PlacementMode.Top : PlacementMode.Bottom;
        BubblePopup.VerticalOffset = placeAbove ? -8 : 8;
    }

    private string PickLine(string[] lines)
    {
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        return lines[_random.Next(lines.Length)];
    }

    private void SnapToEdgeIfNeeded()
    {
        if (_settingsService?.Current.EnablePetEdgeSnap != true)
        {
            return;
        }

        var screen = Screens.ScreenFromPoint(Position);
        if (screen is null)
        {
            return;
        }

        var left = Math.Abs(Position.X - screen.Bounds.X);
        var right = Math.Abs(screen.Bounds.Right - (Position.X + Width));
        var top = Math.Abs(Position.Y - screen.Bounds.Y);
        var bottom = Math.Abs(screen.Bounds.Bottom - (Position.Y + Height));
        if (new[] { left, right, top, bottom }.Min() <= 34)
        {
            DockToNearestEdge(showBubble: false);
        }
    }

    private void DockToNearestEdge(bool showBubble)
    {
        var screen = Screens.ScreenFromPoint(Position);
        if (screen is null)
        {
            return;
        }

        var left = Math.Abs(Position.X - screen.Bounds.X);
        var right = Math.Abs(screen.Bounds.Right - (Position.X + Width));
        var top = Math.Abs(Position.Y - screen.Bounds.Y);
        var bottom = Math.Abs(screen.Bounds.Bottom - (Position.Y + Height));
        var min = new[] { left, right, top, bottom }.Min();
        var margin = 8;
        var x = Position.X;
        var y = Position.Y;
        if (min == left)
        {
            x = screen.Bounds.X + margin;
        }
        else if (min == right)
        {
            x = screen.Bounds.Right - (int)Width - margin;
        }
        else if (min == top)
        {
            y = screen.Bounds.Y + margin;
        }
        else
        {
            y = screen.Bounds.Bottom - (int)Height - margin;
        }

        Position = ClampToScreen(new PixelPoint(x, y), new PixelPoint(x, y));
        if (showBubble)
        {
            _ = PlayTemporaryStateAsync(PetState.Jump, TimeSpan.FromSeconds(1), "小爱贴边站好啦。");
        }
    }

    private PixelPoint ClampToScreen(PixelPoint target, PixelPoint reference)
    {
        var screen = Screens.ScreenFromPoint(reference);
        if (screen is null)
        {
            return target;
        }

        var minX = screen.Bounds.X;
        var maxX = screen.Bounds.X + screen.Bounds.Width - (int)Width;
        var minY = screen.Bounds.Y;
        var maxY = screen.Bounds.Y + screen.Bounds.Height - (int)Height;
        return new PixelPoint(
            Math.Max(minX, Math.Min(target.X, maxX)),
            Math.Max(minY, Math.Min(target.Y, maxY)));
    }

    private void ApplySize(int width, int height)
    {
        Width = Math.Clamp(width, 80, 220);
        Height = Math.Clamp(height, 80, 220);
    }

    private void UpdateFollowAnimation(double deltaX)
    {
        if (Math.Abs(deltaX) < 0.5)
        {
            return;
        }

        _followState = deltaX < 0 ? PetState.FollowLeft : PetState.Follow;
        if (!_isTemporaryState && _activityState is null && _viewModel.IsFollowing)
        {
            SetAnimationState(_followState);
        }
    }

    private void RestoreBaseState()
    {
        if (_isTemporaryState)
        {
            return;
        }

        if (_activityState is PetState activityState)
        {
            SetAnimationState(activityState);
            return;
        }

        SetAnimationState(_viewModel.IsFollowing ? _followState : PetState.Idle);
    }

    private void SetAnimationState(PetState state, bool restart = false)
    {
        _animationService.SetState(state, restart);
        _viewModel.CurrentState = state;
    }

    protected override void OnClosed(EventArgs e)
    {
        _temporaryStateCts?.Cancel();
        _temporaryStateCts?.Dispose();
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }
        _animationService.Dispose();
        _followTimer?.Stop();
        _bubbleTimer?.Stop();
        _idleGreetingTimer?.Stop();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    private static bool TryGetCursorPos(out PixelPoint point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new PixelPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }
}




