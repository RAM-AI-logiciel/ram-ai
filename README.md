# RAM-AI — Memory Intelligence

> Outil d'optimisation mémoire Windows en deux composants : Phase3 (service background C# .NET 10) et Phase4 (dashboard WPF temps réel).
> Utilise l'API Windows `SetProcessWorkingSetSizeEx()` pour libérer la mémoire des processus froids sans tuer aucun processus.

---

## Architecture (2 composants actifs)

**Phase3** — Service Windows background (C# .NET 10)
- Méthode : `SetProcessWorkingSetSizeEx()` — libère le working set des processus froids (inactifs +30s, WS stable <10%)
- Aucun kill de processus — ils restent actifs, seule leur mémoire physique est restituée à l'OS
- Processus protégés : `chrome`, `firefox`, `msedge`, `clipstudio`, `explorer`, `dwm` + processus système Windows + RAM-AI lui-même

**Phase4** — Dashboard WPF temps réel (C# .NET 10, LiveCharts2)
- 6 KPIs : RAM libérée · Processus optimisés · Réduction swap · Latence · CPU moyen · Mode actif
- 2 graphiques temps réel · Bannière gaming · Tray icon · Licence SHA-256

---

## Modes

| Mode | Intervalle | Déclenchement |
|------|-----------|---------------|
| **Adaptatif** (défaut) | 3 000 ms repos / 2 000 ms charge | Automatique selon pression mémoire |
| **Gaming** | 2 000 ms, thread BelowNormal | Détection 50+ jeux + fallback WS >1 Go |
| **Tournoi** (Ultra) | 500 ms, seuil 25% | Manuel — licence Ultra requise |
| **Turbo** | One-shot immédiat | Bouton manuel |
| **Éco** | 3 000 ms, max 8 procs/cycle | Détection batterie automatique |

---

## Seuils dynamiques (basés sur RAM totale installée)

| RAM totale | Seuil déclenchement | coldProcess |
|-----------|---------------------|-------------|
| 8 Go | 1,6 Go | 20 s |
| 16 Go | 3,2 Go | 30 s |
| 32 Go | 6,4 Go | 45 s |

---

## Benchmarks mesurés (reproductibles)

| Métrique | Résultat |
|----------|---------|
| RAM disponible supplémentaire | +7% à +35% |
| Réduction swap | 77% à 94% |
| CPU RAM-AI moyen | 0,46% |
| CPU RAM-AI maximum | 1,41% |
| Technologie | `SetProcessWorkingSetSizeEx()` API Windows native |

---

## Licences

| Palier | Format clé | Durée / Prix | Fonctionnalités |
|--------|-----------|--------------|-----------------|
| **Beta** | `BETA-XXXX-XXXX-XXXX-XXXX` | 30 jours gratuit | Tous modes sauf Tournoi |
| **Pro** | `P-XXXX-XXXX` | 24,99€ one-time | Adaptatif, Gaming, Turbo, Éco, dashboard |
| **Ultra** | `ULT-XXXX-XXXX-XXXX-XXXX` | Prix à définir | Pro + Tournoi + VRAM + prédictif + profils gaming |

Validation SHA-256 locale — aucun appel réseau.
Stockage : `HKCU\Software\RAM-AI\License`

---

## Build & Publish

### Phase 3 — Service Windows

```powershell
cd C:\projettoto\RAM-AI\Phase3
dotnet publish Phase3.csproj -c Release -r win-x64 --self-contained false
.\bin\Release\net10.0-windows\win-x64\RamAI.Phase3.exe --install
sc start RamAI-Phase3
```

### Phase 4 — Dashboard WPF

```powershell
cd C:\projettoto\RAM-AI\Phase4
dotnet publish Phase4.csproj -c Release -r win-x64 --self-contained false
iscc setup.iss
```

---

## Dépendances NuGet

| Projet | Package | Version |
|--------|---------|---------|
| Phase 3 | `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.0 |
| Phase 4 | `LiveChartsCore.SkiaSharpView.WPF` | 2.0.0-rc4.5 |
| Phase 4 | `CommunityToolkit.Mvvm` | 8.3.2 |

---

## Problèmes connus & solutions

| Problème | Solution |
|----------|---------|
| Mode Gaming reste actif après fermeture du jeu | Critère >1 Go désactivé si `gaming_mode.force` est absent |
| KPIs affichent 0 au démarrage | `LogWatcherService` : Seek d'abord, StreamReader ensuite |
| `Memory Compression` déclenche Gaming | Ajouté à `SystemProcesses` dans `MemoryOrchestrator.cs` |
| `MSB3027: fichier verrouillé` | Fermer le dashboard avant de builder Phase4 |

---

*RAM-AI v1.0.0 — 2026-06-11*
