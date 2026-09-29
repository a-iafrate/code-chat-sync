# CodeChatSync

Sincronizza le chat AI (Copilot in Visual Studio, in futuro altri tool) e i
prompt personali tra i PC dell'utente, tenendoli **fuori dai repo Git dei
clienti**.

> Stato: solo pianificazione, nessun codice scritto. Vedi
> [`docs/ROADMAP.md`](docs/ROADMAP.md) per la fase corrente.

## Perché

Chi lavora su progetti di clienti diversi, in repo separati, su più PC, si
trova con le chat Copilot legate a ogni singola macchina: chiuse in `.vs`,
non recuperabili altrove. CodeChatSync le porta con sé, senza toccare i repo
dei clienti e senza usare servizi cloud di terzi.

## Come funziona, in breve

- Le chat vengono **copiate** (mai collegate con symlink) tra la cartella
  locale del progetto e una cartella di sync, che a sua volta è un
  **repo Git privato di proprietà dell'utente**.
- Ogni progetto è identificato dal suo **remote Git**, non dal percorso su
  disco: la stessa solution può stare in cartelle diverse su PC diversi.
- La sincronizzazione avviene **manualmente** o **automaticamente alla
  chiusura di Visual Studio**.
- Un'app in tray (**WinUI 3**, Windows-only) gestisce configurazione, sync e
  avvio automatico; una CLI leggera resta disponibile per uso da terminale o
  script.

Dettagli completi in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Struttura della solution

```
CodeChatSync.Core                      # config, provider registry, merge/conflitti
CodeChatSync.Providers.VisualStudio    # discover/map per le chat Copilot in .vs
CodeChatSync.Git                       # commit/push/pull sul repo privato di sync
CodeChatSync.Cli                       # CLI leggera: add, sync, discover
CodeChatSync.App                       # app WinUI 3: tray, watch integrato, configurazione
```

## Funzionalità pianificate

- Sync delle chat Copilot (Visual Studio) tra PC
- **Chat Library** — vedere ed editare le chat sincronizzate, incluso il
  titolo mostrato nel pannello Chat History di Visual Studio
- **Prompt Library** — libreria personale di prompt file (`.prompt.md`),
  sincronizzata tra i PC, con distribuzione esplicita nei singoli repo
  cliente (unico caso in cui il tool scrive dentro un repo cliente,
  richiesto dal modo in cui Visual Studio legge i prompt file)
- Provider futuri per altri strumenti AI (Claude Code, Copilot CLI)

Roadmap completa, con l'ordine delle fasi, in
[`docs/ROADMAP.md`](docs/ROADMAP.md).

## Documenti per gli assistenti AI

- [`CLAUDE.md`](CLAUDE.md) — istruzioni di progetto per Claude
- [`.github/copilot-instructions.md`](.github/copilot-instructions.md) —
  istruzioni repo per GitHub Copilot

Entrambi rimandano a `docs/ARCHITECTURE.md` e `docs/ROADMAP.md` invece di
duplicarne il contenuto.

## Requisiti

**.NET 10** su tutti i progetti (fissato via `global.json`), Windows per
`CodeChatSync.App` (WinUI 3, ultima versione stabile del Windows App SDK).
`CodeChatSync.Core`, `CodeChatSync.Providers.VisualStudio` e
`CodeChatSync.Git` restano librerie platform-agnostic.

## Licenza

Progetto personale, uso privato. Nessuna licenza pubblica definita al
momento.
