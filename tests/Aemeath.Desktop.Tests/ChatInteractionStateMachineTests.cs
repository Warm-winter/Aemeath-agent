using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class ChatInteractionStateMachineTests
{
    [Fact]
    public void TransitionTo_Streaming_LocksInteractionAndEnablesStopMode()
    {
        var stateMachine = new ChatInteractionStateMachine();

        stateMachine.TransitionTo(ChatUiState.Streaming);

        Assert.Equal(ChatUiState.Streaming, stateMachine.State);
        Assert.True(stateMachine.IsStreaming);
        Assert.True(stateMachine.IsInteractionLocked);
    }

    [Fact]
    public void CancelActiveOperation_Streaming_TransitionsToCanceledAndUnlocks()
    {
        var stateMachine = new ChatInteractionStateMachine();
        stateMachine.TransitionTo(ChatUiState.Streaming);

        stateMachine.CancelActiveOperation();

        Assert.Equal(ChatUiState.Canceled, stateMachine.State);
        Assert.False(stateMachine.IsStreaming);
        Assert.False(stateMachine.IsInteractionLocked);
    }

    [Theory]
    [InlineData((int)ChatUiState.WaitingConfirmation, true)]
    [InlineData((int)ChatUiState.VoiceListening, true)]
    [InlineData((int)ChatUiState.VoiceRecognizing, true)]
    [InlineData((int)ChatUiState.Failed, false)]
    [InlineData((int)ChatUiState.Idle, false)]
    public void TransitionTo_State_ReportsExpectedLock(int stateValue, bool expectedLocked)
    {
        var stateMachine = new ChatInteractionStateMachine();
        var state = (ChatUiState)stateValue;

        stateMachine.TransitionTo(state);

        Assert.Equal(expectedLocked, stateMachine.IsInteractionLocked);
    }
}
