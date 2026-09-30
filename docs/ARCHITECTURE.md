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
  that are in use or getting corrupted. The only exception is an explicit,
  per-PC opt-out (`skipRunningCheckProviderIds` in the local config,
  exposed for Visual Studio and Claude Code under *Settings > Advanced*):
  `ProcessGuard.ForSync` wraps the real guard so manual syncs and discovery
  ignore that provider's processes. The provider-close watcher keeps using
  the real guard, so automatic sync still waits for every tool to close;
  backups before overwrite stay active.
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
CodeChatSync.Providers.VisualStudio    # discover/map for Visual Studio Copilot chats
CodeChatSync.Providers.Claude          # Claude Code transcript discovery and mapping
CodeChatSync.Git
CodeChatSync.Cli                       # lightweight CLI for terminal/scripting use:
                                        # add, sync, discover — coexists with
                                        # CodeChatSync.App, doesn't replace it
CodeChatSync.App                       # WinUI 3 app: tray icon, configuration window,
                                        # watch integrated in the same process
                                        # (Windows-only, no more Blazor/Kestrel)
```

Note: `Core`, both provider libraries, and `Git` stay platform-agnostic
.NET libraries. Only `App` is tied to Windows (WinUI 3); `Cli` remains
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

`IChatSessionProvider` optionally exposes a stable per-session ID for local
restore choices. `IChatRestoreValidator` optionally refuses an archived file
before any local overwrite; Claude uses it to reject transcripts whose `cwd`
does not match this PC's registered project path.

`ProjectInfo` holds the project's identity (normalized Git remote) plus its
current local path. The Core handles copying, date comparison, backup, and
commit/push/pull. The provider only has to say where to look.

## Sync folder layout

```
.codechatsync/
  codechatsync.json                      # shared Git remote → project name mapping
  .gitignore                             # excludes .backups/
  .backups/                              # local safety copies, never committed
  <provider>/<project-from-remote>/...   # e.g. visualstudio/clientA-erp/
```

## Visual Studio chat storage (verified, undocumented)

Copilot chats are **not** stored in the solution's `.vs` folder — that only
holds `CopilotIndices/<version>/SemanticSymbols.db*`, a semantic symbol index.
They live per user, per session under:

```
%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\
  session-store.db (+ -shm/-wal)          # machine-wide index of ALL repositories
  session-state\<session-id>\
    workspace.yaml                        # descriptor: id, cwd, repository, git_root, branch, name
    events.jsonl                          # append-only transcript (source of truth)
    checkpoints\, files\, research\       # session artifacts
    session.db                            # agent scratch DB (todos, inbox)
    inuse.<pid>.lock                      # present only while the session is open
