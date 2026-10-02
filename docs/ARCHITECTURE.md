# Architecture — CodeChatSync

## Goal

Sync AI chats (Copilot, other tools later) across the user's PCs, keeping
them **out of client repos**. No client data should ever end up in the sync
repo; no sync-tool file should ever end up in a client repo.

Two exceptions to the second half are approved, both narrow, both spelled out
below: restoring Visual Studio's chat list entry into
`.vs/<solution>/copilot-chat/`, without which a restored chat is invisible, and
deploying a prompt file into `.github/prompts/`, without which Visual Studio
cannot read it.

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
  that are in use or getting corrupted. This covers a tool's own index as much
  as its chat files: a second writer on a live SQLite database is not something
  a backup recovers from. The only exception is an explicit,
  per-PC opt-out (`skipRunningCheckProviderIds` in the local config,
  exposed for Visual Studio and Claude Code under *Settings > Advanced*):
  `ProcessGuard.ForSync` wraps the real guard so every sync run (manual or
  automatic) and discovery ignore that provider's processes. The watchers keep
  using the real guard to detect closes, but the tray host does not count a
  skipped provider as running: it neither holds back the automatic sync
  triggered by another tool closing nor shows "waiting". A skipped tool that
  stays open does not trigger a sync by itself; *Sync now* covers that case.
  Backups before overwrite stay active.
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
- Every NuGet dependency (`System.CommandLine`, `H.NotifyIcon.WinUI`,
  `Microsoft.Data.Sqlite`, etc.) must be taken at the **latest stable version
  available** at the time it's added, not copied from examples or
  tutorials that might reference older versions.
- `Microsoft.Data.Sqlite` lives only in
  `CodeChatSync.Providers.VisualStudio`, which needs it to add restored
  sessions to Visual Studio's own chat index. The Core stays free of it, so a
  provider for a tool that stores nothing in SQLite pays nothing for it.
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
restore choices. `IChatContentMapper` lets a provider rewrite a file's
per-PC content around the sync: the sync folder always holds the portable
form, used for comparison and baselines too, so a file rewritten for this PC
is never mistaken for a local edit. Visual Studio's descriptor keeps the path
in two known fields; Claude's transcript scatters it through structured
fields and free text alike, so its mapper treats the whole file as text
instead (`ClaudeTranscriptPathMapper`, described under Claude Code below).
`IChatRestoreValidator` optionally refuses an archived file before any local
overwrite; Claude uses it to reject a transcript that still carries another
PC's raw, unmapped path and whose recorded `cwd` falls outside this PC's
registered project folder — a transcript already in portable form restores
anywhere, since restoring it rewrites the path regardless. `IChatSessionRegistrar`
covers tools that keep their own index of chats and only list one that appears
in it: the Core says which sessions this PC may see, the provider decides what
announcing them means, and the step runs under the same "tool must be closed"
rule as any other local write. `IDerivedChatContent` marks a local file the
provider generated here from data that is already synced: it is never published,
and it gives way to a copy arriving from another PC instead of conflicting with
it, because it can always be derived again.

A local chat file is **not** rewritten when it differs from the restored form
only in letter case. Visual Studio was observed recording the same folder as
both `c:\` and `C:\`, and it may compare that path as an exact string, so
normalizing the spelling risks hiding a chat that is currently visible while
fixing nothing. When an existing baseline still hashes the raw local
`workspace.yaml` from before portable path mapping, an unchanged local file
is treated as unchanged: a newer portable archive is restored, or an archive
still in the raw format is upgraded to the portable form. Actual concurrent
edits continue to be reported as conflicts.

`ProjectInfo` holds the project's identity
current local path. The Core handles copying, date comparison, backup, and
commit/push/pull. The provider only has to say where to look.

## Sync folder layout

```
.codechatsync/
  codechatsync.json                      # shared Git remote → project name mapping
  .gitignore                             # excludes .backups/
  .backups/                              # local safety copies, never committed
  <provider>/<project-from-remote>/...   # e.g. visualstudio/clientA-erp/
    <session-id>/...                     # the chat's own files
    <session-id>/.chat-window/<solution>  # Visual Studio's list entry for that chat,
                                          # e.g. .chat-window/src/CodeChatSync.slnx.
                                          # The per-PC workspace folder is left out:
                                          # each PC resolves its own on restore.
