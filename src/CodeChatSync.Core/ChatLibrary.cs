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

    private static bool IsSafeFolderName(string name) =>
        name.Length > 0
        && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/')
        && !name.Contains('\\');
}
