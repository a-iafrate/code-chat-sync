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

Chats whose origin PC never had such a record — the eleven started with the
repository opened as a folder rather than as a solution — are rebuilt from their
transcript instead, which was confirmed working in the UI for both a one-exchange
chat and an 84-exchange one.

Phase 4 is in progress. The tray window now configures the local private sync
folder and its Git `origin`, and allows each PC to choose which synced Copilot
sessions to restore locally without deleting or pruning the Git clone. Claude
Code project selection from its own projects folder and separate per-provider
sync/restore are available. The
window registers projects by Git remote, lists their paths and last local
sync run, removes local registrations without deleting archived chats, and can
sync one project at a time. The automatic-sync preference is local to each PC;
manual sync remains available when it is disabled. Conflict resolution and the
sync log (`SyncCoordinator.RecentRuns`, with per-run detail on the Sync page)
are both done. Restored sessions on another PC and sessions without a recorded
repository still need validation.

A dry run (used by the Conflicts list and the Chat Library to refresh
themselves after every sync) must never raise `SyncCoordinator.Completed`: a
listener that inspects on completion would otherwise trigger its own dry run,
whose completion would trigger another, holding the sync gate forever and
making every request — manual or automatic — report "a sync is already
running" with no way out. This was hit in practice once the Conflicts section
started refreshing on completion; fixed by making dry runs structurally silent
rather than special-casing that one caller.

That fix stopped the gate from getting stuck forever, but left a narrower,
ordinary race: the same dry run (also run once by `LoadSettingsAsync` right
after the window opens) still holds the gate for its own brief moment, and
*Sync now* clicked in that moment got dropped outright — correct per
`RunAsync`'s contract, but indistinguishable from the sync having silently
failed, right when a user testing the fix was most likely to click it.
`SyncCoordinator.RunOrWaitAsync` is now what *Sync now* and a project's own
sync button call: on a busy gate it waits up to 10 seconds (on the semaphore
itself, not by polling) and retries once, while an automatic, watcher-triggered
sync keeps using plain `RunAsync` and is still dropped outright — it can
afford to be, since the next tool close picks up anything new regardless.

Reported again right after that fix shipped: a manual sync "took a while to
show the message" and "seemed to never finish." Added `CodeChatSync.Core.SyncTrace`
(an opt-in, zero-overhead-when-disabled timing trace of the whole pipeline —
every file, every `git` invocation, every `.vs` directory walk — enabled via
`CODECHATSYNC_TRACE_LOG`) and used it to watch a live run end to end. The real
sync completed normally in single-digit seconds, pull through push, confirmed
by the new commit landing on the remote; the "already running" message the user
saw came from a *different* dry run — the Conflicts list's own refresh — losing
the same gate race against that real sync. `GetConflictsAsync` was still calling
plain `RunAsync`, so it hit exactly the failure mode `RunOrWaitAsync` exists to
fix, just from a second call site nobody had updated. It now calls
`RunOrWaitAsync` too. While tracing this down, `CopilotChatWindowStore`'s
directory walk was also found descending into `bin`, `obj`, and `packages` —
harmless on this project's own repo, but a real cost waiting to happen on a
large, many-times-built client project — and those are now excluded alongside
`node_modules` and dot-folders.

A Claude Code transcript's absolute path is not confined to `cwd` — measured
directly, 78% of a real session's lines mentioned it, scattered across tool
parameters and free prose alike, with `cwd` itself tracking the shell's
current directory rather than one fixed value. `ClaudeTranscriptPathMapper`
treats the whole file as text and rewrites every occurrence, so a chat can now
restore onto a PC where the project lives at a different path; a transcript
archived before this existed still restores only at a matching path until a PC
syncs it again. The portable token had to be GUID-qualified after a short,
conventional one collided with literal source code inside this very project's
own development transcript. Verified against real, multi-megabyte transcripts
of this project's own development and end to end through `ChatSyncService`;
**not yet verified** is whether Claude Code itself correctly lists and resumes
a transcript rewritten this way.

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
- [x] Migrate old raw-file baselines when portable path mapping is introduced:
      a locally unchanged `workspace.yaml` must not conflict with its new
      portable archived form. `.gitignore` only excludes local backups; it
      cannot resolve a content conflict in a chat descriptor.

## Phase 3

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
- [x] Projects page lists projects already registered on another PC
      (`AvailableProjects.FindUnregistered`) that this PC hasn't added yet, so
      the user can register one by picking its local folder instead of
      retyping its remote
- [x] Conflicts section on the Sync page: lists chats changed on both sides
      from a dry-run comparison, collapsed when empty. "Keep local" / "Keep
      remote" calls `ChatSyncService.ResolveConflict` (Core, unit-tested),
      which reuses the ordinary push/pull path — backup before overwrite and
      the open-chat check both still apply — so this is the in-app version of
      the manual fixes used earlier in this project's own development