```

What gets synced, and why:

- **Synced:** `workspace.yaml`, `events.jsonl`, and the session's artifact
  folders. The transcript is entirely in `events.jsonl`.
- **`session.db` — excluded.** Inspection of the local store showed it holds
  only the agent's `todos`/`todo_deps`/`inbox_entries` scratch tables. It is
  present in fewer than half of the sessions, is schema-only (empty) in almost
  all of those, and the session with the largest transcript had no such file at
  all. It carries no chat content.
- **`session-store.db` — never synced.** It is machine-wide and indexes the
  sessions of *every* repository on the PC, so copying it into the sync repo
  would put unrelated clients' data there, violating the separation rule. A
  session folder is self-contained without it: many recent, content-rich
  sessions exist on disk with no row in this database. If a restored session
  ever needs to be registered there, the tool must insert only that session's
  rows into the local database, never copy the file.
- **`*.lock`, `*-shm`, `*-wal` — excluded.** Per-PC or live-session runtime
  state.

The format is undocumented: all of this is best-effort and confined to
`CodeChatSync.Providers.VisualStudio`.

## Config

- Shared config (in the sync repo, versioned): `Git remote → project/client
  name` mapping, stored as `codechatsync.json` at the sync folder root. It
  holds no local paths, so it stays valid on every PC.
- Per-PC local config (not versioned):
  `%APPDATA%\CodeChatSync\local-config.json`, holding the sync folder, the
  local path and enabled providers of each registered project. Missing
  `providerIds` in legacy configurations means Visual Studio only. A single
  Git remote can enable Visual Studio and Claude independently.
- Per-PC sync baselines (not versioned):
  `%LOCALAPPDATA%\CodeChatSync\state\<provider>\<project>.json`, recording the
  content hash of each file at the last successful sync. These are what tell a
  one-sided change from a real conflict, so they are machine-specific and must
  never end up in the sync repo.
- `CODECHATSYNC_HOME` overrides both local locations, which keeps tests and
  portable installs away from the real user configuration.
- Optional per-PC `restoreSelections` entries are keyed by provider and
  normalized project remote. A missing entry restores all sessions (including
  future sessions); an explicit list restores only the selected session IDs.
  An empty list restores none. This setting is never added to the shared
  config, so two PCs can restore different subsets of the same Git clone.
  It controls only remote-to-provider copies, not local-to-repo backups. An
  unselected chat already present locally is **not deleted**.

## CLI commands

```
codechatsync discover [path] [--all] [--allow-running-provider]
codechatsync config show
codechatsync config set-sync-root <path> [--init]
codechatsync add [path] [--name <name>] [--remote <url>]
codechatsync list
codechatsync sync [--project <name>] [--dry-run] [--no-git]
```

`add` identifies the project by reading the Git remote from the repository's
own `.git/config`, so no Git library or `git` executable is required just to
register a project. Exit codes: `0` success, `1` failure, `2` bad usage or
configuration, `3` sync completed with conflicts, `4` the sync repository
could not be pulled so nothing was changed locally.

## Git on the sync repository

`CodeChatSync.Git` shells out to the installed `git` command line rather than
taking a Git library dependency. That reuses the user's existing credential
helper, SSH keys and proxy configuration, so the tool never handles or stores
a credential. `GIT_TERMINAL_PROMPT=0` prevents an interactive prompt from
hanging an unattended sync.

A `sync` run is pull → copy → commit → push:

- **Pull is `--ff-only`.** Chat files are copied wholesale, so an automatic
  merge or rebase could silently combine two versions of the same transcript.
  A divergence stops the run and leaves both sides untouched for the user to
  resolve.
- **No remote, or a branch never pushed**, is a normal first run, not a
  failure: the sync continues and the commit simply stays local.
- **Missing Git, or a sync folder that is not a repository**, degrades to
  plain file copying with an explanation, so chats still reach the sync folder.
  `--no-git` forces that mode.
- **`config set-sync-root --init`** creates the repository and writes a
  `.gitignore` containing `.backups/`, keeping the local backups taken before
  each overwrite out of the sync repo.
- Only the sync repository is ever touched. Client repositories are never
  committed to, pulled, or pushed.

## Claude Code provider and planned providers

- **Claude Code (initial implementation)** — sessions under
  `~/.claude/projects/` or `$CLAUDE_CONFIG_DIR/projects`. Candidate working
  directories come from transcript `cwd` metadata, not decoding Claude's
  undocumented, lossy folder naming. Only projects at a Git repository root
  with a remote can be registered in the app. Only top-level session `.jsonl`
  files are archived; memory, session artifacts, and subfolder sessions are
  not included. An archived transcript's `cwd` must match the destination
  PC's project path before restore; otherwise it is skipped explicitly.
  Cross-PC path remapping and restored-session visibility are unverified.
  The runtime guard currently recognizes process name `claude`, not every
  possible host such as `node`; close Claude completely before syncing.
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
- Per-PC window theme: system, light, or dark
- Conflict list (same file changed on two PCs) with a "keep local / keep
  remote" choice
- Log of recent syncs

The configuration window lets the user choose an existing local sync folder
or explicitly initialize a new Git repository, and set or update its `origin`
URL. A blank URL leaves the existing remote untouched. The folder cannot be
inside a registered project or another Git repository: that would mix client
files into the private sync repository. Git changes complete before the per-PC
folder setting is saved; saving during an active sync is refused. The app
shows failures in the window instead of treating them as success. No Git
credentials are stored in the remote URL; the user's existing credential
helper remains responsible for authentication. `themePreference` lives only in
the per-PC local configuration and defaults to `System` for existing installs.
Choosing it saves and updates the WinUI window immediately, including the
custom title bar caption buttons; `System` tracks the Windows appearance. The
native tray menu continues to use the Windows appearance.

The window also lists sessions found in the cloned sync repository for each
registered project. The user may restore every chat (including future ones)
or opt in to individual sessions on this PC. A provider interprets its own
session IDs; the Core only decides whether a pull is permitted. Projects are
collapsed by default in the configuration window; with "restore all" enabled,
individual sessions stay hidden. A custom selection shows a searchable list
with compact titles, update dates, and short session IDs; searching does not
change the selection. A checkbox outside each collapsed project disables all
restores for that project on this PC by persisting an empty per-provider session
selection. Re-enabling a project with no saved selection defaults to restoring
all chats; toggling it off and back on before saving preserves checked sessions.
This choice does not prune the Git clone, stop local-to-repository uploads, or
touch local provider-owned chats already present. Disabling a session does not
remove a previous restore; it only prevents subsequent copies into the provider's
folder.

Project registration in both the app and CLI uses `ProjectRegistration`:
choose a folder in the project's Git repository, detect its remote, and save
the repository root only on this PC while adding a remote-to-name entry to the
shared configuration. An explicit remote is possible when none can be
detected. Sync and client folders must not overlap, and two different remotes
must not share a sync folder name. Removing a project from this PC deletes only
its local registration; the shared mapping, synced chats and local chat files
stay intact. The displayed last run is the modification time of this PC's
baseline file. A per-project sync matches the normalized remote exactly so a
similarly named project cannot be synced accidentally. The configuration
window follows the Windows 11 settings style (Mica backdrop, custom title bar,
left `NavigationView`) and is split into three pages: **Sync** (status card with
*Sync now*, repository/folder summary, automatic sync toggle, last run),
**Projects** (registered projects, add form, restore selection), and
**Settings** (sync repository, appearance). The summary's open buttons use
`GitRemoteWebUrl` (in `CodeChatSync.Git`) to turn the origin remote (https,
scp-like or `ssh://`, Azure DevOps SSH) into a credential-free https page; the
button stays hidden for local or unrecognized remotes. The automatic sync toggle and theme
are saved as soon as they change; repository settings and restore selections
keep explicit save buttons because they can fail or batch several edits.
Registered projects have collapsed
rows with provider, remote, folder, and actions shown on expansion; the add
form is separate and its rarely used name/remote overrides are advanced options.
Visual Studio and Claude Code have separate add forms and provider-labeled
rows in the app. Claude's add form enumerates candidate working directories
from transcript metadata, accepts only Git repository roots with a remote,
and can enable Claude alongside Visual Studio for the same remote. The local
path is shared per remote; use the same repository root for both. Session
lists, baselines and restore selections are provider-scoped. Provider-specific
metadata parsing and local storage mapping remain in the provider, not Core.

