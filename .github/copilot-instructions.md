# Istruzioni repo per Copilot — CodeChatSync

Tool CLI .NET per sincronizzare le chat AI (inizialmente Copilot in Visual
Studio, in futuro altri tool) tra i PC dell'utente, tenendole fuori dai repo
Git dei clienti.

## Documenti di riferimento (leggerli prima di suggerire modifiche strutturali)

- `docs/ARCHITECTURE.md` — design, struttura della solution, contratto
  `IChatProvider`, layout della cartella di sync
- `docs/ROADMAP.md` — fase corrente e decisioni già prese

## Regole per i suggerimenti di codice

- **Target .NET 10 su tutti i progetti** (`global.json` alla radice). Non
  suggerire .NET 8/9, .NET Standard o .NET Framework.
- **Ultima versione stabile per ogni pacchetto NuGet** al momento
  dell'aggiunta (WinUI 3/Windows App SDK incluso), non versioni datate
  copiate da esempi.
- **Niente symlink.** Ogni funzionalità di sync copia file, non li collega.
- **Identificazione progetto per remote Git**, mai per percorso assoluto.
- **Scrittura locale solo a tool chiuso** (verificare che il processo del
  provider, es. `devenv`, non sia in esecuzione) e **sempre con backup del
  file precedente**.
- Il codice che legge/scrive la cartella `.vs` di Visual Studio va isolato in
  `CodeChatSync.Providers.VisualStudio`: il formato non è documentato da
  Microsoft, trattalo come fragile e non propagare assunzioni su di esso nel
  `Core`.
- Non introdurre un servizio cloud o un backend gestito da terzi per lo
  storage: il tool sincronizza solo su un repo Git privato di proprietà
  dell'utente.
- Namespace e progetti seguono `CodeChatSync.<Area>` (`Core`, `Providers.*`,
  `Git`, `Cli`, `Web`).

## Solution

```
CodeChatSync.Core
CodeChatSync.Providers.VisualStudio
CodeChatSync.Git
CodeChatSync.Cli    # CLI leggera per terminale/scripting: add, sync, discover
CodeChatSync.App    # app WinUI 3: tray, watch integrato, finestra di configurazione
                     # (Windows-only; Blazor/MAUI/Photino scartati)
```

## Linguaggio

Commenti, messaggi di commit suggeriti e testo nei prompt Copilot: italiano,
coerente con il resto del repo.
