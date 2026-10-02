namespace CodeChatSync.Core;

/// <summary>A chat held in the private sync repository, as the Chat Library shows it.</summary>
/// <param name="ProviderId">The provider that archived it.</param>
/// <param name="Id">Stable session identifier, the same on every PC.</param>
/// <param name="Title">A short human title, or <see langword="null"/> when the chat has none.</param>
/// <param name="CreatedAt">When the conversation started, from the PC that held it.</param>
/// <param name="UpdatedAt">When it was last active, from the PC that held it.</param>
/// <param name="SizeBytes">Total size of the chat's archived files.</param>
/// <param name="FileCount">Number of archived files that make up the chat.</param>
public sealed record ArchivedChat(
    string ProviderId,
    string Id,
    string? Title,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    long SizeBytes,
    int FileCount);

/// <summary>
/// Optional provider capability: describe the chats archived for one project in the sync
/// folder.
/// </summary>
/// <remarks>
/// This reads the <em>archive</em>, never the tool's own live storage, so it needs no
/// "tool must be closed" guard and works on a PC where the tool is not even installed. The
/// times must come from the archived content itself rather than from file timestamps:
/// after a <c>git pull</c> every file carries the moment of the checkout, not the moment
/// the conversation happened.
/// </remarks>
public interface IArchivedChatCatalog : IChatProvider
{
    /// <summary>Lists the chats in <paramref name="projectSyncFolder"/>, in no particular order.</summary>
    IReadOnlyList<ArchivedChat> ListArchivedChats(string projectSyncFolder);
}

/// <summary>Who wrote a message.</summary>
public enum ChatRole
{
    User,
    Assistant
}

/// <summary>One message of an archived conversation, as the Chat Library shows it.</summary>
public sealed record ChatMessage(ChatRole Role, string Text, DateTimeOffset? At);

/// <summary>
/// What the Chat Library can show of an archived conversation, with a note of what it
/// left out.
/// </summary>
/// <param name="Messages">The messages in order, text only.</param>
/// <param name="OmittedMessages">Messages beyond the cap, not included at all.</param>
/// <param name="ClippedMessages">Included messages whose text was cut short.</param>
public sealed record ArchivedChatContent(
    IReadOnlyList<ChatMessage> Messages,
    int OmittedMessages,
    int ClippedMessages)
{
    /// <summary>Whether anything was left out, so the viewer can say the view is partial.</summary>
    public bool IsTruncated => OmittedMessages > 0 || ClippedMessages > 0;
}

/// <summary>
/// Optional provider capability: read an archived conversation, best effort, for display.
/// </summary>
/// <remarks>
/// Shows the conversation, not everything the tool recorded: tool calls and their output
/// are left out. It reads the archive only, so it needs no "tool must be closed" guard.
/// </remarks>
public interface IArchivedChatReader : IChatProvider
{
    /// <summary>
    /// Reads chat <paramref name="chatId"/> from <paramref name="projectSyncFolder"/>, or
    /// returns <see langword="null"/> when its conversation is not in the archive.
    /// </summary>
    /// <param name="projectRoot">
    /// This PC's folder for the project, used to show a path the archive stores in a
    /// portable form as a real one; <see langword="null"/> when it is not known.
    /// </param>
    ArchivedChatContent? ReadArchivedChat(string projectSyncFolder, string chatId, string? projectRoot = null);
}

/// <summary>
/// Optional provider capability: change the title of an archived chat, in the archive only.
/// </summary>
/// <remarks>
/// Edits files in the sync folder, never the tool's live storage, so it needs no "tool must
/// be closed" guard. The tool picks the new title up the next time the chat is restored
/// to a PC. An existing file is backed up before it is rewritten.
/// </remarks>
public interface IArchivedChatRenamer : IChatProvider
{
    /// <summary>
    /// Sets the title of chat <paramref name="chatId"/> in <paramref name="projectSyncFolder"/>
    /// to <paramref name="title"/> (already normalized). Returns <see langword="false"/> when
    /// the chat is not in the archive.
    /// </summary>
    bool RenameArchivedChat(string projectSyncFolder, string chatId, string title);
}

/// <summary>Outcome of <see cref="ChatLibrary.Rename"/>.</summary>
public sealed record ChatRenameResult(bool Succeeded, string Message)
{
    public static ChatRenameResult Done(string message) => new(true, message);

    public static ChatRenameResult Failed(string message) => new(false, message);
}

/// <summary>
/// Collects messages while a provider streams a transcript, enforcing the limits that keep
/// a viewer responsive: transcripts run to tens of megabytes, almost all of it tool output.
/// </summary>
public sealed class ArchivedChatContentBuilder
{
    /// <summary>Most messages kept; further ones are only counted.</summary>
    public const int MaximumMessages = 1000;

    /// <summary>Longest message kept whole; a longer one is cut and says so.</summary>
    public const int MaximumMessageLength = 20_000;

    private readonly List<ChatMessage> _messages = [];
    private int _omitted;
    private int _clipped;

    /// <summary>Adds a message. Blank text is ignored.</summary>
    public void Add(ChatRole role, string? text, DateTimeOffset? at)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_messages.Count >= MaximumMessages)
        {
            _omitted++;
            return;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaximumMessageLength)
        {
            // Never split a surrogate pair: half of one is not a character.
            var length = char.IsHighSurrogate(trimmed[MaximumMessageLength - 1])
                ? MaximumMessageLength - 1
                : MaximumMessageLength;
            _clipped++;
            trimmed = $"{trimmed[..length]}{Environment.NewLine}… ({trimmed.Length - length:N0} more characters not shown)";
        }

        _messages.Add(new ChatMessage(role, trimmed, at));
    }

    public ArchivedChatContent Build() => new(_messages, _omitted, _clipped);
}

