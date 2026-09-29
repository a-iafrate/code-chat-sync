# CodeChatSync — instructions for Claude

.NET CLI/desktop tool to sync AI chats (initially Copilot in Visual Studio,
other tools later) across the user's PCs, keeping them out of client Git
repos. The owner works on client projects in separate repos, across
multiple PCs.

## Before working on the code

Always read:

- `docs/ARCHITECTURE.md` — design, solution structure, provider contract,
  sync folder layout
- `docs/ROADMAP.md` — current status, phase in progress, decisions already
  made

Don't re-propose alternatives already discarded in `docs/ROADMAP.md` (e.g.
symlinks, local-path project identification, Blazor/MAUI/Photino for the
UI) unless the user explicitly asks: these are closed decisions.

## Non-negotiable constraints

- **Target .NET 10 on every project.** Don't propose .NET 8/9, .NET
  Standard, or .NET Framework, not even as a "compatibility" example: the
  root `global.json` pins the SDK to .NET 10 and must be respected.
- **Latest stable version for every dependency** (WinUI 3/Windows App SDK,
  `System.CommandLine`, the Git package, etc.) at the time it's added. If a
  reference example or snippet uses an older version, check the latest
  available instead of copying it.
- **All documentation, code, identifiers (classes, methods, variables), and
  comments must be in English**, regardless of the language used in chat
  with the assistant.
- **No symlinks.** Only file copies between local and the sync folder.
- **No client data in the sync repo, no tool files in client repos.** Any
  feature that risks mixing the two must be flagged before being
  implemented.
- **Write only while the tool is closed** (e.g. never while `devenv.exe` is
  running). Back up before every local overwrite.
- **Identify a project by its Git remote**, never by absolute local path.
- The format Visual Studio uses to store chats in `.vs` is
  **undocumented**: treat it as best-effort, isolate this logic in the
  `CodeChatSync.Providers.VisualStudio` provider, and don't assume it stays
  stable across VS versions.

## Working style with the user

- The user is a .NET/Azure developer and Microsoft MVP, usually writing to
  the assistant in Italian. **Reply to the user in Italian** — this applies
  to conversation only, not to anything written into the project itself
  (code, comments, docs), which stays in English per the rule above.
- Prefers short, targeted iterations: propose one concrete step at a time
  (see the current phase in `docs/ROADMAP.md`), not the whole project at
  once.
- Before introducing a new NuGet dependency, ask for confirmation if it
  isn't obvious from what's already been discussed.

## Solution

```
CodeChatSync.Core                      # config, provider registry, merge/conflict logic
CodeChatSync.Providers.VisualStudio    # discover/map for Copilot chats in .vs
CodeChatSync.Git                       # commit/push/pull on the private sync repo
CodeChatSync.Cli                       # lightweight CLI: add, sync, discover
CodeChatSync.App                       # WinUI 3 app: tray, integrated watch, configuration
                                        # (Windows-only)
```

`Core`, `Providers.VisualStudio`, and `Git` stay platform-agnostic; only
`App` is tied to Windows/WinUI 3. Full details in `docs/ARCHITECTURE.md`.
