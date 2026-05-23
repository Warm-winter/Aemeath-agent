using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using Avalonia.Threading;

namespace Aemeath.Pet;

public partial class PetViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isFollowing;

    [ObservableProperty]
    private bool _isAnimating;

    [ObservableProperty]
    private DateTime _lastOperateTime = DateTime.Now;

    [ObservableProperty]
    private string _currentGifPath = string.Empty;

    [ObservableProperty]
    private PetState _currentState = PetState.Idle;

    public bool IsDoubleClickPlaying { get; private set; }

    partial void OnIsFollowingChanged(bool value)
    {
        if (value)
        {
            CurrentState = PetState.Follow;
        }
        else
        {
            CurrentState = PetState.Idle;
        }
    }

    [RelayCommand]
    private async Task TriggerDoubleClickAnimation()
    {
        IsDoubleClickPlaying = true;
        CurrentState = PetState.Click;

        await Task.Delay(2000);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsDoubleClickPlaying = false;
            CurrentState = IsFollowing ? PetState.Follow : PetState.Idle;
        });
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        IsFollowing = !IsFollowing;
    }
}

public enum PetState
{
    Idle,
    Follow,
    FollowLeft,
    Click,
    Wave,
    Jump,
    Failed,
    Waiting,
    Running,
    Review
}
