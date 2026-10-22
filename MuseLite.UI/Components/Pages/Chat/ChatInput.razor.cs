using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;

namespace MuseLite.UI.Components.Pages.Chat;

/// <summary>
/// Code-behind for the chat input box.
/// </summary>
public partial class ChatInput
{
    private ElementReference textArea;
    private string? messageText;

    /// <summary>Gets or sets the callback invoked when the user sends a message.</summary>
    [Parameter]
    public EventCallback<ChatMessage> OnSend { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>Focuses the textarea.</summary>
    public ValueTask FocusAsync() => this.textArea.FocusAsync();

    private async Task SendMessageAsync()
    {
        if (this.messageText is { Length: > 0 } text)
        {
            this.messageText = null;
            await this.OnSend.InvokeAsync(new ChatMessage(ChatRole.User, text));
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var module = await this.JS.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./Components/Pages/Chat/ChatInput.razor.js");
                await module.InvokeVoidAsync("init", this.textArea);
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected during render — ignore
            }
        }
    }
}
