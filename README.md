# RAM-AI — Memory Intelligence

> Outil d'optimisation mémoire Windows en deux composants : Phase3 (service background C# .NET 10)
> et Phase4 (dashboard WPF temps réel).
> Utilise l'API Windows `SetProcessWorkingSetSizeEx()` pour libérer la mémoire des processus froids
> sans tuer aucun processus — anticipation de la pression mémoire, pas réaction.

---

## Table des matières

1. [Architecture des 4 phases](#architecture)
2. [Arborescence des fichiers](#arborescence)
3. [Commandes Build & Publish](#build)
4. [Clés de licence de démo](#licences)
5. [Fonctionnalités](#fonctionnalites)
6. [Problèmes connus & solutions](#problemes)

---

## Architecture des 4 phases <a name="architecture"></a>

```
Phase 1  →  Phase 2  →  Phase 3  →  Phase 4
Capture      ML.NET       Service      Dashboard
mémoire      entraîne     Windows      WPF
processus    le modèle    applique     visualise
toutes 2s    FastTree     prédictions  temps réel
```

### Phase 1 — Moniteur mémoire console

- **Rôle** : capturer les métriques de chaque processus toutes les 2 secondes
- **Technologie** : C# .NET 10, `System.Diagnostics.Process`
- **Sortie** : `data\patterns.json` (rolling 10 000 lignes, format NDJSON)
- **Métriques** : `pid`, `name`, `workingSetBytes`, `privateBytes`, `pageFaultDelta`
- **Affichage** : tableau console rafraîchi via `Console.SetCursorPosition`

### Phase 2 — Entraînement ML.NET

- **Rôle** : entraîner un modèle FastTree sur les patterns de Phase 1
- **Technologie** : C# .NET 10, ML.NET 3.0.1 (`Microsoft.ML.FastTree`)
- **Label** : `pageFaultDelta(t+1) > 0` → classification binaire
- **Features** : `WorkingSetMB`, `PrivateBytesMB`, `PageFaultDeltaKB`, `WsToPrivateRatio`, `LogWorkingSet`
- **Split** : 80 % train / 20 % test, seed=42
- **Métriques affichées** : Accuracy, AUC-ROC, F1, Précision, Rappel, MAE, matrice de confusion
- **Sortie** : `model\ram-ai.zip`

### Phase 3 — Service Windows

- **Rôle** : optimiser la mémoire en continu via l'API Windows native
- **Technologie** : C# .NET 10, `Microsoft.Extensions.Hosting.WindowsServices`
- **Méthode** : `SetProcessWorkingSetSizeEx()` — libère le working set des processus froids (inactifs +30s, WS stable <10% variation) ; les processus restent actifs
- **Intervalles par mode** :
  - Adaptatif (défaut) : 3 000 ms repos / 2 000 ms sous charge
  - Gaming : détection automatique 50+ jeux + fallback >1 Go WS, thread BelowNormal
  - Tournoi (Ultra) : 500 ms, seuil 25%, optimisation maximale
  - Turbo : one-shot manuel, libération immédiate
  - Éco : détection batterie, 3 000 ms, max 8 procs/cycle
- **Processus protégés** : processus système Windows + chrome, firefox, msedge, clipstudio, explorer, dwm + RAM-AI lui-même
- **Seuils dynamiques** selon RAM totale :
  - 8 Go : seuil déclenchement 1,6 Go, coldProcess 20 s
  - 16 Go : seuil 3,2 Go, coldProcess 30 s
  - 32 Go : seuil 6,4 Go, coldProcess 45 s
- **Log** : `logs\events.log` (NDJSON, `FileShare.ReadWrite`)
- **Installation** : `RamAI.Phase3.exe --install` / `--uninstall`

### Phase 4 — Dashboard WPF

- **Rôle** : visualiser les métriques en temps réel, gérer la licence et le Mode Gaming
- **Technologie** : C# .NET 10, WPF, LiveCharts2 (`2.0.0-rc4.5`), CommunityToolkit.Mvvm
- **Tray icon** : `System.Windows.Forms.NotifyIcon` (démarrer / arrêter / ouvrir / quitter)
- **Graphiques** : 2 `CartesianChart` LiveCharts2 (latence ms + Mo économisés, 60 derniers ticks)
- **Licence** : validation SHA-256 locale, stockage `HKCU\Software\RAM-AI\License`
- **Installeur** : Inno Setup 6.1, `installer\RAM-AI-Setup-1.0.0.exe`

---

## Arborescence des fichiers <a name="arborescence"></a>

```
C:\projettoto\RAM-AI\
│
├── phase1\phase1\                        Phase 1 — Moniteur console
│   ├── phase1.csproj                     net10.0-windows
│   ├── NativeMethods.cs                  P/Invoke kernel32 + psapi (référence uniquement)
│   ├── ProcessSnapshot.cs                DTO JSON + PatternStore
│   ├── ProcessCollector.cs               Collecte + persistance patterns.json
│   └── Program.cs                        Boucle 2s + affichage console
│
├── Phase2\                               Phase 2 — Entraînement ML.NET
│   ├── Phase2.csproj                     net10.0-windows, Microsoft.ML 3.0.1
│   ├── Program.cs                        Entrée : load → train → évaluer → inférence
│   ├── Data\
│   │   └── ProcessRecord.cs              RawSnapshot · ProcessTrainingRow · ProcessPrediction
│   ├── ML\
│   │   ├── DataLoader.cs                 Lit patterns.json, construit paires (features@t, label@t+1)
│   │   ├── ModelTrainer.cs               Pipeline ML.NET + évaluation + sauvegarde
│   │   └── ModelPredictor.cs             Predict(RawSnapshot[]) → ProcessPrediction[]
│   └── model\
│       └── ram-ai.zip                    ← modèle entraîné (généré à l'exécution)
│
├── Phase3\                               Phase 3 — Service Windows
│   ├── Phase3.csproj                     net10.0-windows, WindowsService=true
│   ├── Program.cs                        Host + --install / --uninstall
│   ├── appsettings.json                  ModelPath · CachePath · LogPath
│   ├── install.bat                       Build → register → start (admin requis)
│   ├── gaming_mode.force                 ← créé dynamiquement ("auto" ou "manual")
│   ├── Memory\
│   │   ├── NativeMemory.cs               P/Invoke : OpenProcess, SetProcessWorkingSetSizeEx
│   │   └── MemoryOrchestrator.cs         Boucle tick, détection mode, optimisation processus froids
│   ├── Service\
│   │   └── RamAiService.cs               BackgroundService → SCM
│   ├── Logging\
│   │   └── EventLogger.cs                NDJSON → events.log (FileShare.ReadWrite)
│   ├── cache\
│   │   └── ram-ai.cache                  ← généré à l'exécution
│   └── logs\
│       └── events.log                    ← généré à l'exécution
│
├── Phase4\                               Phase 4 — Dashboard WPF
│   ├── Phase4.csproj                     net10.0-windows, WPF + WinForms
│   ├── App.xaml / App.xaml.cs            Startup · NotifyIcon · ShutdownMode=OnExplicitShutdown
│   ├── Assets\
│   │   └── RAM-AI.ico                    ← placer votre icône ici
│   ├── Models\
│   │   ├── EventEntry.cs                 DTO log Phase3 (JSON camelCase)
│   │   └── LicenseInfo.cs                Enum LicenseTier + limites cache
│   ├── Services\
│   │   ├── LicenseService.cs             Validation SHA-256 + registre HKCU
│   │   ├── LogWatcherService.cs          Poll 2s + lecture incrémentielle events.log
│   │   └── NotifyIconService.cs          WinForms NotifyIcon
│   ├── ViewModels\
│   │   ├── MainViewModel.cs              KPIs · graphiques · Mode Gaming · auto-start Phase3
│   │   └── LicenseViewModel.cs           Saisie clé · validation · paliers
│   ├── Views\
│   │   ├── MainWindow.xaml/.cs           Dashboard dark · 6 KPIs · 2 charts · bannière gaming
│   │   └── LicenseWindow.xaml/.cs        Formulaire licence · tableau comparatif
│   └── setup.iss                         Inno Setup 6.1 · .NET 10 auto-download
│
└── README.md                             ← ce fichier
```

---

## Commandes Build & Publish <a name="build"></a>

> Remplacer `dotnet` par le chemin complet si non dans le PATH :
> `"C:\Program Files\dotnet\dotnet.exe"`

### Phase 1 — Build

```powershell
cd C:\projettoto\RAM-AI\phase1\phase1
dotnet build phase1.csproj -c Release
dotnet run   phase1.csproj -c Release      # Ctrl+C pour arrêter
```

### Phase 2 — Entraîner le modèle

```powershell
cd C:\projettoto\RAM-AI\Phase2
dotnet run Phase2.csproj -c Release
# Argument optionnel : chemin vers un patterns.json alternatif
dotnet run Phase2.csproj -c Release -- "C:\autre\patterns.json"
```

Le modèle est sauvegardé dans `Phase2\model\ram-ai.zip`.

### Phase 3 — Service Windows

```powershell
# Build
cd C:\projettoto\RAM-AI\Phase3
dotnet build Phase3.csproj -c Release

# Publish (requis avant install)
dotnet publish Phase3.csproj -c Release -r win-x64 --self-contained false

# Installer le service (admin requis)
.\install.bat
# ou directement :
.\bin\Release\net10.0-windows\win-x64\RamAI.Phase3.exe --install

# Désinstaller
.\bin\Release\net10.0-windows\win-x64\RamAI.Phase3.exe --uninstall

# Contrôle SCM
sc start  RamAI-Phase3
sc stop   RamAI-Phase3
sc query  RamAI-Phase3

# Mode console (debug, sans SCM)
.\install.bat /console
```

### Phase 4 — Dashboard WPF

```powershell
cd C:\projettoto\RAM-AI\Phase4

# Build
dotnet build Phase4.csproj -c Release

# Lancer directement
dotnet run Phase4.csproj -c Release

# Publish (requis pour l'installeur)
dotnet publish Phase4.csproj -c Release -r win-x64 --self-contained false `
  -o "bin\Release\net10.0-windows\win-x64\publish"
```

### Générer l'installeur `.exe`

```powershell
# Prérequis : Inno Setup 6.1+ installé
# Publier les deux projets d'abord (voir ci-dessus + Phase3 publish)

cd C:\projettoto\RAM-AI\Phase4
iscc setup.iss
# Sortie : installer\RAM-AI-Setup-1.0.0.exe
```

---

## Clés de licence de démo <a name="licences"></a>

| Palier | Format de clé | Durée / Prix | Fonctionnalités |
|--------|---------------|--------------|-----------------|
| **Beta** | `BETA-XXXX-XXXX-XXXX-XXXX` | 30 jours gratuit | Tous modes sauf Tournoi |
| **Pro** | `P-XXXX-XXXX` | 24,99€ one-time | Adaptatif, Gaming, Turbo, Éco, dashboard |
| **Ultra** | `ULT-XXXX-XXXX-XXXX-XXXX` | Prix à définir | Pro + Tournoi + prédictif + VRAM + profils gaming |

**Format des clés** :
- Beta : `BETA-XXXX-XXXX-XXXX-XXXX`
- Pro : `P-XXXX-XXXX`
- Ultra : `ULT-XXXX-XXXX-XXXX-XXXX`

**Algorithme de validation** : `SHA-256(key + "RAM-AI-2026")[0..2] mod 251 == 0`

**Stockage** : `HKCU\Software\RAM-AI\License` → valeur `Key`

---

## Fonctionnalités <a name="fonctionnalites"></a>

### Optimisation mémoire (Phase 3)

| Fonctionnalité | Détail |
|----------------|--------|
| **API Windows native** | `SetProcessWorkingSetSizeEx()` — libère le working set des processus froids |
| **Ciblage processus froids** | Inactifs depuis +30 s (selon RAM totale), WorkingSet stable <10% variation |
| **Aucun kill de processus** | Les processus restent actifs, juste leur mémoire physique est restituée à l'OS |
| **Seuils dynamiques** | Basés sur la RAM totale installée : 8/16/32 Go → seuils proportionnels |
| **Résultats mesurés** | +7% à +35% RAM disponible supplémentaire · Réduction swap 77–94% · CPU : 0,46% moy / 1,41% max |

### Benchmarks mesurés (reproductibles)

| Métrique | Résultat |
|----------|---------|
| RAM disponible supplémentaire | +7% à +35% |
| Réduction swap | 77% à 94% |
| CPU RAM-AI moyen | 0,46% |
| CPU RAM-AI maximum | 1,41% |
| Technologie | `SetProcessWorkingSetSizeEx()` API Windows native |

### Processus protégés (jamais optimisés)

- Processus système Windows (SYSTEM, services critiques)
- Processus UI/graphiques : `chrome`, `firefox`, `msedge`, `clipstudio`, `explorer`, `dwm`
- RAM-AI lui-même (Phase3 et Phase4)

### Mode Gaming

Le Mode Gaming est déclenché automatiquement dès qu'un jeu est détecté.

**Détection** (par ordre de priorité) :
1. Fichier `gaming_mode.force` contenant `"manual"` (bouton dashboard)
2. Processus connu par nom : `cs2`, `valorant`, `fortnite`, `minecraft`, `roblox`,
   `eldenring`, `cyberpunk2077`, `gta5`, `dota2`, `leagueoflegends`, `steam`,
   `epicgameslauncher`, `origin`, `uplay`, `battlenet`…
3. Processus inconnu avec WorkingSet > 1 Go **et** fichier `gaming_mode.force = "auto"` présent

**Comportement en Mode Gaming** :
- Intervalle tick : 3 000 ms → **2 000 ms** (thread BelowNormal pour ne pas gêner le jeu)
- Le processus du jeu est toujours **protégé**, jamais optimisé
- Détection automatique : 50+ jeux connus + fallback WorkingSet >1 Go
- Log : `GAMING MODE ON — cs2` / `GAMING MODE OFF`

**Désactivation automatique** :
- Si le jeu se ferme et que le flag est `"auto"` → flag supprimé, mode désactivé
- Si le flag est `"manual"` → reste actif jusqu'à ce que l'utilisateur clique à nouveau

**Fichier flag** : `Phase3\gaming_mode.force`
- `"auto"` = déclenché par détection automatique
- `"manual"` = forcé depuis le dashboard (bouton `🎮 Forcer Gaming`)

### Système de licence

- Validation SHA-256 locale (aucun appel réseau)
- Persistance dans le registre Windows : `HKCU\Software\RAM-AI\License`
- Si licence enregistrée au démarrage → écran de licence sauté automatiquement
- Bouton `🔑 Changer de licence` dans le header du dashboard

### Démarrage automatique

- Option cochée par défaut dans l'installeur
- Clé registre : `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `RAM-AI`
- Le dashboard Phase4 vérifie et démarre Phase3 automatiquement au lancement :
  - Service `RamAI-Phase3` installé mais arrêté → `sc start` sans UAC
  - Service non installé → cherche `RamAI.Phase3.exe`, lance `--install` avec UAC

### Dashboard (Phase 4)

- **6 KPIs** : RAM libérée · Processus optimisés · Réduction swap · Latence moy. · CPU moyen · Mode actif
- **2 graphiques temps réel** : Latence (ms) + Mo économisés sur 60 derniers ticks
- **Bannière gaming** : gradient rouge-orange avec pastille clignotante, nom du jeu détecté
- **Tray icon** : démarrer / arrêter / ouvrir / quitter
- **Status bar** : pastille colorée (vert = Actif · orange = transition · rouge = Arrêté)

---

## Problèmes connus & solutions <a name="problemes"></a>

### Build

| Erreur | Cause | Solution |
|--------|-------|----------|
| `MSB3027: fichier verrouillé` | Phase4 tourne pendant le build | Fermer le dashboard ou builder vers un autre dossier `-o /tmp/test` |
| `BG1002: impossible de trouver Assets\RAM-AI.ico` | Fichier `.ico` manquant | Placer `RAM-AI.ico` dans `Phase4\Assets\` |
| `NU1701: OpenTK 3.3.1` | Dépendance transitive LiveCharts2 | Déjà supprimé via `<NoWarn>NU1701</NoWarn>` |

### Service Phase 3

| Problème | Cause | Solution |
|----------|-------|----------|
| `0xc0000374` au démarrage | Était dû au P/Invoke `GetProcessMemoryInfo` (Phase 1) | Phase 3 utilise `System.Diagnostics.Process` — corrigé |
| `ram-ai.cache` verrouillé | `FileShare.Read` au lieu de `FileShare.ReadWrite` | Corrigé dans `PageCacheManager.cs` et `EventLogger.cs` |
| Mode Gaming reste actif après fermeture du jeu | Critère >1 Go maintenait `isGaming=true` même sans flag | Critère >1 Go désactivé si `gaming_mode.force` est absent |
| Mode Gaming ne se désactive pas après suppression du fichier | Chemin codé en dur `C:\projettoto\` au lieu de `AppContext.BaseDirectory` | Corrigé — chemin dynamique dans Phase3 et Phase4 |

### Dashboard Phase 4

| Problème | Cause | Solution |
|----------|-------|----------|
| KPIs affichent 0 au démarrage | `StreamReader` créé avant `Seek()` → lisait depuis pos 0 | `LogWatcherService` : Seek d'abord, StreamReader ensuite |
| Bannière gaming ne s'allume pas | `_forceGamingMode = File.Exists(...)` assigne le champ et non la propriété | Utiliser `ForceGamingMode = true` + `IsGamingModeActive = true` |
| `Memory Compression` déclenche le Mode Gaming | WorkingSet > 1 Go du processus Windows | Ajouté à `SystemProcesses` dans `MemoryOrchestrator.cs` |
| `Schema mismatch for label column 'Label'` | `mlCtx.Regression.Evaluate` attend `Single` mais label est `Boolean` | Remplacé par calcul MAE manuel dans `ModelTrainer.cs` |
| `Could not find column 'Pid'` | `ProcessPrediction.Pid` sans `[NoColumn]` | Ajout de `[NoColumn]` sur `Pid` et `Name` dans `ProcessRecord.cs` |

### Installeur Inno Setup

| Erreur | Cause | Solution |
|--------|-------|----------|
| `Required parameter 'Filename' not specified` | Entrées `[Icons]` coupées sur plusieurs lignes | Chaque entrée Inno Setup doit tenir sur **une seule ligne** |
| `Required parameter 'Root' not specified` | Idem pour `[Registry]`, `[Files]`, `[Run]` | Même règle — tout sur une ligne |
| `Type mismatch` sur `ShellExec` | 7e argument `Result` de type `Integer` au lieu de variable déclarée | Déclarer `var ErrorCode: Integer` et l'utiliser comme 7e argument |
| `Invalid number of parameters` sur `ShellExec` | 6 arguments au lieu de 7 | `ShellExec` requiert exactement 7 paramètres en Inno Setup 6 |

---

## Dépendances NuGet

| Projet | Package | Version |
|--------|---------|---------|
| Phase 2 | `Microsoft.ML` | 3.0.1 |
| Phase 2 | `Microsoft.ML.FastTree` | 3.0.1 |
| Phase 3 | `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.0 |
| Phase 3 | `Microsoft.ML` | 3.0.1 |
| Phase 3 | `Microsoft.ML.FastTree` | 3.0.1 |
| Phase 4 | `LiveChartsCore.SkiaSharpView.WPF` | 2.0.0-rc4.5 |
| Phase 4 | `CommunityToolkit.Mvvm` | 8.3.2 |

---

## Fichiers générés à l'exécution

| Fichier | Créé par | Description |
|---------|----------|-------------|
| `phase1\data\patterns.json` | Phase 1 | Snapshots mémoire (rolling 10 000 lignes) |
| `Phase2\model\ram-ai.zip` | Phase 2 | Modèle FastTree sérialisé |
| `Phase3\logs\events.log` | Phase 3 | Métriques tick (NDJSON) |
| `Phase3\gaming_mode.force` | Phase 3 / Phase 4 | Flag Mode Gaming (`"auto"` ou `"manual"`) |
| `Phase4\installer\RAM-AI-Setup-1.0.0.exe` | Inno Setup | Installeur final |

---

*Généré le 2026-06-03 — RAM-AI v1.0.0*