- [x] Sync log: `SyncCoordinator.RecentRuns` keeps the last 20 real sync
      outcomes in memory (per-PC, not persisted), each with a one-line summary
      and an optional detail — which files were skipped or conflicted and why,
      provider registration failures, Git prepare/publish messages. Shown as
      expandable rows on the Sync page. Fixed, as part of this: dry runs (used
      by the Conflicts list and Chat Library to refresh themselves) no longer
      raise `Completed`, which had been causing those auto-refreshes to loop
      and leave the sync gate stuck on "already running"

## Phase 4bis — Chat Library (priority over Prompt Library)

- [x] List of synced chats per project (**Chats** page, read-only): reads the
      archive in the sync folder for every registered project and provider,
      newest first, with search over titles and IDs. `IArchivedChatCatalog`
      per provider, `ChatLibrary` in the Core. Times come from the archived
      content, not file timestamps (a pull re-stamps every file). Claude
      archives are now walked at any depth — the old listing hid every chat
      started in a subfolder — and untitled Claude chats get the first thing
      the user typed as a name
- [ ] List sessions that exist only as a Chat window record (no
      `workspace.yaml`), which need the record's header and first message
      decoded; the restore selection list has the same gap
- [x] Chat content viewer (read-only, best-effort): selecting a chat shows its
      conversation, text only — tool calls and output are left out. `IArchivedChatReader`
      per provider, `ChatLibrary.Read` in the Core, limits in
      `ArchivedChatContentBuilder` (1,000 messages, 20,000 characters each, and the
      page says what was left out). A 48 MB chat reads in under 200 ms. Ids from the
      UI are never trusted as paths
- [x] Editor for the chat title (pencil icon on a row): edits the archive only,
      then pulls first and publishes, behind the sync gate. Visual Studio: rewrites
      `workspace.yaml` `name` and `user_named: true`, descriptor backed up first; a
      PC that already lists the chat takes the name into its `session-store.db`
      `summary` on the next sync. Claude Code: appends a `custom-title` record
      (the latest wins). Not yet verified: what Visual Studio's chat list shows for
      a renamed chat whose window record keeps the old header name
- [x] Deleting a synced chat (trash icon, with a confirmation): removes the chat from
      the archive only — VS its whole session folder, Claude its transcript — then
      pulls first and publishes, behind the sync gate (`IArchivedChatRemover`,
      `ChatLibrary.Delete`). Other PCs keep their copy, because the sync does not
      propagate removals; Git history keeps the removed files recoverable. Propagating
      a deletion to the other PCs would need tombstones and is not built
- [x] Same write rule as everything else: both edits touch the archive, never a
      tool's live storage, so they need no `devenv.exe` check; the one file
      rewritten (the VS descriptor) is backed up first

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
- [x] Rebuild a missing record from the transcript (`CopilotChatWindowRecord`),
      for chats whose origin PC never had one. Header alone lists the chat but
      opens it empty, so the message values are rebuilt too, keeping only the
      text blocks; an existing record on the PC is the template and every other
      field is copied byte for byte
- [x] Verified end to end: a record synced from the other PC appears, and
      rebuilt ones open with their conversation
- [x] Carry the chat's real times into a rebuilt record, taken from the
      descriptor synced from the PC that held the conversation. They are stored
      as the MessagePack timestamp extension, not as text, so the encoding is
      read from the template rather than assumed
- [x] Decided: a rebuilt record is never pushed. It is derived from an
      already-synced transcript, and two PCs rebuilding the same chat produce
      different bytes, which would be reported as a conflict on content nobody
      wrote. `IDerivedChatContent` keeps it out of the sync and makes it give
      way to a real copy arriving from another PC; once Visual Studio rewrites
      it, it is published like any other chat file
- [ ] Populate the FTS `search_index` tables so a restored chat is findable by
      search (only relevant once it is listed at all)

## Phase 5 — Future providers (once Visual Studio works well)

- [x] Claude Code provider (`~/.claude/projects/`): discover candidates from
      transcript metadata, map Git remotes, and present a selectable list in
      the app. Archive first-level `.jsonl` transcripts under a separate
      provider key; permit same-path restores and reject foreign/missing `cwd`.
- [x] Include sessions started in a project subfolder, which Claude Code stores
      under a different folder: candidates are resolved to their repository root
      and transcripts are archived under their working directory relative to it.
      Without this, a project run from `src` could not be registered at all
- [x] Remap Claude transcript paths between PCs
      (`ClaudeTranscriptPathMapper`): the whole file is treated as text and
      every occurrence of the project root is replaced, since the path is not
      confined to `cwd` — it is scattered through tool parameters and free
      prose too, and `cwd` itself tracks the shell's current directory rather
      than staying fixed. A transcript archived before this existed still
      restores only at a matching path until a PC syncs it again. The token
      needed a GUID suffix after a short, conventional-looking one collided
      with literal source code in this project's own development transcript.
      Verified against real, multi-megabyte transcripts of this project's own
      development and end to end through `ChatSyncService`
- [ ] Confirm Claude Code itself correctly lists and resumes a transcript
      whose paths were rewritten this way — not yet tested against the real
      app, only against the transcript file. Verify process detection for
      Claude hosted by another process name such as `node` before claiming
      complete runtime protection or cross-PC restoration.
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