`CodeChatSync.Cli` remains available to anyone who
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

The tray app polls for the provider's process names every few seconds
(`ProviderWatcher`) instead of subscribing to WMI process-termination
events. Checking a handful of process names is cheap, needs no extra
dependency or elevation, and keeps the timing rules unit-testable.

When the last instance disappears, the app waits out a **settle delay**
(20 seconds by default) before syncing: Visual Studio keeps flushing its
chat files for a moment after the window closes, and closing one instance
while another starts is common. If the provider reappears during that wait,
the pending sync is cancelled.

The per-PC `automaticSyncOnProviderClose` setting defaults to true, including
for older configuration files without this field. The tray watcher still tracks
provider status when it is false, but skips its automatic sync request. Manual
sync (global or per-project) remains available. The app saves this preference
under the coordinator's configuration gate; it is never stored in the shared
Git repository.

`SyncCoordinator` runs one sync at a time. A request arriving while a run is
in progress is dropped rather than queued: overlapping runs would copy the
same files twice and race on the baselines, and the next close picks up
anything new.

## App structure

`CodeChatSync.App` composes rather than implements:

- `SyncHost` builds the sync pipeline from the current configuration on
  every run, so changing the sync folder or registering a project does not
  require restarting the app.
- `TrayIconHost` owns the tray icon (`H.NotifyIcon.WinUI`) and its menu:
  current status, *Sync now*, *Open CodeChatSync*, *Start with Windows*,
  *Exit*. A notification is only raised when a run needs attention — a
  conflict, an aborted pull, or a missing configuration. The library's default
  Win32 popup menu executes each `MenuFlyoutItem.Command`, not its XAML `Click`
  event; all menu actions use commands, including the auto-start toggle (which
  reads the actual registry state before changing it).
