using CodeChatSync.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodeChatSync.App;

/// <summary>
/// The Chats page: every chat held in the private sync repository, per project, read-only.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<ChatLibraryProject> _chatLibrary = [];
    private bool _chatLibraryLoaded;

    private async void OnRefreshChatsClick(object sender, RoutedEventArgs e) => await RefreshChatLibraryAsync();

    private void OnChatSearchChanged(object sender, TextChangedEventArgs e) => RenderChatLibrary();

    /// <summary>
    /// Loads the library the first time the page is shown; after that the Refresh button
    /// is how it is updated, so switching pages never re-reads the archive.
    /// </summary>
    private void EnsureChatLibraryLoaded()
    {
        if (!_chatLibraryLoaded)
        {
            _ = RefreshChatLibraryAsync();
        }
    }

    private async Task RefreshChatLibraryAsync()
    {
        RefreshChatsButton.IsEnabled = false;
        try
        {
            _chatLibrary = await _syncHost.ListArchivedChatsAsync();
            _chatLibraryLoaded = true;
            ChatsInfoBar.IsOpen = false;
            RenderChatLibrary();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ChatsInfoBar.Title = "Could not list the archived chats";
            ChatsInfoBar.Message = exception.Message;
            ChatsInfoBar.Severity = InfoBarSeverity.Error;
            ChatsInfoBar.IsOpen = true;
        }
        finally
        {
            RefreshChatsButton.IsEnabled = true;
        }
    }

    private void RenderChatLibrary()
    {
        ChatLibraryPanel.Children.Clear();

        var filter = ChatSearchBox.Text.Trim();
        var filtering = filter.Length > 0;
        var total = _chatLibrary.Sum(project => project.Chats.Count);
        var shown = 0;

        foreach (var project in _chatLibrary)
        {
            var chats = filtering
                ? project.Chats.Where(chat => MatchesChatFilter(chat, filter)).ToArray()
                : project.Chats;
            shown += chats.Count;

            // While searching, a project with nothing to show is noise; otherwise every
            // project is listed so the user can see it is being tracked at all.
            if (filtering && chats.Count == 0)
            {
                continue;
            }

            ChatLibraryPanel.Children.Add(CreateChatGroup(project, chats, expanded: filtering));
        }

        if (_chatLibrary.Count == 0)
        {
            ChatLibraryPanel.Children.Add(CreateHintCard("No projects are registered on this PC yet, so there is nothing to list."));
            ChatsSummaryText.Text = string.Empty;
            return;
        }

        if (filtering && shown == 0)
        {
            ChatLibraryPanel.Children.Add(CreateHintCard($"No chat matches \"{filter}\"."));
        }

        ChatsSummaryText.Text = filtering
            ? $"{shown} of {Pluralize(total, "chat")} match"
            : $"{Pluralize(total, "chat")} across {Pluralize(_chatLibrary.Count, "project")}";
    }

    private Expander CreateChatGroup(ChatLibraryProject project, IReadOnlyList<ArchivedChat> chats, bool expanded)
    {
        var header = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(CreateProviderBadge(project.ProviderId, 32));

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = project.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock
        {
            Style = (Style)WindowRoot.Resources["CaptionStyle"],
            Text = $"{GetProviderName(project.ProviderId)} · {Pluralize(chats.Count, "chat")}"
        });
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var content = new StackPanel { Spacing = 2, Padding = new Thickness(48, 0, 0, 0) };
        if (chats.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Nothing archived for this project yet. Sync to archive its chats.",
                Style = (Style)WindowRoot.Resources["CaptionStyle"],
                Margin = new Thickness(0, 8, 0, 8)
            });
        }

        foreach (var chat in chats)
        {
            content.Children.Add(CreateChatRow(chat));
        }

        return new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private Grid CreateChatRow(ArchivedChat chat)
    {
        var row = new Grid { MinHeight = 48, Padding = new Thickness(0, 6, 0, 6) };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var untitled = string.IsNullOrWhiteSpace(chat.Title);
        text.Children.Add(new TextBlock
        {
            Text = untitled ? "(untitled chat)" : chat.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = untitled ? 0.6 : 1,
            FontStyle = untitled ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
        });
        text.Children.Add(new TextBlock
        {
            Style = (Style)WindowRoot.Resources["CaptionStyle"],
            Text = DescribeChat(chat)
        });
        row.Children.Add(text);

        // The full identifier and both times, for the one who needs to match a chat up with
        // a folder on disk or with the other PC.
        ToolTipService.SetToolTip(row, DescribeChatInFull(chat));
        return row;
    }

    private static bool MatchesChatFilter(ArchivedChat chat, string filter) =>
        (chat.Title?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || chat.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string DescribeChat(ArchivedChat chat)
    {
        var shortId = chat.Id[..Math.Min(8, chat.Id.Length)];
        var when = chat.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("g") : "no date";
        return $"{shortId} · {when} · {FormatSize(chat.SizeBytes)}";
    }

    private static string DescribeChatInFull(ArchivedChat chat)
    {
        var started = chat.CreatedAt is { } created ? created.ToLocalTime().ToString("g") : "unknown";
        var updated = chat.UpdatedAt is { } last ? last.ToLocalTime().ToString("g") : "unknown";
        return $"{chat.Id}\nStarted {started}\nLast active {updated}\n{Pluralize(chat.FileCount, "file")}, {FormatSize(chat.SizeBytes)}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:N1} MB"
    };

    private static string Pluralize(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
