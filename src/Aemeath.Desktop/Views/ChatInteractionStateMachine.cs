namespace Aemeath.Desktop.Views;

internal sealed class ChatInteractionStateMachine
{
    public ChatUiState State { get; private set; } = ChatUiState.Idle;

    public bool IsStreaming => State == ChatUiState.Streaming;

    public bool IsInteractionLocked => State is
        ChatUiState.Streaming or
        ChatUiState.VoiceListening or
        ChatUiState.WaitingConfirmation;

    public void TransitionTo(ChatUiState state)
    {
        State = state;
    }

    public void CancelActiveOperation()
    {
        if (State is ChatUiState.Streaming or ChatUiState.VoiceListening)
        {
            State = ChatUiState.Canceled;
        }
    }
}
