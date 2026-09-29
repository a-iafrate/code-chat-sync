# Development Plan — CodeChatSync

## Purpose

Turn the decisions in [ARCHITECTURE.md](ARCHITECTURE.md) and the phase order in [ROADMAP.md](ROADMAP.md) into an executable sequence with clear completion gates. This plan does not reopen decisions already recorded in the roadmap.

## Starting point

- Phase 0 has started: the root `global.json` pins .NET SDK 10.0.401, and `src/CodeChatSync.slnx` contains the five planned project skeletons.
- The WinUI 3 app currently has only a minimal launchable window; tray, watch, and configuration behavior remain future work.
- The CLI has an initial read-only `.vs` inventory prototype that lists relative paths and file sizes without opening contents.
- The running-provider guard is configurable, because discovery never reads file contents: Debug builds skip it by default, while Release builds still block and suggest the explicit override. The override is available in both configurations via the `--allow-running-provider` flag or the `CODECHATSYNC_ALLOW_RUNNING_PROVIDER` environment variable. The guard must be reinstated without a debug bypass for any operation that writes or reads chat contents.
- The full solution restored and built successfully with zero warnings and errors, and the WinUI app launches.
- First inventory run confirmed chats are **not** stored under the solution's `.vs` directory; the only Copilot-related entries there are `CopilotIndices/<version>/SemanticSymbols.db*`, a semantic symbol index.
- Chat storage was located at `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\<session-id>\`. Each session folder holds `workspace.yaml` (descriptor), `events.jsonl` (transcript), an optional `session.db`, a `checkpoints/` folder, and `inuse.<pid>.lock` while the session is open. A machine-wide `session-store.db` with SQLite WAL files sits one level up.
- The descriptor already records repository identity (`repository`, `git_root`, `branch`, `host_type`) alongside `cwd`, `client_name`, `name`, and `user_named`. This supports identifying a project by its Git remote without deriving identity from the local path, and exposes the chat title needed later by the Chat Library.
- `discover` now reads that location through `CodeChatSync.Providers.VisualStudio`, scoped to a project folder by default and machine-wide with `--all`. On this repository it reports 8 scoped sessions and 133 in total.
- Phase 1 core types are implemented (`ProjectIdentity`, `ProjectInfo`, `ChatLocation`, `IChatProvider`, `ProcessGuard`, `RelativePathGuard`, `SyncState`, `SyncReport`, `ChatSyncService`) together with `VisualStudioChatProvider`, and are covered by `src/CodeChatSync.Tests` (xUnit, 54 passing tests).
- Open items before any copy logic: the descriptor is undocumented and not every session records a repository, so a fallback identity path is required; transcripts are append-only JSONL written live, so copying must handle sessions that are in use.
- Resolved: `session.db` is the agent's scratch database (todo/inbox tables), empty or absent for most sessions and missing entirely from the largest transcript observed, so it is excluded from sync. The machine-wide `session-store.db` indexes every repository on the PC and must never be copied into the sync repo; registering a restored session there, if ever needed, means inserting only that session's rows locally. See the storage section in [ARCHITECTURE.md](ARCHITECTURE.md).
- Resolved: `inuse.<pid>.lock` files are frequently left behind — 11 of 19 on this PC were stale, some months old — so a session counts as open only while the process named in the lock is still alive. Honoring every lock would exclude those sessions from syncing permanently.

## Delivery approach

Work one phase at a time. Keep each phase buildable, add focused automated tests alongside production behavior, and update the roadmap when a phase is completed or a decision changes. Do not start restore/sync writes until discovery has established the relevant file locations and safety behavior.

## Phase 0 — Solution foundation and discovery

### Work

1. Confirm the .NET 10 SDK is available and add the root `global.json` pin.
2. Populate the solution with `CodeChatSync.Core`, `CodeChatSync.Providers.VisualStudio`, `CodeChatSync.Git`, `CodeChatSync.Cli`, and `CodeChatSync.App`, targeting `net10.0` except for the Windows app (`net10.0-windows`).
3. Check current stable package versions before adding dependencies; confirm any dependency choice that is not already established.
4. Implement `codechatsync discover` as inspection-only functionality for the current solution's Visual Studio Copilot chat data.
5. Record observed storage locations and file characteristics needed by the provider, without copying, editing, or deleting chat data.

### Exit criteria

- The solution restores and builds with the pinned .NET 10 SDK.
- Every project uses its specified target framework and the required nullable/implicit-using settings.
- `discover` reports the relevant locations and useful file metadata, clearly reports when nothing is found, and makes no changes to the inspected files.
- Findings establish what can safely be mapped and copied; undocumented or ambiguous formats are explicitly marked as best-effort rather than assumed.

## Phase 1 — Core and Visual Studio provider

### Work

- Define `IChatProvider`, `ProjectInfo`, and the minimal chat-location model.
- Normalize Git remotes consistently for project identity; keep per-PC paths in local configuration and the shared remote-to-project mapping in the sync repository.
- Implement Visual Studio discovery and relative-path mapping only in `CodeChatSync.Providers.VisualStudio`.
- Implement local-to-sync and sync-to-local copying, with backup-before-overwrite behavior and no symlinks.

### Exit criteria

- Core contains no Visual Studio `.vs` format assumptions.
- Provider mapping is based on the project's Git remote, not its local path.
- Tests cover remote normalization, path mapping, copy direction, backup behavior, missing files, and paths that must not be allowed to escape the intended sync/local roots.
- No destination is overwritten without the required backup, and no link is created.

### Status

- Complete. Core model, sync service, Visual Studio provider, per-PC configuration, the shared remote-to-project mapping, and the `discover`/`config`/`add`/`list`/`sync` commands are implemented, with 110 xUnit tests covering remote normalization, path-traversal rejection, baseline state, push/pull direction, backup-before-overwrite, conflict detection, dry run, in-use chats, the running-provider guard on writes, configuration round-trips, and Git remote parsing.
- Verified end-to-end against a temporary sync folder and an isolated `CODECHATSYNC_HOME`: first run pushed 9 files, the second reported them unchanged, and a pending pull was correctly refused while Visual Studio was running, leaving the local file untouched.
- Design question resolved in Phase 2: a pending pull no longer aborts the project's sync. Pushes always proceed, and pulls that need the tool closed are reported as skipped and blocked by the provider.

## Phase 2 — Git integration and CLI sync

### Work

- Implement private sync-repository initialization/validation and Git pull, commit, and push operations in `CodeChatSync.Git`.
- Add `codechatsync add <solution-path>` and `codechatsync sync` to the lightweight CLI.
- Detect concurrent edits using persisted comparison state or another explicitly tested mechanism; surface conflicts instead of silently choosing a side.

### Exit criteria

- CLI commands validate configuration and provide actionable failures without exposing credentials.
- A sync can be exercised against a temporary local Git repository without requiring a hosted service.
- Conflicting changes are preserved and reported; ordinary non-conflicting changes sync in both directions.

### Status

- Complete. `CodeChatSync.Git` drives the installed `git` command line through `GitCommandRunner`, and `SyncRepository` covers initialization, status, fast-forward-only pull, commit, and push, with 131 xUnit tests in total.
- No NuGet Git dependency was added: reusing the user's own Git installation keeps their credential helpers, SSH keys, and proxy settings working, and no credential is ever stored by the tool.
- `sync` pulls first, copies files, then commits and pushes. `--no-git` limits a run to file copying, and a missing Git installation or a sync folder that is not a repository degrades to file copying instead of failing.
- Pull uses `--ff-only`: chat files are copied wholesale, so an automatic merge could silently combine two versions of the same transcript. A divergence stops the run with exit code 4 and leaves both sides untouched.
- `config set-sync-root --init` creates the repository and writes a `.gitignore` that excludes `.backups/`, so local safety copies are never committed or pushed.
- Verified end-to-end against a temporary bare repository with an isolated `CODECHATSYNC_HOME`: the first run pushed 9 chat files and published them, the second reported 9 unchanged and nothing to commit, and the remote contained only the chat files, the shared mapping, and `.gitignore`.

## Phase 3 — WinUI app, tray, watch, and auto-start

### Work

- Create the WinUI 3 tray application and its lifecycle.
- Integrate process-termination monitoring for Visual Studio and trigger automatic sync only after the last `devenv.exe` instance exits and files have had time to settle.
- Add auto-start through the selected MSIX StartupTask approach.

### Exit criteria

- Manual app startup/shutdown and tray interactions work on Windows.
- Watch does not sync while any Visual Studio instance is running and handles repeated process events safely.
- Auto-start can be enabled/disabled through the supported Windows packaging flow and leaves no manually managed scheduled task.

### Status

- Tray, watch, and auto-start are complete. 179 xUnit tests pass.
- The sync flow moved into `SyncOrchestrator` in Core, with `ISyncPublisher` declared there and implemented by `CodeChatSync.Git`. The CLI and the tray app now run exactly the same logic instead of two copies, and the sync flow stays independent of Git.
- `ProviderWatcher` polls the provider's process names instead of subscribing to WMI process-termination events. Polling a handful of names every few seconds is cheap, adds no dependency, needs no elevation, and let the timing rules be unit-tested exactly, with an injected clock and no real waiting.
- A 20-second settle delay runs after the last instance disappears, and a provider that reappears during the wait cancels the pending sync. `SyncCoordinator` drops a sync requested while one is already running, so closing several instances together cannot race on the baselines.
- Verified by running the app: it starts with no window, stays responsive in the tray, and creates no configuration as a side effect. The CLI was re-verified end to end after the refactor, with identical results to Phase 2.
- **Auto-start decision:** a per-user `Run` entry under `HKCU`, toggled from the tray, rather than the MSIX StartupTask extension originally planned. StartupTask needs package identity and the app builds unpackaged (`WindowsPackageType=None`). The rules live in `AutoStartManager` behind `IStartupEntryStore`, so packaging the app later means replacing only `RegistryStartupEntryStore`. Trade-off accepted and documented: the entry is not removed automatically on uninstall, so the app removes it itself when auto-start is turned off.
- Verified that the `Run` key round-trips a value without elevation, that an entry pointing at a different build counts as disabled so enabling repairs it, and that simply starting the app writes nothing to the registry.

## Phase 4 — Configuration window

### Work

- Configure the private sync Git remote and local sync folder.
- List registered projects and support adding/removing them, showing detected remote, local path, and last sync time.
- Provide global/per-project manual sync, the auto-sync toggle, conflict choices, and a recent-sync log.

### Exit criteria

- UI settings persist using the defined local/shared configuration split.
- Users can see sync progress, errors, and unresolved conflicts; no failure is presented as a successful sync.
- Choosing a conflict resolution applies only the selected copy and retains the other copy or backup as specified by the sync safety rules.

## Phase 4bis — Chat Library

### Work

- List synced chats by project and show their content to the extent supported by Phase 0 findings.
- Allow title editing only if discovery confirms a reliable title field; support deleting a synced chat.

### Exit criteria

- Unsupported or unknown chat formats are presented as such rather than parsed optimistically.
- Mutations follow provider-process locking and backup rules; tests verify the lock and backup behavior.

## Phase 4ter — Prompt Library

### Work

- List, edit, and sync personal `.prompt.md` files and their `name` front matter.
- Add an explicit manual deployment action to copy selected prompts into a chosen client's `.github/prompts` directory.
- Exclude deployed prompt paths from that client's Git status by default using `.git/info/exclude`; make sharing an explicit user choice.

### Exit criteria

- Deployment never happens automatically and clearly identifies the target client repository before writing.
- Existing files are backed up before replacement, and exclude-file changes preserve existing entries.
- Tests cover front matter, deployment, backup, and exclusion behavior.

## Phase 5 — Additional providers

Only after the Visual Studio workflow is reliable, implement Claude Code and Copilot CLI providers. Evaluate VS Code separately against its native sync capabilities before committing to a provider. Each provider must satisfy the same `IChatProvider` contract and file-safety gates.

## Cross-cutting quality and safety gates

- Keep all projects on .NET 10; use the latest stable dependency versions at the time they are introduced.
- Keep all code, documentation, identifiers, comments, and user-facing product text in English.
- Do not use symlinks, identify projects by local absolute paths, or introduce managed cloud storage.
- Keep Visual Studio `.vs` format handling inside `CodeChatSync.Providers.VisualStudio` and treat it as undocumented/best-effort.
- Never write provider-owned live chat data while the provider process is running. Workspace source and documentation edits may be made while Visual Studio is open; back up an existing workspace file before overwriting it.
- Keep client project data out of the sync repository. Prompt deployment into a client repository remains the sole intentional exception and must be explicit and manual.
- Add tests for each phase using a test framework selected before introducing its package dependencies. Run the smallest relevant test/build command after each change and the solution build at phase gates.

## Risks and decision gates

- **Undocumented Visual Studio storage:** Phase 0 findings determine which files are safe to synchronize and whether chat content/title editing is feasible; do not promise support for unknown formats.
- **Concurrent edits and interrupted copies:** define and test conflict detection, atomicity/recovery, and backup retention before enabling automatic sync.
- **Git remote variations:** test equivalent HTTPS/SSH and hosted/local remote forms before using normalized remotes as stable identity.
- **Windows packaging and permissions:** validate WinUI, tray, StartupTask, and filesystem access on the supported Windows setup before finalizing distribution.
- **Client-repository boundary:** prompt deployment is deliberately exceptional; require explicit destination confirmation and verify `.git/info/exclude` behavior without changing tracked team configuration.
