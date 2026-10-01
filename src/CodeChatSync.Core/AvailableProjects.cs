namespace CodeChatSync.Core;

/// <summary>A project known to the sync repository's shared mapping but not registered on this PC.</summary>
public sealed record UnregisteredProject(string Remote, string Name);

/// <summary>
/// Decides which projects already present in the sync repository still need to be
/// registered on this PC, so the user can pick one by name instead of retyping a remote
/// from memory. The UI only renders the result.
/// </summary>
public static class AvailableProjects
{
    /// <summary>
    /// Projects in <paramref name="shared"/>'s remote-to-name mapping — registered from
    /// any PC — that have no entry at all in <paramref name="local"/>, ordered by name.
    /// </summary>
    /// <remarks>
    /// A project already registered here for one provider is left out even when another
    /// provider could still be added for it: each provider has its own dedicated add flow
    /// once a project is known locally, and this list is only for projects this PC has
    /// never seen.
    /// </remarks>
    public static IReadOnlyList<UnregisteredProject> FindUnregistered(LocalConfig local, SharedConfig shared)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(shared);

        var result = new List<UnregisteredProject>();
        foreach (var entry in shared.Projects)
        {
            // A shared entry written by a future version, or edited by hand, might not
            // parse; skip it rather than let one bad row hide every other project.
            if (!ProjectIdentity.TryFromRemote(entry.Remote, out var identity) || identity is null)
            {
                continue;
            }

            if (local.Find(identity) is null)
            {
                result.Add(new UnregisteredProject(entry.Remote, entry.Name));
            }
        }

        result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
