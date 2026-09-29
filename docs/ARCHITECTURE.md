# Architecture — CodeChatSync

## Goal

Sync AI chats (Copilot, other tools later) across the user's PCs, keeping
them **out of client repos**. No client data should ever end up in the sync
repo; no sync-tool file should ever end up in a client repo.

## Design principles

- **No symlinks.** The tool copies files, period. Push from local to the
  sync folder, pull from the sync folder to local. No links between a
  client repo and the sync repo.
- **Identification by Git remote, not by path.** The same project can live
  at `C:\Dev\...` on one PC and `D:\Clients\...` on another: it's recognized
  by its normalized Git remote, not by its disk path.
- **Plugin-based providers.** The Core knows nothing about a specific tool.
  Each tool (Visual Studio, later Claude Code, Copilot CLI, VS Code) is a
  provider implementing `IChatProvider`: it says where chats live locally,
  how to map a relative path back to a local path on another PC, and which
  processes indicate the tool is "in use" (for the write lock and for the
  `watch` trigger).
- **Sync only while the tool is closed.** Never write while the provider's
  process (e.g. `devenv.exe`) is running, to avoid reading/writing files
  that are in use or getting corrupted.
- **Backup before overwrite.** Every local restore backs up the existing
  file before replacing it.
- **Storage: the user's own private Git repo.** No third-party service, no
  cloud managed by us. The tool only does commit/push/pull on a repo the
  user owns.

## Technology stack

- **.NET 10** on every project in the solution
  (`<TargetFramework>net10.0</TargetFramework>`, `net10.0-windows` for
  `CodeChatSync.App`). Also pinned via a root `global.json`, so a PC with a
  different SDK installed doesn't drift to a misaligned version.
- **The latest C# version supported by .NET 10**, with `Nullable` and
  `ImplicitUsings` enabled on every project.
