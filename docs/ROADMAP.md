# Roadmap — CodeChatSync

## Current status

Phases 0–3 are complete: discovery, guarded file sync, Git publishing, the
CLI, and the tray app with watch and auto-start are implemented. Visual Studio
chats live under `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\`,
not the solution's `.vs` folder. Sync excludes agent scratch databases and the
machine-wide session index, which may include unrelated client projects.

Restoring a Visual Studio chat's files is not enough to make it appear, and
neither is registering it in the machine-wide `session-store.db`: twelve
restored sessions were given complete `sessions` and `turns` rows there and
stayed absent from the list. Neither does the agent's own
`copilot sessions import`, which writes only to the agent store. The list is
driven by a per-solution record under `.vs/<solution>/copilot-chat/`, **inside
the client repository**. With the owner's approval this is now an explicit,
narrow exception to the "no tool files in client repos" rule, and those records
are synced like any other chat file. The `session-store.db` registration is kept
because it keeps a restored session consistent with what the agent expects of
its own store.

Still to confirm end to end: chats archived **before** this existed have no
record in the sync repository, so the PC that owns them has to sync once with
this version before the other PC can list them.

Phase 4 is in progress. The tray window now configures the local private sync
folder and its Git `origin`, and allows each PC to choose which synced Copilot
sessions to restore locally without deleting or pruning the Git clone. Claude
Code project selection from its own projects folder and separate per-provider
sync/restore are available, but cross-path Claude transcript restore is
blocked until safe remapping and resumption are verified. The
window registers projects by Git remote, lists their paths and last local
sync run, removes local registrations without deleting archived chats, and can
sync one project at a time. The automatic-sync preference is local to each PC;
manual sync remains available when it is disabled. Conflict resolution and
the recent-sync log remain open. Restored sessions on another PC and sessions
without a recorded repository still need validation.

## Phase 0 — Discover (first concrete step)

- [x] Root `global.json` pinning the SDK to **.NET 10**
- [x] Solution scaffolding: `CodeChatSync.Core`,
      `CodeChatSync.Providers.VisualStudio`, `CodeChatSync.Git`,
      `CodeChatSync.Cli`, `CodeChatSync.App` (WinUI 3), all targeting
      `net10.0` (`net10.0-windows` for `App`)
- [x] `codechatsync discover` command (in the lightweight `Cli`): finds
      where Visual Studio stores Copilot chats and shows them (no copying,
      inspection only). Confirmed they live under
      `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\`,
      not inside the solution's `.vs` folder.

## Phase 1 — Core + Visual Studio provider

- [x] `IChatProvider` and the `ProjectInfo` model (identity via Git remote)
- [x] Visual Studio provider: `Discover` and `MapToLocal`
- [x] Per-PC local config (paths) + shared config in the sync repo (remote →
      project mapping)
- [x] Copy local → sync folder (push) and sync folder → local (pull), no
      symlinks
- [x] Backup of the local file before every overwrite

## Phase 2 — Git + CLI commands

- [x] `CodeChatSync.Git`: commit/push/pull on the private sync repo
- [x] `codechatsync add <solution-path>` — register a solution
      (lightweight CLI)
- [x] `codechatsync sync` — push + pull (lightweight CLI)
- [x] Conflict handling: same file changed on two PCs → don't overwrite,
      flag it
- [x] Keep Git from rewriting chats (`.gitattributes` with `* -text` plus
      `core.autocrlf=false` on the sync repository, applied to existing
      repositories too). Line-ending translation on checkout was reporting
      conflicts on files nobody had edited.

## Phase 3 — WinUI app: tray, watch, and auto-start

- [x] `CodeChatSync.App`: tray icon (`H.NotifyIcon.WinUI`)
- [x] Watch integrated into the app's process: automatic sync once Visual
      Studio has been closed long enough for its files to settle — no longer
      a separate CLI command. Implemented by polling the provider's process
      names rather than a WMI subscription: cheaper, dependency-free, and
      unit-testable.
- [x] Auto-start without a manually created Scheduled Task: per-user
      Windows `Run` entry, toggled from the tray. The MSIX StartupTask
      extension needs package identity, which the unpackaged app does not
      have; the rules sit behind `IStartupEntryStore` so switching to
      StartupTask later only means replacing the store.

## Phase 4 — Configuration window (WinUI 3)

- [x] Window opened from the tray: sync repo's Git remote + local folder
- [x] Per-PC selection of which archived Visual Studio chats are restored to
      the provider folder; the full Git clone is retained and existing local
      chats are not deleted
- [x] List of registered projects (add/remove on this PC), local path and
      last run; manual sync per project. The list and add form are collapsible,
      show Visual Studio (Copilot) explicitly, and the window sections are separated.
- [x] Window redesigned after `docs/CodeChatSync Settings.html`: Sync, Projects,
      and Settings pages in a left navigation pane, Windows 11 styling
- [x] First-run guidance: setup wizard on a fresh install, Get started
      checklist on the Sync page, contextual hints and a guided tour; all
      reopenable from Settings > Help
- [x] Toggle: automatic sync on VS close (per PC, enabled by default)
- [x] Opt-outs: sync Visual Studio / Claude Code while running (per PC, off by
      default, with warning and confirmation; a skipped tool no longer holds back automatic sync)
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

## Phase 4quater — Restored chats actually visible

- [x] Register restored sessions in Visual Studio's local `session-store.db`
      (`IChatSessionRegistrar` in the Core, `CopilotSessionStore` in the
      provider): insert only the project's own rows, never copy the database,
      back it up first, and run under the "tool must be closed" guard
- [x] Rebuild the `turns` rows from `events.jsonl`
      (`CopilotTranscriptTurns`): the chat list renders from them, so a
      `sessions` row alone left every restored chat invisible. Verified against
      a transcript Visual Studio had already turned into thirteen rows itself
- [x] Don't rewrite a descriptor whose only difference is the letter case of
      this PC's path
- [x] Establish what actually drives the chat list: a per-solution MessagePack
      file under `.vs/<solution>/copilot-chat/<hash>/sessions/<session-id>`,
      not the machine-wide database
- [x] Owner approved writing inside a client repository's `.vs` folder, as a
      narrow exception limited to that one Visual Studio-generated, Git-ignored
      folder
- [x] Sync those records (`CopilotChatWindowStore`): copied rather than
      synthesized, with the per-PC workspace folder left out of the synced path
      and read from disk on restore. A project where Visual Studio has never
      opened a chat has no such folder, so the restore is refused with an
      explanation instead of guessed at
- [ ] Verify end to end from the other PC, which must sync once with this
      version before its chats can be listed here
- [ ] Populate the FTS `search_index` tables so a restored chat is findable by
      search (only relevant once it is listed at all)

## Phase 5 — Future providers (once Visual Studio works well)

- [x] Claude Code provider (`~/.claude/projects/`): discover candidates from
      transcript metadata, map Git remotes, and present a selectable list in
      the app. Archive first-level `.jsonl` transcripts under a separate
      provider key; permit same-path restores and reject foreign/missing `cwd`.
- [ ] Safely remap Claude transcript paths between PCs and verify Claude can
      display/resume the restored sessions. Extend discovery to sessions
      started in project subfolders and verify process detection for Claude
      hosted by another process name such as `node` before claiming complete
      runtime protection or cross-PC restoration.
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
