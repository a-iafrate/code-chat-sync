# Roadmap — CodeChatSync

## Current status

Planning only. No code written yet. Next concrete step: the `discover`
command.

## Phase 0 — Discover (first concrete step)

- [ ] Root `global.json` pinning the SDK to **.NET 10**
- [ ] Solution scaffolding: `CodeChatSync.Core`,
      `CodeChatSync.Providers.VisualStudio`, `CodeChatSync.Git`,
      `CodeChatSync.Cli`, `CodeChatSync.App` (WinUI 3), all targeting
      `net10.0` (`net10.0-windows` for `App`)
- [ ] `codechatsync discover` command (in the lightweight `Cli`): finds
      where Visual Studio stores Copilot chats inside `.vs` for the current
      solution, and shows them (no copying, inspection only). Needed to
      confirm the exact path before writing backup/restore logic.

## Phase 1 — Core + Visual Studio provider

- [ ] `IChatProvider` and the `ProjectInfo` model (identity via Git remote)
- [ ] Visual Studio provider: `Discover` and `MapToLocal`
- [ ] Per-PC local config (paths) + shared config in the sync repo (remote →
      project mapping)
- [ ] Copy local → sync folder (push) and sync folder → local (pull), no
      symlinks
- [ ] Backup of the local file before every overwrite

## Phase 2 — Git + CLI commands

- [ ] `CodeChatSync.Git`: commit/push/pull on the private sync repo
- [ ] `codechatsync add <solution-path>` — register a solution
      (lightweight CLI)
- [ ] `codechatsync sync` — push + pull (lightweight CLI)
- [ ] Conflict handling: same file changed on two PCs → don't overwrite,
      flag it

## Phase 3 — WinUI app: tray, watch, and auto-start

- [ ] `CodeChatSync.App`: tray icon (`H.NotifyIcon.WinUI`)
- [ ] Watch integrated into the app's process: automatic sync on Visual
      Studio close (WMI event on `devenv.exe`, with a wait for file
      release) — no longer a separate CLI command
- [ ] Auto-start via the StartupTask extension (MSIX), no more manually
      created Scheduled Task

## Phase 4 — Configuration window (WinUI 3)

- [ ] Window opened from the tray: sync repo's Git remote + local folder
- [ ] List of registered projects, manual sync per project
- [ ] Toggle: automatic sync on VS close
- [ ] Conflicts section: "keep local / keep remote" choice
- [ ] Sync log

## Phase 4bis — Chat Library (priority over Prompt Library)

- [ ] List of synced chats per project, read from
      `.codechatsync/visualstudio/<project>/...`
- [ ] Chat content viewer (best-effort: the `.vs` format is undocumented,
      depends on what Phase 0 — `discover` — reveals)
- [ ] Editor for the chat title (the name shown in Visual Studio's Chat
      History panel), if the format exposes it
- [ ] Deleting a synced chat
- [ ] Same write rule as everything else: never while `devenv.exe` is
      running, backup before overwrite

## Phase 4ter — Prompt Library

- [ ] List of personal prompts (`.codechatsync/prompts/*.prompt.md`),
      synced across PCs like chats
- [ ] Prompt content editor
- [ ] Editor for the `name` front-matter field (the name shown after `/` in
      the editor, distinct from the file name)
- [ ] "Deploy to project" action: manual copy into `.github/prompts/` of
      the selected client repo (the only place in the tool that writes
      inside a client repo — see the constraint in
      `docs/ARCHITECTURE.md`)
- [ ] Automatically add the deployed prompt's path to `.git/info/exclude`
      of the client repo (default: not versioned; sharing with the client's
      team stays an explicit user choice)

## Phase 5 — Future providers (once Visual Studio works well)

- [ ] Claude Code provider (`~/.claude/projects/`, path remapping)
- [ ] Copilot CLI provider (`~/.copilot`)
- [ ] Evaluate a VS Code provider (probably unnecessary, given VS Code's
      native sync is already available)

## Decisions made (for reference — don't re-litigate without a reason)

- Project/command name: **CodeChatSync** / `codechatsync`
- No symlinks: file copies only
- Project identity: Git remote, not local path
- Sync: manual + automatic on tool close (not periodic, not on
  login/shutdown)
- Storage: the user's own private Git repo (not a service we manage)
- UI: a **WinUI 3** tray app, Windows-only (Blazor Server, MAUI, and
  Photino discarded). The watch lives in the same process as the app, not
  in a CLI command or a separate Scheduled Task.
- `CodeChatSync.Cli` remains as a separate lightweight tool for
  terminal/scripting use (`add`, `sync`, `discover`); it coexists with
  `App`, doesn't replace it, and doesn't handle the watch.
- Distribution: nothing decided yet. Store (MSIX) is feasible without
  overhauling the architecture; for personal-only use, Scoop is the
  lowest-friction option (private bucket, no publishing).
- Prompt Library: a personal prompt-file library synced like chats; to work
  in a client project it still has to be copied into that repo's
  `.github/prompts/` (the only case where the tool deliberately writes into
  a client repo), excluded from git by default via `.git/info/exclude`.
- Chat Library: viewing/editing synced chats takes priority over the Prompt
  Library in the roadmap (Phase 4bis before Phase 4ter).
- Stack: **.NET 10** on every project, pinned via `global.json`; latest
  stable version for every dependency (WinUI 3/Windows App SDK included),
  never an older version "for convenience".
- Language: **all documentation, code, identifiers, and comments in
  English**, regardless of the language used in chat with an AI assistant
  about the project.