- `MainWindow` is a status window, not the app's lifetime: closing it leaves
  the app watching in the tray.

The app deliberately starts **without showing a window**: it belongs in the
tray, and opening a window on every login would be intrusive. The window is
created on demand, the first time it is asked for. The one exception is a
fresh install (no sync folder, no projects, setup wizard never seen): the
window opens by itself with the setup guide, otherwise a new user would only
see a tray icon. Closing the window never ends the process
(`DispatcherShutdownMode.OnExplicitShutdown`); only *Exit* in the tray does.

First-run guidance has three parts, all in the App with the decision logic in
`CodeChatSync.Core.GettingStarted` (unit-tested):

- **Setup wizard** (`ContentDialog`): welcome, sync repository (folder, optional
  `origin`, initialize), Visual Studio projects, how syncing works, finish with
  optional first sync and tour. It opens automatically only on a fresh install;
  finishing or skipping records `onboardingWizardSeen` in the per-PC config.
- **Get started checklist** on the Sync page: sync folder, optional remote,
  first project, first sync. It is derived from the configuration and baseline
  files, highlights the next required step, and hides when the required steps
  are done or the user chooses *Hide* (`gettingStartedDismissed`).
- **Contextual hints**: a `TeachingTip` that points at the relevant control for
  each checklist step, and a six-step guided tour of the window.

*Settings > Help* reopens the wizard (also showing the checklist again) or the
tour. Both flags are per-PC and never synced.

Showing it needs more than `Window.Activate()`. Windows only lets a process
change the foreground window when it already owns it, which is not the case
when the request comes from a tray click: the window is created, is genuinely
visible, but sits *behind* the other applications, so the click looks like it
did nothing. `ForegroundWindow.Bring` briefly attaches to the input queue of
the thread that currently owns the foreground (`AttachThreadInput`), which
restores that permission for the duration of the call, then detaches again.
It also restores a window the user had minimised, which `Activate()` does not
do.

The sync flow itself lives in `SyncOrchestrator` (Core), so the CLI and the
tray app run exactly the same logic. Core declares `ISyncPublisher` and
`CodeChatSync.Git` implements it, which keeps the sync flow independent of
Git.

### Icons

`assets/icons` is the single source: the `.ico` files are linked into the app
project and copied to the output, never duplicated in `src`. `app-icon.ico`
is the executable icon (`ApplicationIcon`) and the window icon
(`AppWindow.SetIcon`); `tray-icon.ico` is a simplified glyph for the
notification area, where a detailed icon would be unreadable at 16 px.

`AppIcons` loads the tray icon at the size reported by `SM_CXSMICON` rather
than a hard-coded 16 px, so on a high-DPI display Windows picks the matching
frame from the multi-resolution `.ico` instead of upscaling a small one.

## Auto-start

Handled with a **per-user Windows `Run` entry**
(`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), toggled from the
tray menu. The app currently ships unpackaged
(`WindowsPackageType=None`), and the MSIX StartupTask extension originally
considered requires package identity, so it is not available here. A
per-user entry needs no elevation and only affects the user whose chats are
being synced.

Trade-off accepted: unlike a StartupTask, the entry is **not** removed
automatically when the app is uninstalled, so the app removes it itself when
auto-start is turned off. If the app is packaged as MSIX later, switching to
StartupTask means replacing only `RegistryStartupEntryStore`: the rules live
in `AutoStartManager` behind `IStartupEntryStore`.

An entry pointing at a different build counts as disabled, so enabling
auto-start repairs a stale entry left behind by an earlier install.

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
