# Repo instructions for Copilot — CodeChatSync

.NET tool to sync AI chats (initially Copilot in Visual Studio, other tools
later) across the user's PCs, keeping them out of client Git repos.

## Reference documents (read before suggesting structural changes)

- `docs/ARCHITECTURE.md` — design, solution structure, `IChatProvider`
  contract, sync folder layout
- `docs/ROADMAP.md` — current phase and decisions already made

## Code suggestion rules

- **Target .NET 10 on every project** (`global.json` at the root). Don't
  suggest .NET 8/9, .NET Standard, or .NET Framework.
- **Latest stable version for every NuGet package** at the time it's added
  (including WinUI 3/Windows App SDK), not dated versions copied from
  examples.
- **All code, identifiers (classes, methods, variables), and comments must
  be in English.**
- **No symlinks.** Every sync feature copies files, it never links them.
- **Identify a project by its Git remote**, never by absolute path.
- **Runtime sync safety:** CodeChatSync must not read or write provider-owned
  live data (e.g. chat files under `.vs`) while the provider process is
  running. It must wait until the provider is closed and back up existing
  local files before overwriting them. This is product behavior to implement,
  not a restriction on editing this workspace: source and documentation may
  be changed while Visual Studio is open, including through its integrated
  Copilot Chat. Back up existing workspace files before overwriting them.
- Code that reads/writes Visual Studio's `.vs` folder must be isolated in
  `CodeChatSync.Providers.VisualStudio`: the format isn't documented by
  Microsoft, treat it as fragile, and don't leak assumptions about it into
  `Core`.
- Don't introduce a cloud service or a third-party managed backend for
  storage: the tool only syncs to a private Git repo owned by the user.
- Namespaces and projects follow `CodeChatSync.<Area>` (`Core`,
  `Providers.*`, `Git`, `Cli`, `App`).

## Solution

```
CodeChatSync.Core
CodeChatSync.Providers.VisualStudio
CodeChatSync.Git
CodeChatSync.Cli    # lightweight CLI for terminal/scripting use: add, sync, discover
CodeChatSync.App    # WinUI 3 app: tray, integrated watch, configuration window
                     # (Windows-only; Blazor/MAUI/Photino discarded)
```

## Language

Code, identifiers, comments, suggested commit messages, and any text shown
to the user in Copilot prompts: **English**, for consistency across the
whole repo.
