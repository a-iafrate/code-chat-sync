# CodeChatSync

Syncs AI chats (Copilot in Visual Studio, other tools later) and personal
prompts across the user's PCs, keeping them **out of client Git repos**.

> Status: Phases 0–3 are complete; Phase 4 (the configuration window) is in
> progress. See [`docs/ROADMAP.md`](docs/ROADMAP.md) for current progress.

## Quick start (CLI)

```powershell
# Point the tool at your private sync repository
# (add --init to create the repository if the folder is not one yet)
codechatsync config set-sync-root C:\path\to\your\sync-repo

# Register a project, identified by its Git remote
codechatsync add C:\path\to\your\project --name client-erp

# See what would be copied, then do it
codechatsync sync --dry-run
codechatsync sync
```

`codechatsync discover` lists the chat sessions found on this PC without
copying anything. Local chat files are only written while Visual Studio is
closed, and every local file is backed up before being overwritten.

`sync` pulls the sync repository, copies the chats, then commits and pushes,
using the `git` you already have installed so your existing credential helper
and SSH keys keep working. Add `--no-git` to copy files only. Pulls are
fast-forward only: if the same chat changed on two PCs, the run stops and
leaves both sides untouched instead of merging two transcripts.

## Tray app

`CodeChatSync.App` runs in the notification area and syncs on its own once
Visual Studio has been closed for long enough that its chat files have
settled. Its menu shows the current status and offers *Sync now*, a status
window, *Start with Windows*, and *Exit*. It only interrupts you when a run
needs attention, such as a conflict or a pull that could not be completed.

Open the window from the tray to choose a **separate private sync folder** and
optionally set its Git `origin` URL. Select an existing clone, or check
*Initialize as a Git repository if needed* to create one in a new folder.
A blank URL leaves an existing remote unchanged. The window reports errors
without saving a failed configuration change. Registering projects remains
available through `codechatsync add` while the rest of the UI is built.

Under *Chats on this PC*, choose which synced Copilot sessions to restore into
Visual Studio on this machine. The full Git clone remains available for backup;
a different PC can select a different subset. By default all sessions are
restored. Unchecking a session prevents future restores but does **not** delete
an existing local chat or remove anything from the repository.

## Why

Anyone working across multiple client projects, in separate repos, on
several PCs, ends up with Copilot chats tied to a single machine: locked
inside `.vs`, unreachable elsewhere. CodeChatSync carries them along,
without touching client repos and without relying on third-party cloud
services.

## How it works, in short

- Chats are **copied** (never symlinked) between the project's local folder
  and a sync folder, which is itself a **private Git repo owned by the
  user**.
- Each project is identified by its **Git remote**, not by its disk path:
  the same solution can live in different folders on different PCs.
- Syncing happens **manually** or **automatically when Visual Studio
  closes**.
- A tray app (**WinUI 3**, Windows-only) handles configuration, syncing,
  and auto-start; a lightweight CLI stays available for terminal or script
  use.

Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Solution structure

```
CodeChatSync.Core                      # config, provider registry, merge/conflict logic
CodeChatSync.Providers.VisualStudio    # discover/map for Copilot chats in .vs
CodeChatSync.Git                       # commit/push/pull on the private sync repo
CodeChatSync.Cli                       # lightweight CLI: add, sync, discover
CodeChatSync.App                       # WinUI 3 app: tray, integrated watch, configuration
```

## Planned features

- Syncing Copilot chats (Visual Studio) across PCs
- **Chat Library** — view and edit synced chats, including the title shown
  in Visual Studio's Chat History panel
- **Prompt Library** — a personal library of prompt files (`.prompt.md`),
  synced across PCs, with explicit deployment into individual client repos
  (the only case where the tool writes inside a client repo, required by
  how Visual Studio reads prompt files)
- Future providers for other AI tools (Claude Code, Copilot CLI)

Full roadmap, with phase order, in
[`docs/ROADMAP.md`](docs/ROADMAP.md).

## Documents for AI assistants

- [`CLAUDE.md`](CLAUDE.md) — project instructions for Claude
- [`.github/copilot-instructions.md`](.github/copilot-instructions.md) —
  repo instructions for GitHub Copilot

Both point to `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` instead of
duplicating their content.

## Language

All documentation, code, identifiers, and comments are in **English**,
regardless of the language used when discussing the project with an AI
assistant.

## Requirements

**.NET 10** on every project (pinned via `global.json`), Windows for
`CodeChatSync.App` (WinUI 3, latest stable Windows App SDK).
`CodeChatSync.Core`, `CodeChatSync.Providers.VisualStudio`, and
`CodeChatSync.Git` stay platform-agnostic libraries.

## License

Personal project, private use. No public license defined at this time.
