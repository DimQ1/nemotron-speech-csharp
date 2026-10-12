using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>Open a recording (optionally at a time) from a chat citation or the chat list.</summary>
/// <param name="SeekSeconds">Start of the cited passage; the page seeks the player and highlights the segment.</param>
/// <param name="VersionId">Version the citation was made from, when it still exists.</param>
public sealed record RecordingTarget(string RecordingId, double? SeekSeconds = null, string? VersionId = null);

/// <summary>
/// The chat screens' access to the chat service and navigation. The service is registered in
/// <c>App.xaml.cs</c> (<see cref="IChatService"/>); in DEBUG builds a fake takes over when the real one
/// is absent, so the UI can be run and checked on its own.
/// </summary>
internal static class ChatServices
{
    private static IChatService? s_resolved;

    /// <summary>The chat service, or null when this build has none (the entry points hide then).</summary>
    public static IChatService? Current
    {
        get
        {
            if (s_resolved is not null)
                return s_resolved;
            s_resolved = App.Services.GetService<IChatService>();
#if DEBUG
            s_resolved ??= new FakeChatService(HistoryServices.Chats, HistoryServices.Recordings);
#endif
            return s_resolved;
        }
    }

    public static bool IsSupported => Current is not null;

    /// <summary>Title of a recording, null when it no longer exists.</summary>
    public static string? TitleOf(string recordingId) =>
        HistoryServices.Recordings.Get(recordingId)?.Manifest.EffectiveTitle;

    public static string? CurrentVersionOf(string recordingId) =>
        HistoryServices.Recordings.Get(recordingId)?.Manifest.CurrentVersion;

    /// <summary>The app's root frame (the one the menu pages are navigated in).</summary>
    public static Frame? RootFrame(XamlRoot? root) =>
        (root?.Content as Grid)?.Children.OfType<Frame>().FirstOrDefault();

    /// <summary>Opens a chat full screen on the root frame (also from pages embedded in a detail pane).</summary>
    public static void OpenChat(XamlRoot? root, string chatId) =>
        RootFrame(root)?.Navigate(typeof(ChatPage), chatId);

    /// <summary>Opens the recording page, at the cited time when given.</summary>
    public static void OpenRecording(XamlRoot? root, RecordingTarget target) =>
        RootFrame(root)?.Navigate(typeof(RecordingPage), target);

    /// <summary>
    /// "Ask Gemma" about one recording: creates the chat and opens it. Returns false when the
    /// chat could not be created (message shown).
    /// </summary>
    public static async Task<bool> AskAboutAsync(XamlRoot? root, IReadOnlyList<string> recordingIds)
    {
        if (Current is not { } service || recordingIds.Count == 0)
            return false;
        try
        {
            var chat = await Task.Run(() => service.CreateChat(recordingIds));
            OpenChat(root, chat.Id);
            return true;
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(root, "Could not start the chat", ex.Message);
            return false;
        }
    }
}
