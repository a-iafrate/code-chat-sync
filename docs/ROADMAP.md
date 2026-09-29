# Roadmap — CodeChatSync

## Stato attuale

Solo pianificazione. Nessun codice scritto. Prossimo passo concreto: comando
`discover`.

## Fase 0 — Discover (primo passo concreto)

- [ ] `global.json` alla radice che fissa l'SDK a **.NET 10**
- [ ] Scaffolding solution: `CodeChatSync.Core`, `CodeChatSync.Providers.VisualStudio`,
      `CodeChatSync.Git`, `CodeChatSync.Cli`, `CodeChatSync.App` (WinUI 3),
      tutti su `net10.0` (`net10.0-windows` per `App`)
- [ ] Comando `codechatsync discover` (nella `Cli` leggera): individua dove
      Visual Studio salva le chat Copilot dentro `.vs` per la solution
      corrente, e le mostra (nessuna copia, solo ispezione). Serve a
      confermare il percorso esatto prima di scrivere backup/restore.

## Fase 1 — Core + provider Visual Studio

- [ ] `IChatProvider` e modello `ProjectInfo` (identità per remote Git)
- [ ] Provider Visual Studio: `Discover` e `MapToLocal`
- [ ] Config locale per PC (percorsi) + config condivisa nel repo di sync
      (mapping remote → progetto)
- [ ] Copia locale → cartella di sync (push) e cartella di sync → locale
      (pull), senza symlink
- [ ] Backup del file locale prima di ogni sovrascrittura

## Fase 2 — Git + comandi CLI

- [ ] `CodeChatSync.Git`: commit/push/pull sul repo privato di sync
- [ ] `codechatsync add <path-solution>` — registra una solution (CLI leggera)
- [ ] `codechatsync sync` — push + pull (CLI leggera)
- [ ] Gestione conflitti: stesso file cambiato su due PC → non sovrascrivere,
      segnalare

## Fase 3 — App WinUI: tray, watch e avvio automatico

- [ ] `CodeChatSync.App`: icona in tray (`H.NotifyIcon.WinUI`)
- [ ] Watch integrato nel processo dell'app: sync automatico alla chiusura
      di Visual Studio (evento WMI su `devenv.exe`, con attesa di rilascio
      file) — non è più un comando CLI separato
- [ ] Avvio automatico via StartupTask extension (MSIX), non più Scheduled
      Task creata a mano

## Fase 4 — Finestra di configurazione (WinUI 3)

- [ ] Finestra aperta dal tray: remote Git di sync + cartella locale
- [ ] Elenco progetti registrati, sync manuale per progetto
- [ ] Toggle: sync automatico alla chiusura di VS
- [ ] Sezione conflitti: scelta "tieni locale / tieni remoto"
- [ ] Log delle sincronizzazioni

## Fase 4bis — Chat Library (priorità sopra Prompt Library)

- [ ] Elenco chat sincronizzate per progetto, lette da
      `.codechatsync/visualstudio/<progetto>/...`
- [ ] Visualizzazione del contenuto di una chat (best-effort: formato `.vs`
      non documentato, dipende da quanto emerge in Fase 0 — `discover`)
- [ ] Editor del titolo della chat (il nome mostrato nel pannello Chat
      History di Visual Studio), se il formato lo espone
- [ ] Eliminazione di una chat sincronizzata
- [ ] Stessa regola delle altre scritture: mai con `devenv.exe` in
      esecuzione, backup prima di sovrascrivere

## Fase 4ter — Prompt Library

- [ ] Elenco prompt personali (`.codechatsync/prompts/*.prompt.md`),
      sincronizzati tra i PC come le chat
- [ ] Editor di contenuto del prompt
- [ ] Editor del campo front-matter `name` (nome visualizzato dopo `/`
      nell'editor, distinto dal nome del file)
- [ ] Azione "Distribuisci nel progetto": copia manuale in
      `.github/prompts/` del repo cliente selezionato (unico punto del tool
      che scrive dentro un repo cliente — vedi vincolo in
      `docs/ARCHITECTURE.md`)
- [ ] Aggiunta automatica del prompt distribuito a `.git/info/exclude` del
      repo cliente (default: non versionato; condivisione col team resta
      una scelta esplicita dell'utente)

## Fase 5 — Provider successivi (dopo che Visual Studio funziona bene)

- [ ] Provider Claude Code (`~/.claude/projects/`, rimappatura percorsi)
- [ ] Provider Copilot CLI (`~/.copilot`)
- [ ] Valutare provider VS Code (probabilmente non necessario, sync nativa
      già disponibile)

## Decisioni prese (per riferimento, non ridiscutere senza motivo)

- Nome progetto/comando: **CodeChatSync** / `codechatsync`
- Niente symlink: solo copia file
- Identificazione progetto: remote Git, non percorso locale
- Sync: manuale + automatico alla chiusura del tool (non periodico, non al
  login/spegnimento)
- Storage: repo Git privato dell'utente (non un servizio gestito da noi)
- UI: **app WinUI 3** in tray, Windows-only (Blazor Server, MAUI e Photino
  scartati). Il watch vive nello stesso processo dell'app, non in un comando
  CLI o in una Scheduled Task separata.
- `CodeChatSync.Cli` resta come strumento leggero separato per uso da
  terminale/scripting (`add`, `sync`, `discover`); convive con `App`, non lo
  sostituisce, e non gestisce il watch.
- Distribuzione: nessuna decisa. Store (MSIX) fattibile senza stravolgere
  l'architettura; per uso solo personale, Scoop è l'opzione più semplice
  (bucket privato, nessuna pubblicazione).
- Prompt Library: libreria personale di prompt file sincronizzata come le
  chat; per funzionare in un progetto cliente va comunque copiata dentro
  `.github/prompts/` di quel repo (unico caso in cui il tool scrive
  deliberatamente in un repo cliente), con esclusione di default da git
  tramite `.git/info/exclude`.
- Chat Library: vedere/editare le chat sincronizzate ha priorità sulla
  Prompt Library nella roadmap (Fase 4bis prima di Fase 4ter).
- Stack: **.NET 10** su tutti i progetti, fissato via `global.json`; ultima
  versione stabile disponibile per ogni dipendenza (WinUI 3/Windows App SDK
  incluso), non versioni precedenti "di comodo".
