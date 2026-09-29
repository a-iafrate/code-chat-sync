# CodeChatSync — istruzioni per Claude

Tool CLI .NET per sincronizzare le chat AI (inizialmente Copilot in Visual
Studio, in futuro altri tool) tra i PC dell'utente, tenendole fuori dai repo
Git dei clienti. Il proprietario lavora su progetti di clienti in repo
separati, su più PC.

## Prima di lavorare sul codice

Leggi sempre:

- `docs/ARCHITECTURE.md` — design, struttura della solution, contratto dei
  provider, layout della cartella di sync
- `docs/ROADMAP.md` — stato attuale, fase in corso, decisioni già prese

Non riproporre alternative già scartate in `docs/ROADMAP.md` (es. symlink,
identificazione per percorso locale, Blazor/MAUI/Photino per la UI) senza
che l'utente lo chieda esplicitamente: sono decisioni già discusse e chiuse.

## Vincoli non negoziabili

- **Target .NET 10 su tutti i progetti.** Non proporre .NET 8/9, .NET
  Standard o .NET Framework, nemmeno come esempio "per compatibilità": il
  `global.json` alla radice fissa l'SDK a .NET 10 e va rispettato.
- **Ultima versione stabile per ogni dipendenza** (WinUI 3/Windows App SDK,
  `System.CommandLine`, pacchetto Git, ecc.) al momento in cui viene
  aggiunta. Se un esempio o uno snippet di riferimento usa una versione più
  vecchia, verifica prima l'ultima disponibile invece di copiarla.
- **Niente symlink.** Solo copia di file tra locale e cartella di sync.
- **Niente dato del cliente nel repo di sync, niente file del tool nei repo
  cliente.** Qualunque funzionalità che rischi di mescolare i due va segnalata
  prima di essere implementata.
- **Scrittura solo a tool chiuso** (es. mai mentre `devenv.exe` è in
  esecuzione). Backup prima di ogni sovrascrittura locale.
- **Identificazione progetto per remote Git**, mai per percorso assoluto su
  disco.
- Il formato con cui Visual Studio salva le chat in `.vs` **non è
  documentato**: trattalo come best-effort, isola questa logica nel provider
  `CodeChatSync.Providers.VisualStudio`, e non assumere stabilità tra versioni
  di VS.

## Stile di lavoro con l'utente

- L'utente è .NET/Azure, MVP Microsoft, lavora prevalentemente in italiano.
  Rispondi in italiano.
- Preferisce iterazioni brevi e mirate: proponi un passo concreto alla volta
  (vedi la fase corrente in `docs/ROADMAP.md`), non tutto il progetto in un
  colpo solo.
- Prima di introdurre una nuova dipendenza NuGet, chiedi conferma se non è
  ovvia dal contesto già discusso.

## Solution

```
CodeChatSync.Core                      # config, provider registry, merge/conflitti
CodeChatSync.Providers.VisualStudio    # discover/map per le chat Copilot in .vs
CodeChatSync.Git                       # commit/push/pull sul repo privato di sync
CodeChatSync.Cli                       # CLI leggera: add, sync, discover
CodeChatSync.App                       # app WinUI 3: tray, watch integrato, configurazione
                                        # (Windows-only)
```

`Core`, `Providers.VisualStudio` e `Git` restano platform-agnostic; solo
`App` è legata a Windows/WinUI 3. Dettagli completi in
`docs/ARCHITECTURE.md`.