- **WinUI 3 on the latest stable Windows App SDK release** at the time of
  implementation (don't pin to an old version "because it works"): check
  the current version before adding the package.
- Every NuGet dependency (`System.CommandLine`, `H.NotifyIcon.WinUI`, the
  chosen Git package, etc.) must be taken at the **latest stable version
  available** at the time it's added, not copied from examples or
  tutorials that might reference older versions.
- **All documentation, code, identifiers (classes, methods, variables), and
  comments must be in English**, regardless of the language used when
  talking to an AI assistant about the project.

## Solution structure

```
CodeChatSync.Core                      # config, provider registry, remote→project mapping,
                                        # date comparison, merge/conflict logic
CodeChatSync.Providers.VisualStudio    # discover/map for Copilot chats in .vs
CodeChatSync.Git                       # commit/push/pull on the private sync repo
CodeChatSync.Cli                       # lightweight CLI for terminal/scripting use:
                                        # add, sync, discover — coexists with
                                        # CodeChatSync.App, doesn't replace it
CodeChatSync.App                       # WinUI 3 app: tray icon, configuration window,
                                        # watch integrated in the same process
                                        # (Windows-only, no more Blazor/Kestrel)
```

Note: `Core`, `Providers.VisualStudio`, and `Git` stay platform-agnostic
.NET libraries. Only `App` is tied to Windows (WinUI 3); if terminal use on
Linux is ever needed (e.g. for a future Claude Code provider), `Cli` remains
available without a GUI.

## Provider contract

```csharp
public interface IChatProvider
{
    string Id { get; }                         // "visualstudio", "claudecode", ...
    IReadOnlyList<string> ProcessNames { get; } // for watch and for the lock: "devenv"
    IEnumerable<ChatLocation> Discover(ProjectInfo project); // where chats live locally
    string MapToLocal(ProjectInfo project, string relativePath); // for restore
}
```

`ProjectInfo` holds the project's identity (normalized Git remote) plus its
current local path. The Core handles copying, date comparison, backup, and
commit/push/pull. The provider only has to say where to look.

## Sync folder layout

```
.codechatsync/
  <provider>/<project-from-remote>/...   # e.g. visualstudio/clientA-erp/
```

## Config

- Shared config (in the sync repo, versioned): `Git remote → project/client
  name` mapping.
- Per-PC local config (not versioned): the local path of each project
  registered on that machine.

## Planned providers (after Visual Studio)

- **Claude Code** — sessions under `~/.claude/projects/`, folders encode the
  absolute path: the provider must rename them on the destination PC.
- **Copilot CLI** — data under `~/.copilot`, per session.
- **VS Code** — chats indexed by workspace hash; with VS Code's native
  GitHub-based sync, a dedicated provider is probably unnecessary.

claude.ai chats stay out of scope: they're already tied to the user's
account.

## Configuration UI

A **WinUI 3** app (`CodeChatSync.App`), Windows-only: a tray icon (via
`H.NotifyIcon.WinUI`, since WinUI has no native tray API) plus a
configuration window opened from the tray. The app stays running in the
background and also integrates the watch logic in the same process: there's
no longer a separate `watch` command to launch or schedule.

Chosen over Blazor Server: cross-platform support (Linux/macOS) is lost,
but in exchange we get a single process for tray + watch + configuration,
auto-start handled natively by Windows, and a more natural MSIX packaging
path if the app is ever distributed via the Microsoft Store (WinUI 3 + MSIX
is Microsoft's intended combination, unlike Blazor+Kestrel).

Minimum functionality of the configuration window:

- Sync repo's Git remote + local folder
- List of registered projects (add/remove), with detected remote, local
  path, last sync time
- Sync now (globally and per project)
- Toggle for automatic sync on Visual Studio close
- Conflict list (same file changed on two PCs) with a "keep local / keep
  remote" choice
- Log of recent syncs

`CodeChatSync.Cli` remains as a separate lightweight tool for anyone who
prefers running `add`/`sync`/`discover` from a terminal or a script,
without going through the UI. It doesn't handle the watch — that only lives
in the WinUI app.

## Chat Library

Like the Prompt Library, but for chats already synced by the tool — no
"deploy" action is needed, since chats already live in
`.codechatsync/<provider>/<project>/...` and at the local path mapped by
the provider (`MapToLocal`).

Functionality:

- List of synced chats, per project
- Content view (**best-effort**: the format Visual Studio uses to store
  chats in `.vs` isn't documented — see Phase 0, `discover` — so how deep
  the parsing can go depends on what's found there)
- Editing the chat's title, i.e. the name shown in Visual Studio's Chat
  History panel, if the format exposes an identifiable field for it
- Deleting a synced chat
- Same write rules as every other feature: never while `devenv.exe` is
  running, backup before overwrite

## Prompt Library

Functionality to view/edit Copilot prompt files (`.prompt.md`) and their
display name in the editor (the `name` front-matter field, distinct from
the file name: if absent, Copilot uses the file name after `/`).

**The technical constraint driving this design:** both Visual Studio and VS
Code only read prompt files from `.github/prompts` **inside the open
repo**. VS Code also supports a global "user" scope outside any repo;
regular Visual Studio currently does not. So a personal prompt, to work in
a client project opened in VS, has to physically live inside that repo's
`.github/prompts` — the opposite of everything else in the tool, which
keeps the user's own material out of client repos.

Design:

- **Personal library**, synced across PCs like chats:
  `.codechatsync/prompts/<slug>.prompt.md` in the sync repo. App UI: list,
  content editor, `name` field editor.
- **"Deploy to project"**: an explicit, strictly manual action (never
  automatic) that copies the chosen prompts into `.github/prompts/` of the
  selected client repo. This is the only place in the tool where it
  deliberately writes inside a client repo, due to the technical constraint
  above.
- By default, after deployment the prompt's path is added to
  `.git/info/exclude` of the client repo, so it stays usable in VS but
  doesn't show up in that repo's git status/commits. Sharing it with the
  client's team is still possible, but it's an explicit user choice, not
  the default behavior.

## How it detects Visual Studio closing

The WinUI app subscribes to WMI process-termination events filtered on
`devenv.exe`. When the last instance closes, it waits a few seconds for
files to be released, then triggers a sync.

## Auto-start

Handled via the Windows/MSIX **StartupTask extension** instead of a
manually created Scheduled Task. Practical benefit: it's cleanly removed by
the system when the app is uninstalled, which a manually created Scheduled
Task doesn't guarantee.

## Distribution (evaluated, not yet decided/implemented)

- **Microsoft Store (MSIX):** feasible with WinUI 3 without overhauling the
  architecture. Open item: `broadFileSystemAccess` needs to be justified
  during certification, since solutions can live anywhere on disk.
- **Winget/Scoop/Chocolatey:** for a "just across my own PCs" tool with no
  publishing, **Scoop** is the lowest-friction option — just a private
  bucket (a Git repo with JSON manifests), no review, no server.
- In every case, no package channel uninstalls things the app created
  outside its own folder on its own: with auto-start via the MSIX
  StartupTask this is no longer an issue (see above); if a manual Scheduled
  Task is ever reintroduced, an explicit cleanup command should be added.

## Reference to similar projects (not used as a dependency, inspiration only)

- [AI Chat Sync](https://github.com/fxwl/AI-Chat-Sync) — same concept
  (syncing AI chats across PCs via the user's own private Git repo), scoped
  to Codex/Windows, Electron+React. Useful as a reference for the security
  model (sync repo kept separate from source repos, project mapping,
  conflict handling without silent overwrites).