/// <summary>The archived chats of one project for one provider.</summary>
public sealed record ChatLibraryProject(
    string ProviderId,
    ProjectIdentity Project,
    string Name,
    IReadOnlyList<ArchivedChat> Chats);

/// <summary>
/// Lists everything archived in the sync folder, grouped by registered project and
/// provider.
/// </summary>
public static class ChatLibrary
{
    /// <summary>
    /// Builds the library from this PC's registered projects. A project appears even when
    /// it has no chats yet, so the user can see it is being tracked.
    /// </summary>
    /// <remarks>
    /// Works from the registration rather than from <see cref="SyncWorkspace.ResolveProjects"/>
    /// on purpose: a project whose folder is missing on this PC cannot be synced, but the
    /// chats already archived for it are still there to look at.
    /// </remarks>
    public static IReadOnlyList<ChatLibraryProject> List(
        LocalConfig config,
        SharedConfig shared,
        string syncRoot,
        IEnumerable<IChatProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRoot);
        ArgumentNullException.ThrowIfNull(providers);

        var catalogs = providers
            .OfType<IArchivedChatCatalog>()
            .ToDictionary(catalog => catalog.Id, StringComparer.OrdinalIgnoreCase);

        var projects = new List<ChatLibraryProject>();
        foreach (var entry in config.Projects)
        {
            // An unparsable remote or an unusable folder name cannot be turned into a path
            // safely; the Projects page already reports both, so they are skipped here.
            if (!ProjectIdentity.TryFromRemote(entry.Remote, out var identity) || identity is null)
            {
                continue;
            }

            var name = shared.Find(identity)?.Name ?? identity.Slug;
            if (!IsSafeFolderName(name))
            {
                continue;
            }

            foreach (var providerId in LocalConfig.GetEnabledProviderIds(entry))
            {
                if (!catalogs.TryGetValue(providerId, out var catalog))
                {
                    continue;
                }

                var folder = Path.Combine(Path.GetFullPath(syncRoot), catalog.Id, name);
                var chats = catalog.ListArchivedChats(folder)
                    .OrderByDescending(chat => chat.UpdatedAt ?? chat.CreatedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(chat => chat.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                projects.Add(new ChatLibraryProject(catalog.Id, identity, name, chats));
            }
        }

        return projects;
    }

    /// <summary>
    /// Reads one archived conversation for display, or <see langword="null"/> when the
    /// project, the provider or the chat is not there to read.
    /// </summary>
    /// <remarks>
    /// Resolves the project folder the same way <see cref="List"/> does, so a chat can only
    /// ever be read from inside its own project's folder in the sync repository.
    /// </remarks>
    public static ArchivedChatContent? Read(
        LocalConfig config,
        SharedConfig shared,
        string syncRoot,
        IEnumerable<IChatProvider> providers,
        string providerId,
        ProjectIdentity project,
        string chatId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRoot);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        var reader = providers
            .OfType<IArchivedChatReader>()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        var name = shared.Find(project)?.Name ?? project.Slug;
        if (reader is null || !IsSafeFolderName(name))
        {
            return null;
        }

        var folder = Path.Combine(Path.GetFullPath(syncRoot), reader.Id, name);
        return reader.ReadArchivedChat(folder, chatId, config.Find(project)?.LocalPath);
    }

    /// <summary>
    /// Renames one archived chat and publishes the change so the other PCs get it.
    /// </summary>
    /// <remarks>
    /// Pulls first so the edit lands on the latest archive, then edits, then publishes. The
    /// project folder is resolved exactly as <see cref="Read"/> does, so only a chat inside
    /// its own project's folder can be touched.
    /// </remarks>
    public static ChatRenameResult Rename(
        SharedConfig shared,
        string syncRoot,
        IEnumerable<IChatProvider> providers,
        ISyncPublisher? publisher,
        string providerId,
        ProjectIdentity project,
        string chatId,
        string? newTitle)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRoot);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        var title = ChatTitle.Normalize(newTitle);
        if (title is null)
        {
            return ChatRenameResult.Failed("Type a title for the chat.");
        }

        var renamer = providers
            .OfType<IArchivedChatRenamer>()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        var name = shared.Find(project)?.Name ?? project.Slug;
        if (renamer is null)
        {
            return ChatRenameResult.Failed("This kind of chat cannot be renamed.");
        }

        if (!IsSafeFolderName(name))
        {
            return ChatRenameResult.Failed("The project folder name is not usable.");
        }

        var prepared = publisher?.PrepareForSync();
        if (prepared is { CanContinue: false })
        {
            return ChatRenameResult.Failed(prepared.Message ?? "The sync folder could not be brought up to date.");
        }

        var folder = Path.Combine(Path.GetFullPath(syncRoot), renamer.Id, name);
        if (!renamer.RenameArchivedChat(folder, chatId, title))
        {
            return ChatRenameResult.Failed("The chat is no longer in the archive.");
        }

        var published = publisher?.PublishChanges(1);
        return published is { CanContinue: false }
            ? ChatRenameResult.Failed($"Renamed locally, but not published: {published.Message}")
            : ChatRenameResult.Done("Renamed.");
    }

    private static bool IsSafeFolderName(string name) =>
        name.Length > 0
        && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/')
        && !name.Contains('\\');
}