```

## Visual Studio chat storage (verified, undocumented)

There are **two** layers, and both are needed for a chat to be usable:

1. The **agent's transcript**, per user and per session under
   `%LOCALAPPDATA%` (below). This is the conversation itself.
2. The **chat window's own record**, per solution, inside that solution's
   `.vs` folder:
   `​.vs/<solution>/copilot-chat/<hash>/sessions/<session-id>`.

**The list Visual Studio shows comes from layer 2, not from layer 1.** This was
established by elimination: twelve restored sessions were given complete
`sessions` rows and reconstructed `turns` rows in the machine-wide
`session-store.db`, matching byte for byte what Visual Studio writes for a chat
it does list, and none of them appeared. The single listed chat was the only one
with a file under `copilot-chat/`, and that file's `LastMessagePreview` field
holds the exact subtitle rendered in the panel.

The record is MessagePack carrying Visual Studio's own conversation types
(`Microsoft.VisualStudio.Conversations.Chat.HelpWindow`, `Responders`,
`SelectedAgent`, `IsRead`, `TimeUpdated`, `LastMessagePreview`). It is
**self-contained**: 36 KB held an entire conversation, and only one absolute path
appeared anywhere in it — inside the chat's own text, not in its structure. So a
chat that never used the CLI agent, and therefore has no transcript at all, still
restores from this file alone.

An existing record is **copied, not rebuilt**, like every other chat file, which
keeps its full detail. Copying was verified directly: a record placed in the
right folder under a different session ID appeared in the list as a second chat.

A record is **rebuilt from the transcript** when the PC that produced the chat
never had one (`CopilotChatWindowRecord`). That turned out to be the common
case: of fifteen sessions on one PC, only the four opened with the *solution*
loaded had a record, while the eleven started with the repository opened as a
*folder* had none — so without this they could never be listed on any PC. The
rebuild is driven by evidence, in two steps that were each confirmed in the UI:

- The record is a sequence of MessagePack values: a version marker, a header,
  then one value per message, shaped `[kind, {…}]` with `0` for a question and
  `1` for a reply. A header alone **lists the chat but opens it empty**.
- The message values look far heavier than they are. In a measured example the
  question carried 18,440 characters of tool definitions and 2,495 of gathered
  IDE context around 141 characters of text; what is said lives in content
  blocks of kind `3`. Dropping the rest turned a 46 MB transcript into a 298 KB
  record holding all 84 exchanges, which opened and read correctly.

A rebuilt record is **never published**. It is derived from a transcript the
sync repository already holds, so publishing it would store the same
conversation twice; and because every rebuild mints fresh message identifiers,
two PCs rebuilding the same chat produce different bytes — measured at 452 on
one chat — which the sync would report as a conflict on content nobody wrote.
Deterministic identifiers would not help, since each PC copies raw fields from
its own template and those differ by Visual Studio edition.

This is tracked per PC in
`%LOCALAPPDATA%\CodeChatSync\state\<provider>\<project>.chat-window.json`,
which stores the hash of each record this machine wrote. That hash is what
distinguishes a rebuild the tool still owns from one Visual Studio has taken
over: continuing a restored chat makes Visual Studio rewrite the record with the
real thing, tool calls included, and from that moment it is published like any
other chat file — otherwise the other PC would never see the continuation. In
the other direction, a rebuild is redone when the transcript has grown past it,
which is how a chat continued elsewhere shows its new exchanges here.

Nothing is invented: an existing record on this PC is the template, and every
field other than the identifiers, the text and the times is copied byte for
byte, so the extension types and typed containers survive. The times come from
the descriptor synced from the PC that held the conversation, so the list shows
each restored chat at its real age; Visual Studio stores them as the MessagePack
timestamp extension rather than as text, so that encoding is read from the
template rather than assumed. A PC with no record at all has no
shape to copy, so the rebuild is skipped with an explanation — opening a chat
once provides both the template and the folder. Tool-call blocks are
deliberately not rebuilt: they record file edits, confirmations and execution
results, and inventing state that never happened on this PC would be worse than
reading the conversation without them.

`<hash>` is **not** derived from the solution: the same value was found on
fourteen unrelated solutions of one PC, so it identifies the machine or the
signed-in account. No hash of the solution path, its name, the account or the
chat window identifier reproduced it, and it appears nowhere in Visual Studio's
own state as text. It is therefore **read from disk, never computed**
(`CopilotChatWindowStore.FindWorkspaceId`), and the synced path leaves it out
entirely. A project where Visual Studio has never opened a chat on this PC has
no such folder, so restoring a record there is refused with an explanation
rather than guessed at: opening a chat once creates it.

**This is the one place besides the Prompt Library where the tool writes inside a
client repository**, and it is a deliberate exception to that rule, approved by
the owner, because there is no other way to make a restored chat visible. It is
narrow: only `.vs`, which Visual Studio generates and Git ignores, and only the
`copilot-chat/<hash>/sessions/` folder. The usual rules still apply — nothing is
written while Visual Studio is running, and an existing record is backed up
before it is replaced.

A session that is currently open is skipped in both directions: Visual Studio
rewrites its record as the conversation goes on, so copying it would risk a torn
file exactly as copying a live transcript would.

An earlier note in this document claimed chats were not in `.vs` at all because
that folder only held `CopilotIndices/<version>/SemanticSymbols.db*`. That was
right about the transcript and wrong about the list.

The transcript lives per user, per session under:

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
- **`session-store.db` — never synced, but written to locally.** It is
  machine-wide and indexes the sessions of *every* repository on the PC, so
  copying it into the sync repo would put unrelated clients' data there,
  violating the separation rule.

  A session folder is **not** self-contained without it: the agent uses it to
  know its own sessions. It does **not** drive the chat list — see the two
  layers above. Two tables are written:

  - `sessions(id, cwd, repository, host_type, branch, summary, created_at,
    updated_at)` identifies the chat. `summary` holds what `workspace.yaml`
    calls `name`; timestamps are ISO 8601 with milliseconds and a `Z` suffix.
  - `turns(session_id, turn_index, user_message, assistant_response, timestamp)`
    is what the list actually **renders**: the entry's title is the user's
    message, its subtitle the assistant's reply.

  Every sync writes both, built entirely from the session's own files —
  `workspace.yaml` for the row, `events.jsonl` for the turns
  (`CopilotTranscriptTurns`) — and never copies or moves the database. This
  keeps a restored session consistent with what the agent expects of its own
  store, which is worth having on its own; it is **not** sufficient to make the
  chat appear in Visual Studio's list.

  The reconstruction rule is one turn per `user.message` event, in order, paired
  with the last non-empty `assistant.message` before the next question. It was
  checked against a 3.2 MB transcript whose thirteen turns Visual Studio had
  recorded itself: all thirteen user messages and eleven of the thirteen replies
  came back identical. In the other two Visual Studio stored no reply where the
  transcript holds a partial one, which only changes the preview text. The
  `timestamp` it stores matches no event — it is when the row was written — so
  the turn's own time is used, which is what the list needs in order to sort.

  The schema is undocumented (version 6 when this was written), so the columns
  present are read at runtime and only those are written: a Visual Studio update
  that adds or drops one degrades to a skipped registration with an explanation
  instead of a failed sync. A session Visual Studio has already recorded a turn
  for is left completely alone, which makes the step safe to repeat on every run
  — and it has to run on every run, because a session restored before this
  existed needs no file copy and would otherwise stay hidden forever.

  `checkpoints`, `session_files`, `session_refs` and the `search_index` FTS
  tables are deliberately left alone. Only `search_index` has a visible cost: a
  restored chat is listed and can be opened, but will not be found by searching
  its text until that is addressed.
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
- Per-PC safety copies of provider-owned local data the tool modifies:
  `%LOCALAPPDATA%\CodeChatSync\backups\<provider>\<timestamp>\`, currently
  Visual Studio's `session-store.db` and its `-wal`/`-shm` companions. These sit
  outside the sync folder on purpose, unlike the backups taken before a chat
  file is overwritten: that index covers every repository on the PC, so a copy
  of it inside the sync repository would put unrelated clients' data there even
  though Git ignores the folder.
- `CODECHATSYNC_HOME` overrides all three local locations, which keeps tests and
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
hanging an unattended sync. The process itself has a timeout (5 minutes, 30
seconds for a credential-free availability check), but exiting is not the same
as being done: draining the redirected output and error streams afterwards
uses a bounded wait (5 seconds) rather than the parameterless `WaitForExit()`,
because a helper Git spawns — a credential manager, typically — can inherit
those pipe handles and keep them open after `git` itself has exited, which
would otherwise block forever with no process left to time out. A truncated
tail on that rare path is preferable to a sync that never returns.

A `sync` run is pull → copy → commit → push:

- **Pull is `--ff-only`.** Chat files are copied wholesale, so an automatic
  merge or rebase could silently combine two versions of the same transcript.
  A divergence stops the run and leaves both sides untouched for the user to
  resolve.
- **No remote, or an empty remote**, is a normal first run, not a failure:
  the sync continues and the commit simply stays local or becomes the first
  push.
- **A branch without upstream is fetched first.** If the remote already has
  the same branch (another PC pushed first), it is pulled fast-forward and set
  as upstream before anything is copied; if the remote only has other branches
  or cannot be reached, the run stops. On a branch with no commit yet, the
  untracked `.gitignore` from `--init` is set aside for the checkout and its
  `.backups/` entry merged back.
- **A sync folder that is not a repository** degrades to plain file copying
  with an explanation, so chats still reach the sync folder. **Missing or
  unusable Git on a folder that is a repository stops the run**, because it
  could not be pulled first. `--no-git` forces file-only mode.
- **Git must never rewrite a chat.** Chats are copied wholesale and compared by
  content hash, so a byte Git changes on its own is indistinguishable from an
  edit made on another PC. With the common `core.autocrlf=true`, checking out a
  chat file translates its line endings: the sync then sees both sides as
  changed since the last run and reports a conflict on a file nobody touched.
  Every run therefore writes a `.gitattributes` containing `* -text` and sets
  `core.autocrlf=false` on the sync repository — not only at `--init` time,
  since the setting is usually inherited from the user's global configuration
  long after the sync folder was created. It is applied after the pull, so the
  new file cannot block a fast-forward checkout; the next commit records the
  files as they really are on disk, once, and they stay stable afterwards.
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
  with a remote can be registered in the app, but a session **started in a
  subfolder belongs to that project**: Claude Code derives a session's storage
  folder from the directory it was run from, so one repository spreads over as
  many folders as the subdirectories used — running it from `src` is the common
  case, and requiring the root would have left those sessions unregisterable.
  The app therefore resolves every candidate working directory to its
  repository root and offers the ones sharing a root as a single project.

  Each transcript is archived under its working directory relative to the
  project root (`src/<session-id>.jsonl`, or the plain file name for the root
  itself). That relative path is identical on every PC, which is what lets a
  restore rebuild the right storage folder locally. A transcript is only
  considered when it sits in the folder its own `cwd` maps to, so nothing is
  silently relocated by a restore.

  Only top-level session `.jsonl` files are archived; memory and session
  artifacts are not included.

  A transcript's absolute path is **not confined to `cwd`**: it is recorded on
  most lines, in tool parameters (`file_path`, `path`, duplicated again under
  `wireToolInputs`), and in free text such as system-reminder attachments and
  the assistant's own prose — measured directly on a real session, 78% of its
  lines mentioned the path somewhere, and `cwd` itself tracked the shell's
  current directory as the agent moved around, not one fixed value. Extracting
  only fields with a known name would miss the free-text occurrences, so
  `ClaudeTranscriptPathMapper` instead treats the whole file as text and
  replaces every occurrence of the project's root — wherever it sits, whatever
  field or prose it is in — with a portable token, the way a careful
  find-and-replace would. A path outside the project root (a temp folder the
  agent happened to use, a different repository) never matches and is left
  alone. Matching is case-insensitive and boundary-aware, so `C:\repo` does not
  also match inside `C:\repo-backup`.

  The token is not a short, conventional-looking placeholder such as the
  `${project}` Visual Studio's descriptor mapper uses: a transcript can contain
  literal source code, and a tool call that writes code mentioning a common
  interpolation syntax (shell, JavaScript, templates) would have that text
  mistaken for the marker on restore. That exact collision was found testing
  against a real transcript of this project's own development, where a test
  fixture literally contained `${project}` as a string constant, and corrupted
  that one historical message on restore — never a real file, since a
  transcript is only ever a historical log, not something replayed. The token
  is GUID-qualified to make an unrelated, organic occurrence astronomically
  unlikely; the residual case — this tool's own source code, which must define
  the constant as a string literal, appearing in a transcript of someone using
  Claude Code to develop CodeChatSync itself — is not eliminated by any choice
  of token, but stays harmless for the same reason.

  A transcript archived **before** this existed still carries its source PC's
  raw, unmapped path, and restores only where that already matches, exactly as
  before; it becomes restorable anywhere once some PC syncs it again. Visual
  Studio's descriptor mapper does not face the free-text problem, since its
  format keeps the path in two known fields.

  Verified: the mapper round-trips a real, multi-megabyte transcript of this
  project's own development byte-for-byte, and `ChatSyncService` correctly
  pushes from one path and restores onto a different one end to end. **Not yet
  verified:** whether Claude Code's own session list and `--resume` correctly
  display and resume a transcript rewritten this way. The runtime guard
  currently recognizes process name `claude`, not every possible host such as
  `node`; close Claude completely before syncing.
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
stay intact.

The Projects page also lists projects another PC has already registered —
`AvailableProjects.FindUnregistered` (Core, unit-tested) diffs the shared
mapping against this PC's local registrations, purely in memory, no provider
discovery involved. A project stays off that list once it is registered here
for *any* provider, since adding a second provider for an already-known
project goes through that provider's own add flow instead. Picking one from
the list only asks for its local folder: the remote and sync folder name come
from the shared entry as-is and are passed to `ProjectRegistration.Add` as an
explicit override, the same mechanism the manual add form's "Remote override"
field already uses, so a folder that is not even a Git repository can still be
registered this way. The displayed last run is the modification time of this PC's
baseline file. A per-project sync matches the normalized remote exactly so a
similarly named project cannot be synced accidentally. The configuration
window follows the Windows 11 settings style (Mica backdrop, custom title bar,
left `NavigationView`) and is split into three pages: **Sync** (status card with
*Sync now*, repository/folder summary, a conflicts section, automatic sync
toggle, last run, and a sync log), **Projects** (registered projects, add form,
restore selection), and **Settings** (sync repository, appearance). The summary's open buttons use
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

The Sync page's **Conflicts** section lists every chat currently changed on
both sides, from the same dry-run comparison a sync performs — skipping the
git pull a real sync would do first, exactly like `--dry-run` — and stays
collapsed when there is nothing to resolve. *Keep local* or *Keep remote*
calls `ChatSyncService.ResolveConflict` (Core), which forces that side to win
by reusing the ordinary push or pull path: a pull still backs up the replaced
local file first, and a currently open chat is still refused. It applies
regardless of whether the file is still actually in conflict when clicked, so
stale UI state cannot block it; a side that turns out to have nothing to keep
is reported instead of guessed at, the same as an ordinary sync. The list
refreshes after any sync completes, manual or automatic, and after resolving
one entry.

The Sync page's **Sync log** lists `SyncCoordinator.RecentRuns` (see above),
each row collapsed to a one-line summary and expandable for the detail; a row
with nothing to show besides its summary is disabled rather than left to
expand into blank space. The window also now reacts to `SyncHost.SyncStarted`
— raised the moment a real sync begins, including one the watcher triggered
automatically — so "Syncing…" appears for a background run too, not only one
started from *Sync now*.

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
anything new. `RunOrWaitAsync` is the one exception, used for *Sync now*, a
project's own sync button, and the Conflicts list's own dry-run refresh
(`SyncHost.GetConflictsAsync`): it waits for a busy gate to free up (up to 10
seconds, by waiting on the gate's own semaphore rather than polling) and
retries exactly once before giving up. These requests have no "next time" for
an automatic trigger's logic to lean on, and the gate is busy astonishingly
often for a reason that has nothing to do with an actual sync: `LoadSettingsAsync`
on startup and every refresh of the Conflicts list — including the one a real
sync itself triggers right after finishing — all run a dry run that holds the
same gate just as briefly. Without this, clicking *Sync now* in the first
moment after opening the window reliably lost that race and reported "a sync
is already running" — technically correct, useless to the user, and with no
way to make it actually sync short of clicking again and hoping the timing
worked out.

Both call sites needed the fix, not just *Sync now*: a sync could complete
successfully in the background while `GetConflictsAsync`'s own dry run — racing
the same real sync for the same gate — was rejected and surfaced its own "a
sync is already running" message, which read as the whole operation having
failed even though the chat had already been pushed. Confirmed directly:
`CODECHATSYNC_TRACE_LOG` (below) showed a real sync completing end to end,
including a successful `git push` that landed on the remote, at the same moment
the window displayed that message — the user had no way to tell the difference
between "still working" and "silently failed" from the UI alone.

A **dry run never raises `Completed` or joins the log**, and `Started` is
raised for a real sync only. This is not cosmetic: the Conflicts list and the
Chat Library both refresh themselves by running a dry run whenever a sync
completes (see above), so if a dry run's own completion were announced the
same way, that announcement would trigger another refresh, whose dry run would
announce itself in turn — the gate (`SyncCoordinator`'s semaphore) would stay
held forever, and every subsequent sync request, manual or automatic, would
report "a sync is already running" with no way out. This was an actual
regression once the Conflicts section started refreshing on every completion;
the fix is structural, not a special case for that one caller, so any future
listener that inspects on completion is safe by construction.

`SyncCoordinator.RecentRuns` keeps the last 20 **real** sync outcomes in
memory (newest first; per-PC, cleared on restart — there is no reason to
persist or sync it). Each `SyncLogEntry` carries a one-line summary plus an
optional `Detail`: every entry with a `Conflict` or `Skipped` action, any
provider registration failure, and the publish/prepare messages from Git, so
clicking into a failed or incomplete run on the Sync page's **Sync log**
section answers "which file, and why" without reaching for the CLI.
`NeedsAttention` flags a run for a highlighted icon the same way
`SyncOutcome.NeedsAttention` does for the status card.

## Diagnosing a slow or seemingly stuck sync

`CodeChatSync.Core.SyncTrace` is a step-by-step timing trace of the whole sync
pipeline, off by default and with no overhead when disabled — every call site
checks `IsEnabled` first, so leaving the instrumentation in place permanently
costs nothing. Enabled by setting `CODECHATSYNC_TRACE_LOG` to a file path before
launching either the CLI or the app (for the app, from a terminal or via
`Start-Process` with the variable set on that one process — the GUI has no way
to set its own environment before Windows creates the process); never set by
the test suite, so a test run never writes outside its own temp directories.
The file is truncated once per process rather than appended across runs, so it
always holds exactly the run it was enabled for.

Every step is timestamped with the elapsed time since the process started and
the managed thread ID, and traced end to end: `SyncOrchestrator.Run` (project
resolution, publisher prepare/publish), `ChatSyncService.Sync` (discovery, then
**every file individually** — not sampled — so a hang shows up as the last
"start" line with no matching "done"), `VisualStudioChatProvider`'s two
discovery phases and its SQLite/`.vs` registration steps, the directory count
and duration of every `.vs` tree walk, and every `git` invocation with its exit
code and output size. `SyncCoordinator` also logs a gate rejection and a
`RunOrWaitAsync` wait/retry decision, both otherwise invisible — a rejected
request runs nothing downstream, so without this line it leaves no trace of
having happened at all.

This is how the actual cause of an "already running" message that would not go
away was found: the real sync was completing normally, pull through push, in
single-digit seconds; what the user saw was a *different* dry run (the
Conflicts list's own refresh) losing the same gate race and surfacing its
rejection as if the sync itself had failed — see `RunOrWaitAsync` above. The
same investigation also caught `CopilotChatWindowStore`'s directory walk
descending into `bin`, `obj`, and `packages`, which cost nothing on this
project's own repo (dozens of folders) but could have been expensive on a
large, many-times-built client project; those are now excluded alongside
`node_modules` and dot-folders.

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
