using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using Windows.Storage;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// Centred sheets of the chat screens (design §4.4, §4.7): rename, delete, the recording pickers
/// (new chat / Add recordings / Add to chat), the Sources sheet and the Markdown export. Built in
/// code like <see cref="HistoryDialogs"/>: small, shared by both chat pages and the recording page.
/// </summary>
internal static class ChatDialogs
{
    internal const string DeleteKeyPrefix = "chat:";

    public static string DeleteKey(string chatId) => DeleteKeyPrefix + chatId;

    /// <summary>Height available for the scrolling list of a dialog (landscape phones are only 360 px tall).</summary>
    internal static double ListMaxHeight(XamlRoot? root, double reserved = 250) =>
        Math.Clamp((root?.Size.Height ?? 700) - reserved, 120, 460);

    // ── Rename ───────────────────────────────────────────────────────────

    /// <summary>The new title, or null when cancelled or left empty.</summary>
    public static async Task<string?> RenameAsync(XamlRoot? xamlRoot, ChatRecord chat)
    {
        var box = new TextBox
        {
            Text = ChatUiFormat.DisplayTitle(chat),
            PlaceholderText = "Chat title",
            MinHeight = 44,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(box, "Chat title");

        var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
        body.Children.Add(box);
        body.Children.Add(new TextBlock
        {
            Text = "✦ Gemma suggests a title after the first answer. Your own title is never replaced.",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Rename chat",
            Content = body,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(box.Text))
            {
                e.Handled = true;
                dialog.Tag = "enter";
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        var result = await HistoryDialogs.ShowSafeAsync(dialog);
        if (result != ContentDialogResult.Primary && !Equals(dialog.Tag, "enter"))
            return null;
        var title = box.Text.Trim();
        return title.Length == 0 || title == chat.Title ? null : title;
    }

    // ── Delete ───────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a chat after the confirmation rule of the recordings: with "Ask before deleting" on, a
    /// dialog and an immediate delete; with it off, hidden now with an 8 s Undo snackbar.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteChatAsync(XamlRoot? xamlRoot, ChatRecord chat)
    {
        var id = chat.Id;
        if (!HistoryServices.HistorySettings.ConfirmDelete)
        {
            HistoryServices.Deletes.Schedule("Chat deleted", [DeleteKey(id)], null, () => DeleteNow(id));
            return DeleteOutcome.Pending;
        }

        var text = new HistoryUiConfirmation("Delete chat?",
            $"“{ChatUiFormat.DisplayTitle(chat)}” and its {HistoryUiFormat.Count(ChatUiFormat.CountMessages(chat), "message")} "
            + "are deleted from this device. The recordings stay. This can't be undone.");
        if (!await HistoryDialogs.ConfirmDeleteAsync(xamlRoot, text, allowDontAsk: true))
            return DeleteOutcome.Cancelled;
        await Task.Run(() => DeleteNow(id));
        return DeleteOutcome.Deleted;
    }

    public static void DeleteNow(string chatId)
    {
        try
        {
            ChatServices.Current?.CloseChat(chatId);
            HistoryServices.Chats.Delete(chatId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[VoiceType.Uno] Chat {chatId} could not be deleted: {ex.Message}");
        }
    }

    public static bool IsDeletePending(string chatId) =>
        HistoryServices.Deletes.Pending.IsRecordingHidden(DeleteKey(chatId));

    // ── Recording pickers ────────────────────────────────────────────────

    /// <summary>
    /// Searchable checklist of recordings. <paramref name="already"/> are shown disabled with
    /// <paramref name="alreadyText"/>. Returns the ticked ids, or null when cancelled.
    /// </summary>
    /// <param name="actionFormat">Primary button: <c>Start chat</c>, <c>Add {0}</c>.</param>
    public static async Task<IReadOnlyList<string>?> PickRecordingsAsync(
        XamlRoot? xamlRoot, string title, string actionFormat, IReadOnlySet<string> already, string alreadyText,
        string? hint = null, IReadOnlyCollection<string>? preselected = null)
    {
        var data = await Task.Run(() =>
        {
            var store = HistoryServices.Recordings;
            var entries = store.LoadAll().Recordings.ToDictionary(e => e.Id, e => e.Manifest, StringComparer.Ordinal);
            var index = HistorySearch.BuildIndex(store, HistoryServices.Chats);
            return (entries, index);
        });
        var picker = new RecordingPickerDialog(xamlRoot, title, actionFormat, already, alreadyText, hint,
            data.entries, data.index, preselected);
        return await picker.RunAsync();
    }

    private sealed class RecordingPickerDialog
    {
        private readonly ContentDialog _dialog;
        private readonly string _actionFormat;
        private readonly IReadOnlySet<string> _already;
        private readonly string _alreadyText;
        private readonly Dictionary<string, RecordingManifest> _manifests;
        private readonly IReadOnlyList<HistorySearchDocument> _index;
        private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
        private readonly StackPanel _list = new() { Spacing = 0 };
        private readonly TextBlock _empty;
        private readonly TextBox _search;

        public RecordingPickerDialog(
            XamlRoot? xamlRoot, string title, string actionFormat, IReadOnlySet<string> already, string alreadyText,
            string? hint, Dictionary<string, RecordingManifest> manifests, IReadOnlyList<HistorySearchDocument> index,
            IReadOnlyCollection<string>? preselected)
        {
            _actionFormat = actionFormat;
            _already = already;
            _alreadyText = alreadyText;
            _manifests = manifests;
            _index = index;
            foreach (var id in preselected ?? [])
                _selected.Add(id);

            _search = new TextBox { PlaceholderText = "Search recordings", MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_search, "Search recordings");
            _search.TextChanged += (_, _) => Rebuild();

            _empty = new TextBlock { Text = "No recordings found", Opacity = 0.7, Margin = new Thickness(4, 12, 4, 12), Visibility = Visibility.Collapsed };
            var scroll = new ScrollViewer
            {
                Content = new StackPanel { Children = { _list, _empty } },
                MaxHeight = ListMaxHeight(xamlRoot),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
            };

            var body = new StackPanel { Spacing = 8, MaxWidth = 520 };
            if (hint is { Length: > 0 })
                body.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(_search);
            body.Children.Add(scroll);

            _dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = body,
                PrimaryButtonText = actionFormat.Replace(" ({0})", "").Replace(" {0}", ""),
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            Rebuild();
            UpdatePrimary();
        }

        public async Task<IReadOnlyList<string>?> RunAsync()
        {
            var result = await HistoryDialogs.ShowSafeAsync(_dialog);
            return result == ContentDialogResult.Primary ? _selected.ToList() : null;
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            var hits = HistorySearch.Search(_index, _search.Text);
            var now = DateTimeOffset.Now;
            foreach (var hit in hits)
            {
                var id = hit.Document.RecordingId;
                if (!_manifests.TryGetValue(id, out var manifest))
                    continue;
                var isAlready = _already.Contains(id);
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = manifest.EffectiveTitle, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock
                {
                    Text = isAlready ? _alreadyText : HistoryUiFormat.RowMeta(manifest, now),
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var box = new CheckBox
                {
                    Content = label,
                    MinHeight = 52,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsChecked = isAlready || _selected.Contains(id),
                    IsEnabled = !isAlready,
                };
                AutomationProperties.SetName(box, manifest.EffectiveTitle + (isAlready ? " · " + _alreadyText : ""));
                box.Checked += (_, _) => { _selected.Add(id); UpdatePrimary(); };
                box.Unchecked += (_, _) => { _selected.Remove(id); UpdatePrimary(); };
                _list.Children.Add(box);
            }
            _empty.Visibility = _list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePrimary()
        {
            _dialog.PrimaryButtonText = _selected.Count == 0
                ? _actionFormat.Replace(" ({0})", "").Replace(" {0}", "")
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, _actionFormat, _selected.Count);
            _dialog.IsPrimaryButtonEnabled = _selected.Count > 0;
        }
    }

    // ── Add to chat… (from a recording) ──────────────────────────────────

    /// <summary>What "Add to chat…" chose.</summary>
    /// <param name="ChatId">The existing chat; null = start a new chat with the recording.</param>
    public sealed record AddToChatChoice(string? ChatId);

    public static async Task<AddToChatChoice?> PickChatAsync(XamlRoot? xamlRoot, string recordingId)
    {
        var chats = await Task.Run(() => HistoryServices.Chats.LoadAll().Chats);
        var now = DateTimeOffset.Now;
        AddToChatChoice? choice = null;
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = "Add to chat", CloseButtonText = "Cancel" };

        var list = new StackPanel { Spacing = 4, MaxWidth = 520 };
        var fresh = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = "✦  New chat with this recording",
        };
        AutomationProperties.SetName(fresh, "New chat with this recording");
        fresh.Click += (_, _) => { choice = new AddToChatChoice(null); dialog.Hide(); };
        list.Children.Add(fresh);

        foreach (var chat in chats.Where(c => !IsDeletePending(c.Id)))
        {
            var includes = chat.Includes(recordingId);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = ChatUiFormat.DisplayTitle(chat), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock
            {
                Text = includes ? "Already includes it" : ChatUiFormat.RowMeta(chat, ChatServices.TitleOf, now),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var button = new Button
            {
                Content = label,
                MinHeight = 52,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = !includes,
            };
            AutomationProperties.SetName(button, ChatUiFormat.DisplayTitle(chat) + (includes ? " · already includes it" : ""));
            var id = chat.Id;
            button.Click += (_, _) => { choice = new AddToChatChoice(id); dialog.Hide(); };
            list.Children.Add(button);
        }

        dialog.Content = new ScrollViewer
        {
            Content = list,
            MaxHeight = ListMaxHeight(xamlRoot, 210),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        await HistoryDialogs.ShowSafeAsync(dialog);
        return choice;
    }

    // ── Sources sheet ────────────────────────────────────────────────────

    public enum SourcesAction
    {
        None,
        Open,
        Add,
    }

    public sealed record SourcesOutcome(SourcesAction Action, string? RecordingId);

    /// <summary>
    /// Sources of a chat: version and time added per recording; tap opens it, × removes it, "Use vN"
    /// moves an outdated one to the current version. Returns what the user asked for when the sheet closes.
    /// </summary>
    public static async Task<SourcesOutcome> ShowSourcesAsync(XamlRoot? xamlRoot, string chatId, IChatService service)
    {
        var outcome = new SourcesOutcome(SourcesAction.None, null);
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = "Sources", CloseButtonText = "Done" };
        var list = new StackPanel { Spacing = 4, MaxWidth = 520 };
        var footnote = new TextBlock
        {
            Text = "Removed recordings are no longer read. Earlier answers keep their citations.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var add = new Button
        {
            Content = "+  Add recording",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
        };
        AutomationProperties.SetName(add, "Add recording");
        add.Click += (_, _) => { outcome = new SourcesOutcome(SourcesAction.Add, null); dialog.Hide(); };

        void Rebuild()
        {
            list.Children.Clear();
            if (HistoryServices.Chats.Get(chatId) is not { } chat)
                return;
            var now = DateTimeOffset.Now;
            foreach (var source in chat.Sources)
            {
                var title = ChatServices.TitleOf(source.RecordingId);
                var current = ChatServices.CurrentVersionOf(source.RecordingId);
                var outdated = current is not null && current != source.VersionId;
                var detail = HistoryUiFormat.Join(source.VersionId + " · added " + ChatUiFormat.WhenText(source.AddedAt, now),
                    outdated ? "outdated, current is " + current : null);

                var row = new Grid { ColumnSpacing = 0, MinHeight = 52 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = title ?? "Deleted recording", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                var open = new Button
                {
                    Content = label,
                    MinHeight = 52,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    IsEnabled = title is not null,
                };
                AutomationProperties.SetName(open, "Open " + (title ?? "recording"));
                var id = source.RecordingId;
                open.Click += (_, _) => { outcome = new SourcesOutcome(SourcesAction.Open, id); dialog.Hide(); };
                row.Children.Add(open);

                if (outdated)
                {
                    var update = new Button { Content = "Use " + current, MinHeight = 44, Padding = new Thickness(10, 0, 10, 0) };
                    AutomationProperties.SetName(update, $"Use version {current} of {title}");
                    Grid.SetColumn(update, 1);
                    update.Click += async (_, _) =>
                    {
                        await RunSafeAsync(xamlRoot, () => service.AddSource(chatId, id));
                        Rebuild();
                    };
                    row.Children.Add(update);
                }

                var remove = new Button
                {
                    Width = 44,
                    Height = 44,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new SymbolIcon(Symbol.Cancel),
                    IsEnabled = chat.Sources.Count > 1,
                };
                AutomationProperties.SetName(remove, "Remove " + (title ?? "recording"));
                ToolTipService.SetToolTip(remove, chat.Sources.Count > 1 ? "Remove from chat" : "A chat needs at least one recording");
                Grid.SetColumn(remove, 2);
                remove.Click += async (_, _) =>
                {
                    await RunSafeAsync(xamlRoot, () => service.RemoveSource(chatId, id));
                    Rebuild();
                };
                row.Children.Add(remove);
                list.Children.Add(row);
            }
            list.Children.Add(add);
            list.Children.Add(footnote);
        }

        Rebuild();
        dialog.Content = new ScrollViewer
        {
            Content = list,
            MaxHeight = ListMaxHeight(xamlRoot, 210),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        await HistoryDialogs.ShowSafeAsync(dialog);
        return outcome;
    }

    private static async Task RunSafeAsync(XamlRoot? xamlRoot, Func<ChatRecord> action)
    {
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(xamlRoot, "Could not change the sources", ex.Message);
        }
    }

    // ── Export (Markdown) ────────────────────────────────────────────────

    /// <summary>Export sheet of a chat: one <c>.md</c> file (title, sources, conversation, citations).</summary>
    public static async Task ExportAsync(XamlRoot? xamlRoot, string chatId)
    {
        var data = await Task.Run(() =>
        {
            var chat = HistoryServices.Chats.Get(chatId);
            if (chat is null)
                return default((string Markdown, string FileName)?);
            var manifests = new Dictionary<string, RecordingManifest>(StringComparer.Ordinal);
            foreach (var source in chat.Sources)
            {
                if (HistoryServices.Recordings.Get(source.RecordingId)?.Manifest is { } manifest)
                    manifests[source.RecordingId] = manifest;
            }
            return ((string Markdown, string FileName)?)(ChatExport.ToMarkdown(chat, manifests),
                ExportFiles.SanitizeFileName(chat.Title.Length > 0 ? chat.Title : "chat") + ".md");
        });
        if (data is not { } export)
            return;

        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "Markdown document (.md)", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Title, sources with their versions, the conversation and the citations (recording and time).",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{export.FileName} · {HistoryUiFormat.Bytes(System.Text.Encoding.UTF8.GetByteCount(export.Markdown))}",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Quick save writes to " + ExportDialog.DocumentsFolder(),
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Export chat",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Save to…",
            SecondaryButtonText = "Quick save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await HistoryDialogs.ShowSafeAsync(dialog);
        if (result == ContentDialogResult.None)
            return;

        var files = new[] { new HistoryExportFile(export.FileName, null, export.Markdown, export.Markdown.Length) };
        try
        {
            string where;
            if (result == ContentDialogResult.Primary)
            {
                StorageFolder? folder = await ExportDialog.PickFolderAsync(xamlRoot);
                if (folder is null)
                    return;
                await ExportDialog.WriteAsync(folder, files);
                where = folder.Path is { Length: > 0 } p ? p : folder.Name;
            }
            else
            {
                var dir = ExportDialog.DocumentsFolder();
                await Task.Run(() => ExportFiles.WriteText(Path.Combine(dir.Length == 0 ? "." : EnsureDirectory(dir), export.FileName), export.Markdown));
                where = dir;
            }
            await HistoryDialogs.ShowMessageAsync(xamlRoot, "Exported", $"{export.FileName} saved to {where}.");
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(xamlRoot, "Export failed", ex.Message);
        }
    }

    private static string EnsureDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
