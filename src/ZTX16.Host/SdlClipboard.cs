using ZTX16.Platform.Authoring;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>The operating system clipboard through SDL3 (text only).</summary>
internal sealed class SdlClipboard : IClipboard
{
    public string GetText() => SDL_HasClipboardText() ? SDL_GetClipboardText() ?? string.Empty : string.Empty;

    public void SetText(string text) => SDL_SetClipboardText(text);
}
