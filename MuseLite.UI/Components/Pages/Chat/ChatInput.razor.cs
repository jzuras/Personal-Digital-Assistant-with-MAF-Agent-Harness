using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;

namespace MuseLite.UI.Components.Pages.Chat;

/// <summary>
/// Code-behind for the chat input box.
/// </summary>
public partial class ChatInput
{
    private ElementReference TextArea { get; set; }
    private string? MessageText { get; set; }

    /// <summary>Gets or sets the callback invoked when the user sends a message.</summary>
    [Parameter]
    public EventCallback<ChatMessage> OnSend { get; set; }

    /// <summary>
    /// Gets or sets whether the input is disabled — set while the agent server is unreachable so the user
    /// cannot type into a void. When disabled, the textarea and send button are inert and
    /// <see cref="SendMessageAsync"/> is a no-op.
    /// </summary>
    [Parameter]
    public bool Disabled { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>Focuses the textarea.</summary>
    public ValueTask FocusAsync() => this.TextArea.FocusAsync();

    private async Task SendMessageAsync()
    {
        if (this.Disabled is true)
        {
            return;
        }

        if (this.MessageText is { Length: > 0 } text)
        {
            this.MessageText = null;
            await this.OnSend.InvokeAsync(new ChatMessage(ChatRole.User, text));
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender is true)
        {
            try
            {
                var module = await this.JS.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./Components/Pages/Chat/ChatInput.razor.js");
                await module.InvokeVoidAsync("init", this.TextArea);
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected during render — ignore
            }
        }
    }
}
