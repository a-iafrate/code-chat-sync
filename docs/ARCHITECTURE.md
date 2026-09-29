# Architettura — CodeChatSync

## Obiettivo

Sincronizzare le chat AI (Copilot, e in futuro altri tool) tra i PC dell'utente,
tenendole **fuori dai repo dei clienti**. Nessun dato del cliente deve finire
nel repo di sync; nessun file del tool di sync deve finire nei repo cliente.

## Principi di design

- **Niente symlink.** Il tool copia i file, punto. Push da locale a cartella di
  sync, pull da cartella di sync a locale. Niente collegamenti tra repo cliente
  e repo di sync.
- **Identificazione per remote Git, non per percorso.** Lo stesso progetto può
  vivere in `C:\Dev\...` su un PC e `D:\Clienti\...` su un altro: viene
  riconosciuto tramite il remote Git normalizzato, non il path su disco.
- **Provider a plugin.** Il Core non sa nulla di uno strumento specifico.
  Ogni tool (Visual Studio, in futuro Claude Code, Copilot CLI, VS Code) è un
  provider che implementa `IChatProvider`: dice dove sono le chat in locale,
  come mappare un percorso relativo di nuovo in locale su un altro PC, e quali
  processi indicano che il tool è "in uso" (per il lock in scrittura e per il
  trigger di `watch`).
- **Sync solo a tool chiuso.** Non si scrive mai mentre il processo del
  provider (es. `devenv.exe`) è in esecuzione, per non leggere/scrivere file
  in uso o corrotti.
- **Backup prima di sovrascrivere.** Ogni restore locale fa un backup del file
  esistente prima di sostituirlo.
- **Storage: repo Git privato dell'utente.** Nessun servizio di terze parti,
  nessun cloud gestito da noi. Il tool fa solo commit/push/pull su un repo che
  l'utente possiede.

## Stack tecnologico

- **.NET 10** su tutti i progetti della solution (`<TargetFramework>net10.0</TargetFramework>`,
  `net10.0-windows` per `CodeChatSync.App`). Fissato anche via `global.json`
  alla radice, per evitare che un PC con un SDK diverso installato usi una
  versione non allineata.
- **C# più recente supportato da .NET 10**, `Nullable` e `ImplicitUsings`
  abilitati su tutti i progetti.
- **WinUI 3 sull'ultima versione stabile del Windows App SDK** al momento
  dell'implementazione (non pinnare a una versione vecchia "perché
  funziona"): verificare la versione corrente prima di aggiungere il
  pacchetto.
- Ogni dipendenza NuGet (`System.CommandLine`, `H.NotifyIcon.WinUI`, il
  pacchetto Git scelto, ecc.) va presa **nell'ultima versione stabile
  disponibile** al momento in cui viene aggiunta, non da esempi o tutorial
  che potrebbero referenziare versioni precedenti.

## Struttura della solution

```
CodeChatSync.Core                      # config, provider registry, mapping remote→progetto,
                                        # confronto date, logica di merge/conflitti
CodeChatSync.Providers.VisualStudio    # discover/map per le chat Copilot in .vs
CodeChatSync.Git                       # commit/push/pull sul repo privato di sync
CodeChatSync.Cli                       # CLI leggera per uso da terminale/scripting:
                                        # add, sync, discover — convive con CodeChatSync.App,
                                        # non lo sostituisce
CodeChatSync.App                       # app WinUI 3: tray icon, finestra di configurazione,
                                        # e watch integrato nello stesso processo
                                        # (Windows-only, niente più Blazor/Kestrel)
```

Nota: `Core`, `Providers.VisualStudio` e `Git` restano librerie .NET
platform-agnostic. Solo `App` è legata a Windows (WinUI 3); se in futuro
servirà uso da terminale su Linux (es. per un provider Claude Code), resta
disponibile `Cli` senza GUI.

## Contratto provider

```csharp
public interface IChatProvider
{
    string Id { get; }                         // "visualstudio", "claudecode", ...
    IReadOnlyList<string> ProcessNames { get; } // per watch e per il lock: "devenv"
    IEnumerable<ChatLocation> Discover(ProjectInfo project); // dove sono le chat in locale
    string MapToLocal(ProjectInfo project, string relativePath); // per il restore
}
```

`ProjectInfo` contiene l'identità del progetto (remote Git normalizzato) più
il percorso locale corrente. Il Core si occupa di copia, confronto delle
date, backup e commit/push/pull. Il provider si limita a dire dove guardare.

## Layout della cartella di sync

```
.codechatsync/
  <provider>/<progetto-da-remote>/...   # es. visualstudio/clienteA-gestionale/
```

## Config

- Config condivisa (nel repo di sync, versionata): mapping `remote Git → nome
  progetto/cliente`.
- Config locale per PC (non versionata): percorso locale di ogni progetto
  registrato su quella macchina.

## Provider pianificati (dopo Visual Studio)

- **Claude Code** — sessioni in `~/.claude/projects/`, cartelle codificano il
  percorso assoluto: il provider deve rinominarle sul PC di destinazione.
- **Copilot CLI** — dati in `~/.copilot`, per sessione.
- **VS Code** — chat indicizzate per hash del workspace; con la sync nativa
  di VS Code su GitHub probabilmente non serve un provider dedicato.

Le chat di claude.ai restano fuori: sono già legate all'account utente.

## UI di configurazione

App **WinUI 3** (`CodeChatSync.App`), Windows-only: icona in tray (tramite
`H.NotifyIcon.WinUI`, non c'è un'API nativa WinUI per il tray) più finestra
di configurazione aperta dal tray. L'app resta in esecuzione in background e
integra anche il watch nello stesso processo: non c'è più un comando
`watch` separato da lanciare o pianificare.

