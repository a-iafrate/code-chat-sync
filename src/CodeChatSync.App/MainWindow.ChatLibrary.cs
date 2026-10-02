using CodeChatSync.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodeChatSync.App;

/// <summary>
/// The Chats page: every chat held in the private sync repository, per project, and a
/// read-only view of the conversation in each.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Messages added to the view at a time: a long chat is not one huge layout pass.</summary>
    private const int MessagePageSize = 200;

    private IReadOnlyList<ChatLibraryProject> _chatLibrary = [];
    private bool _chatLibraryLoaded;

    private IReadOnlyList<ChatMessage> _openChatMessages = [];
    private int _shownMessageCount;

    /// <summary>
    /// Bumped whenever the detail view is opened or closed, so a conversation that finishes
    /// loading after the user has moved on is not drawn into the wrong view.
    /// </summary>
    private int _chatViewVersion;

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
            content.Children.Add(CreateChatRow(project, chat));
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

    private Grid CreateChatRow(ChatLibraryProject project, ArchivedChat chat)
    {
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

        // The title and details are one large target that works from the keyboard...
        var open = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 48,
            Padding = new Thickness(8, 6, 8, 6)
        };
        open.Click += async (_, _) => await OpenChatAsync(project, chat);

        // The full identifier and both times, for the one who needs to match a chat up with
        // a folder on disk or with the other PC.
        ToolTipService.SetToolTip(open, DescribeChatInFull(chat));

        // ...and the same action is spelled out as an icon, because nothing about a bare row
        // says it can be opened. The icons sit in a column of their own so that the next
        // action on a chat — deleting it — is added here, beside this one.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(CreateRowActionButton("", "View chat", () => OpenChatAsync(project, chat)));

        actions.Children.Add(CreateRowActionButton("", "Rename chat", () => RenameChatAsync(project, chat)));

        actions.Children.Add(CreateRowActionButton("", "Delete chat from the archive", () => DeleteChatAsync(project, chat)));

        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(open);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    /// <summary>An icon-only button for an action on a row, labelled for the tooltip and for screen readers.</summary>
    private static Button CreateRowActionButton(string glyph, string label, Func<Task> action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 40,
            Height = 40,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await action();
        return button;
    }

    // ---- renaming one chat -----------------------------------------------------------

    private async Task RenameChatAsync(ChatLibraryProject project, ArchivedChat chat)
    {
        var input = new TextBox
        {
            Text = chat.Title ?? string.Empty,
            PlaceholderText = "Chat title",
            MaxLength = ChatTitle.MaximumLength,
            MinWidth = 360
        };
        input.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = "Rename chat",
            Content = input,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (ChatTitle.Normalize(input.Text) is null)
        {
            ChatsInfoBar.Title = "Type a title for the chat";
            ChatsInfoBar.Message = string.Empty;
            ChatsInfoBar.Severity = InfoBarSeverity.Warning;
            ChatsInfoBar.IsOpen = true;
            return;
        }

        try
        {
            var result = await _syncHost.RenameArchivedChatAsync(project.ProviderId, project.Project, chat.Id, input.Text);
            ChatsInfoBar.Title = result.Succeeded ? "Chat renamed" : "Could not rename the chat";
            ChatsInfoBar.Message = result.Message;
            ChatsInfoBar.Severity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ChatsInfoBar.IsOpen = true;
            if (result.Succeeded)
            {
                await RefreshChatLibraryAsync();

                // The refresh closes the notice; say what happened again afterwards.
                ChatsInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ChatsInfoBar.Title = "Could not rename the chat";
            ChatsInfoBar.Message = exception.Message;
            ChatsInfoBar.Severity = InfoBarSeverity.Error;
            ChatsInfoBar.IsOpen = true;
        }
    }

    // ---- deleting one chat -----------------------------------------------------------

    private async Task DeleteChatAsync(ChatLibraryProject project, ArchivedChat chat)
    {
        var name = string.IsNullOrWhiteSpace(chat.Title) ? "this chat" : $"\"{chat.Title}\"";
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = "Delete chat from the archive?",
            Content = $"{name} will be removed from the private sync repository. Copies on PCs that already have it "
                + "are not deleted, and neither is the chat in the tool on this PC. The removal stays in the "
                + "repository's Git history.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await _syncHost.DeleteArchivedChatAsync(project.ProviderId, project.Project, chat.Id);
            if (result.Succeeded)
            {
                await RefreshChatLibraryAsync();
            }

            ChatsInfoBar.Title = result.Succeeded ? "Chat deleted" : "Could not delete the chat";
            ChatsInfoBar.Message = result.Message;
            ChatsInfoBar.Severity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ChatsInfoBar.IsOpen = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ChatsInfoBar.Title = "Could not delete the chat";
            ChatsInfoBar.Message = exception.Message;
            ChatsInfoBar.Severity = InfoBarSeverity.Error;
            ChatsInfoBar.IsOpen = true;
        }
    }

    // ---- reading one chat ------------------------------------------------------------

    private async Task OpenChatAsync(ChatLibraryProject project, ArchivedChat chat)
    {
        var version = ++_chatViewVersion;

        ChatListView.Visibility = Visibility.Collapsed;
        ChatDetailView.Visibility = Visibility.Visible;
        ChatDetailTitleText.Text = string.IsNullOrWhiteSpace(chat.Title) ? "(untitled chat)" : chat.Title;
        ChatDetailCaptionText.Text = $"{project.Name} · {GetProviderName(project.ProviderId)} · {DescribeChat(chat)}";
        ChatMessagesPanel.Children.Clear();
        ShowMoreMessagesButton.Visibility = Visibility.Collapsed;
        ChatDetailInfoBar.IsOpen = false;
        _openChatMessages = [];
        _shownMessageCount = 0;
        SetChatLoading(true);

        try
        {
            var content = await _syncHost.ReadArchivedChatAsync(project.ProviderId, project.Project, chat.Id);
            if (version != _chatViewVersion)
            {
                return;
            }

            if (content is null)
            {
                ShowChatDetailNotice(
                    InfoBarSeverity.Warning,
                    "The conversation of this chat is not in the archive, so there is nothing to show.");
                return;
            }

            _openChatMessages = content.Messages;
            ShowChatDetailNotice(InfoBarSeverity.Informational, DescribeContent(content));
            if (content.Messages.Count == 0)
            {
                ChatMessagesPanel.Children.Add(CreateHintCard("This chat has no text messages to show."));
                return;
            }

            ShowNextMessages();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (version == _chatViewVersion)
            {
                ShowChatDetailNotice(InfoBarSeverity.Error, $"Could not read this chat: {exception.Message}");
            }
        }
        finally
        {
            if (version == _chatViewVersion)
            {
                SetChatLoading(false);
            }
        }
    }

    private void OnBackToChatsClick(object sender, RoutedEventArgs e)
    {
        // Invalidates a read still in flight, and lets go of the messages: a long chat is a
        // lot of text boxes to keep alive for a view nobody is looking at.
        _chatViewVersion++;
        _openChatMessages = [];
        _shownMessageCount = 0;
        ChatMessagesPanel.Children.Clear();
        ChatDetailView.Visibility = Visibility.Collapsed;
        ChatListView.Visibility = Visibility.Visible;
    }

    private void OnShowMoreMessagesClick(object sender, RoutedEventArgs e) => ShowNextMessages();

    private void ShowNextMessages()
    {
        var next = Math.Min(_shownMessageCount + MessagePageSize, _openChatMessages.Count);
        for (var index = _shownMessageCount; index < next; index++)
        {
            ChatMessagesPanel.Children.Add(CreateMessageCard(_openChatMessages[index]));
        }

        _shownMessageCount = next;
        var remaining = _openChatMessages.Count - _shownMessageCount;
        ShowMoreMessagesButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreMessagesButton.Content = $"Show {Math.Min(MessagePageSize, remaining)} more ({remaining:N0} remaining)";
    }

    private Border CreateMessageCard(ChatMessage message)
    {
        var isUser = message.Role == ChatRole.User;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock
        {
            Text = isUser ? "You" : "Assistant",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (message.At is { } at)
        {
            header.Children.Add(new TextBlock
            {
                Text = at.ToLocalTime().ToString("g"),
                Style = (Style)WindowRoot.Resources["CaptionStyle"],
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var body = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(header);
        content.Children.Add(body);

        // The user's own messages sit on a tinted background so a long chat can be scanned
        // for where the questions are.
        return new Border
        {
            Child = content,
            Style = (Style)WindowRoot.Resources[isUser ? "UserMessageStyle" : "AssistantMessageStyle"]
        };
    }

    private void SetChatLoading(bool loading)
    {
        ChatDetailProgress.IsActive = loading;
        ChatDetailProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowChatDetailNotice(InfoBarSeverity severity, string message)
    {
        ChatDetailInfoBar.Severity = severity;
        ChatDetailInfoBar.Message = message;
        ChatDetailInfoBar.IsOpen = true;
    }

    private static string DescribeContent(ArchivedChatContent content)
    {
        var notes = new List<string> { "Only the conversation is shown: tool calls and their output are left out." };
        if (content.OmittedMessages > 0)
        {
            notes.Add($"The first {content.Messages.Count:N0} messages are shown; {content.OmittedMessages:N0} more are not.");
        }

        if (content.ClippedMessages > 0)
        {
            notes.Add($"{Pluralize(content.ClippedMessages, "very long message")} cut short.");
        }

        return string.Join(" ", notes);
    }

    // ---- shared helpers --------------------------------------------------------------

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