Scelta al posto di Blazor Server: si perde il cross-platform (Linux/macOS),
ma si guadagna un unico processo per tray + watch + configurazione, avvio
automatico gestito nativamente da Windows, e un packaging MSIX più naturale
se in futuro si vorrà distribuire su Microsoft Store (WinUI 3 + MSIX è la
combinazione prevista da Microsoft, a differenza di Blazor+Kestrel).

Funzionalità minime della finestra di configurazione:

- Remote Git del repo di sync + cartella locale
- Elenco progetti registrati (aggiungi/rimuovi), con remote rilevato,
  percorso locale, ultimo sync
- Sync ora (globale e per singolo progetto)
- Toggle per il sync automatico alla chiusura di Visual Studio
- Elenco conflitti (stesso file cambiato su due PC) con scelta
  "tieni locale / tieni remoto"
- Log delle ultime sincronizzazioni

`CodeChatSync.Cli` resta come strumento leggero separato per chi preferisce
lanciare `add`/`sync`/`discover` da terminale o da script, senza passare
dalla UI. Non gestisce il watch: quello vive solo nell'app WinUI.

## Chat Library

Come la Prompt Library, ma per le chat già sincronizzate dal tool — non
richiede un'azione di "distribuzione", perché le chat vivono già in
`.codechatsync/<provider>/<progetto>/...` e nel percorso locale mappato dal
provider (`MapToLocal`).

Funzionalità:

- Elenco delle chat sincronizzate, per progetto
- Visualizzazione del contenuto (**best-effort**: il formato con cui Visual
  Studio salva le chat in `.vs` non è documentato — vedi Fase 0, `discover`
  — quindi la profondità di parsing possibile dipende da cosa emerge lì)
- Modifica del titolo della chat, cioè il nome mostrato nel pannello Chat
  History di Visual Studio, se il formato espone un campo identificabile
  come tale
- Eliminazione di una chat sincronizzata
- Stesse regole di scrittura delle altre funzionalità: mai con `devenv.exe`
  in esecuzione, backup prima di sovrascrivere

## Prompt Library

Funzionalità per vedere/modificare i prompt file di Copilot (`.prompt.md`) e
il loro nome visualizzato nell'editor (campo front-matter `name`, distinto
dal nome del file: se assente, Copilot usa il nome del file dopo `/`).

**Vincolo tecnico che guida il design:** sia Visual Studio che VS Code
leggono i prompt file solo da `.github/prompts` **dentro il repo aperto**.
VS Code supporta anche uno scope "utente" globale fuori dai repo; Visual
Studio normale, allo stato attuale, no. Quindi un prompt personale, per
funzionare in un progetto cliente aperto in VS, deve fisicamente stare
dentro `.github/prompts` di quel repo — al contrario di tutto il resto del
tool, che tiene i materiali dell'utente fuori dai repo cliente.

Design:

- **Libreria personale**, sincronizzata tra i PC come le chat:
  `.codechatsync/prompts/<slug>.prompt.md` nel repo di sync. UI nell'app:
  elenco, editor di contenuto, editor del campo `name`.
- **"Distribuisci nel progetto"**: azione esplicita ed esclusivamente
  manuale (mai automatica) che copia i prompt scelti in `.github/prompts/`
  del repo cliente selezionato. È l'unico punto del tool in cui si scrive
  deliberatamente dentro un repo cliente, per il vincolo tecnico sopra.
- Per default, dopo la distribuzione il percorso del prompt viene aggiunto a
  `.git/info/exclude` del repo cliente, così resta utilizzabile in VS ma non
  compare nel git status/commit di quel repo. Condividerlo con il team del
  cliente resta possibile ma è una scelta esplicita dell'utente, non il
  comportamento di default.

## Come rileva la chiusura di Visual Studio

L'app WinUI si sottoscrive agli eventi WMI di terminazione processo filtrati
su `devenv.exe`. Alla chiusura dell'ultima istanza, attende qualche secondo
per il rilascio dei file, poi lancia il sync.

## Avvio automatico

Gestito con la *StartupTask extension* di Windows/MSIX invece che con una
Scheduled Task creata manualmente. Vantaggio pratico: viene rimossa in modo
pulito dal sistema alla disinstallazione dell'app, cosa che una Scheduled
Task creata a mano non garantisce.

## Distribuzione (valutata, non ancora decisa/implementata)

- **Microsoft Store (MSIX):** fattibile con WinUI 3 senza stravolgere
  l'architettura. Nodo aperto: `broadFileSystemAccess` da giustificare in
  certificazione, perché le solution possono stare ovunque su disco.
- **Winget/Scoop/Chocolatey:** per un tool "solo tra i miei PC" senza
  pubblicazione, **Scoop** è l'opzione con meno attrito — basta un bucket
  privato (repo Git con manifest JSON), nessuna revisione, nessun server.
- In tutti i casi, nessun canale disinstalla da solo elementi creati fuori
  dalla cartella dell'app: con l'avvio via StartupTask MSIX questo non è più
  un problema (vedi sopra); se in futuro si torna a una Scheduled Task
  manuale, va previsto un comando esplicito di pulizia.

## Riferimento di progetti simili (non usati come dipendenza, solo ispirazione)

- [AI Chat Sync](https://github.com/fxwl/AI-Chat-Sync) — stesso concetto
  (sync chat AI tra PC via repo Git privato dell'utente), ambito Codex/Windows,
  Electron+React. Utile come riferimento per il modello di sicurezza
  (repo di sync separato da quello sorgente, mapping progetti, gestione
  conflitti senza sovrascritture silenziose).
