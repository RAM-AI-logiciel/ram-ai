# =============================================================================
# RAM-AI - Benchmark modulaire 4 phases
# Phase 1 : Sans RAM-AI      (toujours, 3 min, reference)
# Phase 2 : Mode Automatique (toujours, 3 min)
# Phase 3 : Mode Gaming      (optionnel -IncludeGaming)
# Phase 4 : Mode Tournoi     (optionnel -IncludeTournoi, Ultra uniquement)
# Compatible PowerShell 5.1 -- pas de ternaire, pas de Join-String
# =============================================================================

[CmdletBinding()]
param()

Set-StrictMode -Version 2

# -- Verification droits administrateur ----------------------------------------

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host ""
    Write-Host "ERREUR : Ce benchmark doit etre lance en tant qu'Administrateur." -ForegroundColor Red
    Write-Host ""
    Write-Host "Comment faire :" -ForegroundColor Yellow
    Write-Host "  1. Clic droit sur le raccourci RAM-AI Benchmark" -ForegroundColor White
    Write-Host "  2. Choisir 'Executer en tant qu'administrateur'" -ForegroundColor White
    Write-Host ""
    Write-Host "Appuyez sur ENTREE pour quitter..." -ForegroundColor Gray
    Read-Host
    exit 1
}

# -- Configuration -------------------------------------------------------------

$OutputDir   = "C:\ProgramData\RAM-AI"
$OutputFile  = Join-Path $OutputDir "benchmark_result.html"
$DurationSec = 180
$IntervalSec = 10
$TotalPoints = [int]($DurationSec / $IntervalSec)   # 18 points par phase

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
if (Test-Path $OutputFile) { Remove-Item $OutputFile -Force }

# -- Compteur Swap -------------------------------------------------------------

$script:swapCounter   = $null
$script:swapAvailable = $false

try {
    $script:swapCounter = New-Object System.Diagnostics.PerformanceCounter("Memory", "Pages/sec", $true)
    $null = $script:swapCounter.NextValue()
    Start-Sleep -Milliseconds 600
    $script:swapAvailable = $true
} catch {
    Write-Warning "  Compteur swap inaccessible -- Pages/sec sera 0."
}

# -- Compteurs CPU -------------------------------------------------------------

$script:cpuCounter        = $null
$script:cpuRamAiCounter   = $null
$script:cpuAvailable      = $false
$script:cpuRamAiAvailable = $false

try {
    $script:cpuCounter = New-Object System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total")
    $script:cpuCounter.NextValue() | Out-Null
    $script:cpuAvailable = $true
} catch {
    Write-Warning "  Compteur CPU systeme inaccessible."
}

try {
    $script:cpuRamAiCounter = New-Object System.Diagnostics.PerformanceCounter("Process", "% Processor Time", "RamAI.Phase3")
    $script:cpuRamAiCounter.NextValue() | Out-Null
    $script:cpuRamAiAvailable = $true
} catch {
    Write-Warning "  Compteur CPU RAM-AI inaccessible (service arrete ?)."
}

if ($script:cpuAvailable -or $script:cpuRamAiAvailable) {
    Start-Sleep -Milliseconds 500
    if ($script:cpuAvailable)      { $script:cpuCounter.NextValue()      | Out-Null }
    if ($script:cpuRamAiAvailable) { $script:cpuRamAiCounter.NextValue() | Out-Null }
}

# -- Fonctions utilitaires -----------------------------------------------------

function Get-Median {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { return 0.0 }
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return $sorted[($n - 1) / 2] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2.0
}

function Get-P95 {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { return 0.0 }
    $sorted = $Values | Sort-Object
    $idx = [math]::Ceiling($sorted.Count * 0.95) - 1
    if ($idx -lt 0) { $idx = 0 }
    return $sorted[$idx]
}

function Get-RamAvailableGb {
    $cs = Get-CimInstance -ClassName Win32_OperatingSystem
    $mb = [math]::Round($cs.FreePhysicalMemory / 1024, 0)
    return [math]::Round($mb / 1024, 2)
}

function Get-SwapPagesPerSec {
    if (-not $script:swapAvailable) { return 0.0 }
    try { return [math]::Round($script:swapCounter.NextValue(), 1) } catch { return 0.0 }
}

function Get-CpuPercent {
    if (-not $script:cpuAvailable) { return 0.0 }
    try   { return [math]::Round($script:cpuCounter.NextValue(), 1) }
    catch { return 0.0 }
}

function Get-CpuRamAiPercent {
    if (-not $script:cpuRamAiAvailable) { return 0.0 }
    try {
        $raw = $script:cpuRamAiCounter.NextValue()
        return [math]::Round($raw / [Environment]::ProcessorCount, 2)
    } catch { return 0.0 }
}

function Get-RamAiCorrectionGb {
    $wsBytes = 0L
    try {
        $p3 = Get-Process -Name "RamAI.Phase3" -ErrorAction SilentlyContinue
        $p4 = Get-Process -Name "RamAI.Phase4" -ErrorAction SilentlyContinue
        if ($p3) { foreach ($p in $p3) { $wsBytes += $p.WorkingSet64 } }
        if ($p4) { foreach ($p in $p4) { $wsBytes += $p.WorkingSet64 } }
    } catch {}
    return [math]::Round($wsBytes / 1GB, 2)
}

function Write-Header {
    Clear-Host
    Write-Host ""
    Write-Host "  ########################################################" -ForegroundColor Cyan
    Write-Host "  #                                                      #" -ForegroundColor Cyan
    Write-Host "  #     RAM-AI  --  Benchmark modulaire 4 phases         #" -ForegroundColor Cyan
    Write-Host "  #     Smart Memory Optimizer                           #" -ForegroundColor Cyan
    Write-Host "  #                                                      #" -ForegroundColor Cyan
    Write-Host "  ########################################################" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Phase {
    param([string]$Title, [string]$Color)
    Write-Host ""
    Write-Host "  -------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host "  -------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
}

function Write-ProgressBar {
    param([int]$Done, [int]$Total, [string]$Label, [string]$Suffix, [string]$Color)
    $pct    = [math]::Round(($Done / $Total) * 100)
    $filled = [math]::Round($pct / 4)
    $empty  = 25 - $filled
    $bar    = "[" + ("=" * $filled) + (" " * $empty) + "]"
    Write-Host ("`r  $Label  $bar  $pct%   $Suffix  ") -NoNewline -ForegroundColor $Color
}

function Measure-Phase {
    param([string]$PhaseName, [string]$BarColor)

    $ramSamples      = @()
    $swapSamples     = @()
    $cpuSamples      = @()
    $cpuRamAiSamples = @()

    $null = Get-SwapPagesPerSec
    $null = Get-CpuPercent
    $null = Get-CpuRamAiPercent
    Start-Sleep -Milliseconds 200

    for ($i = 0; $i -lt $TotalPoints; $i++) {
        $elapsed   = $i * $IntervalSec
        $remaining = $DurationSec - $elapsed

        $ram      = Get-RamAvailableGb
        $swap     = Get-SwapPagesPerSec
        $cpu      = Get-CpuPercent
        $cpuRamAi = Get-CpuRamAiPercent

        $ramSamples      += $ram
        $swapSamples     += $swap
        $cpuSamples      += $cpu
        $cpuRamAiSamples += $cpuRamAi

        $done = $i + 1
        $sfx  = "RAM:" + $ram + "Go  Swap:" + $swap + "p/s  CPU-AI:" + $cpuRamAi + "%  (" + $remaining + "s)"
        Write-ProgressBar -Done $done -Total $TotalPoints `
            -Label $PhaseName -Suffix $sfx -Color $BarColor

        if ($i -lt ($TotalPoints - 1)) {
            Start-Sleep -Seconds $IntervalSec
        }
    }

    Write-Host ""
    return @{ Ram = $ramSamples; Swap = $swapSamples; Cpu = $cpuSamples; CpuRamAi = $cpuRamAiSamples }
}

function Get-PhaseActions {
    param([DateTime]$PhaseStart, [DateTime]$PhaseEnd)

    # Source de vérité : events.log (champ physicalMbFreed), écrit par Phase3 à chaque tick.
    # actions.json est figé (Phase3 n'y écrit plus depuis un refactoring).
    $zeros     = [double[]]::new($TotalPoints)
    $eventsLog = Join-Path $OutputDir "events.log"
    if (-not (Test-Path $eventsLog)) {
        Write-Warning "  Get-PhaseActions : events.log introuvable -> valeurs nulles"
        return $zeros
    }

    try {
        $inv3        = [System.Globalization.CultureInfo]::InvariantCulture
        $dtStyles    = [System.Globalization.DateTimeStyles]::RoundtripKind
        $windowStart = $PhaseStart.AddSeconds(-30)
        $windowEnd   = $PhaseEnd.AddSeconds(30)

        # Lecture avec FileShare (Phase3 garde le fichier ouvert en écriture)
        $fs   = [System.IO.FileStream]::new($eventsLog, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr   = [System.IO.StreamReader]::new($fs)
        $raw  = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()

        $lines    = $raw -split "`n"
        $filtered = [System.Collections.Generic.List[double]]::new()
        $matched  = 0

        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try {
                $entry = ConvertFrom-Json $line
                $ts    = [DateTime]::Parse($entry.timestamp, $inv3, $dtStyles)
                if ($ts -ge $windowStart -and $ts -le $windowEnd) {
                    $filtered.Add([double]$entry.physicalMbFreed)
                    $matched++
                }
            } catch {}
        }

        if ($matched -eq 0) {
            Write-Warning ("  Get-PhaseActions : aucune entree dans la fenetre [{0:HH:mm:ss} - {1:HH:mm:ss}] UTC -> valeurs nulles" -f $windowStart, $windowEnd)
            return $zeros
        }

        $result = [System.Collections.Generic.List[double]]::new()
        foreach ($v in $filtered) { $result.Add([math]::Max(0, [math]::Round($v, 0))) }

        while ($result.Count -lt $TotalPoints) { $result.Add(0) }

        return $result.ToArray()[0..($TotalPoints - 1)]
    } catch {
        Write-Warning "  Get-PhaseActions : erreur lecture events.log -> $_"
        return $zeros
    }
}

function Enable-GamingMode {
    $flagFile = Join-Path $OutputDir "gaming_mode.force"
    [System.IO.File]::WriteAllText($flagFile, "auto", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  Mode Gaming active (gaming_mode.force = 'auto')" -ForegroundColor Cyan
    Write-Host "  Stabilisation 5 secondes..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 5
}

function Disable-GamingMode {
    $flagFile = Join-Path $OutputDir "gaming_mode.force"
    if (Test-Path $flagFile) { Remove-Item $flagFile -Force }
    Write-Host "  Mode Gaming desactive." -ForegroundColor DarkGray
}

function Enable-TournoiMode {
    $flagFile = Join-Path $OutputDir "tournament_mode.force"
    [System.IO.File]::WriteAllText($flagFile, "1", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  Mode Tournoi active (tournament_mode.force = '1')" -ForegroundColor Magenta
    Write-Host "  Stabilisation 5 secondes..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 5
}

function Disable-TournoiMode {
    $flagFile = Join-Path $OutputDir "tournament_mode.force"
    if (Test-Path $flagFile) { Remove-Item $flagFile -Force }
    Write-Host "  Mode Tournoi desactive." -ForegroundColor DarkGray
}

# -- Charge RAM artificielle ---------------------------------------------------

$script:ramLoadObjects = New-Object System.Collections.ArrayList

function Start-RamLoad {
    $LoadChunkMb    = 25     # taille de chaque bloc (granularite fine = stop plus precis)
    $MinFreeAfterMb = 700    # RAM libre minimale a garantir (marge de securite anti-gel)

    # Cible : allouer au plus 75% de la RAM totale.
    # Reproductible quelle que soit la machine (16 Go, 32 Go, etc.)
    $cs          = Get-CimInstance -ClassName Win32_OperatingSystem
    $totalRamMb  = [int]($cs.TotalVisibleMemorySize / 1024)
    $MaxLoadMb   = [int]($totalRamMb * 0.75)
    $freeNowMb   = [int]($cs.FreePhysicalMemory  / 1024)

    Write-Host "  Chargement RAM en cours..." -ForegroundColor Yellow
    Write-Host "  (RAM totale : $totalRamMb Mo  |  Cible : $MaxLoadMb Mo = 75%  |  Garde-fou : $MinFreeAfterMb Mo libres)" -ForegroundColor DarkGray
    Write-Host ""

    $script:ramLoadObjects.Clear()

    $targetMb  = $freeNowMb - $MinFreeAfterMb
    if ($targetMb -gt $MaxLoadMb)   { $targetMb = $MaxLoadMb }
    if ($targetMb -lt $LoadChunkMb) { $targetMb = $LoadChunkMb }

    $chunks    = [int]($targetMb / $LoadChunkMb)
    $allocated = 0
    $freeNow   = $freeNowMb

    for ($i = 0; $i -lt $chunks; $i++) {
        # Vérification RAM libre toutes les 2 itérations (= 50 Mo) pour stopper tôt
        if ($i % 2 -eq 0) {
            $csNow   = Get-CimInstance -ClassName Win32_OperatingSystem
            $freeNow = [int]($csNow.FreePhysicalMemory / 1024)
        }
        if ($freeNow -lt $MinFreeAfterMb) { break }

        $pct    = [math]::Round(($i / $chunks) * 100)
        $filled = [math]::Round($pct / 4)
        $bar    = "[" + ("=" * $filled) + (" " * (25 - $filled)) + "]"
        Write-Host ("`r  Allocation  $bar  $pct%   Libre : $freeNow Mo  ") -NoNewline -ForegroundColor Yellow

        try {
            $arr      = New-Object byte[] ($LoadChunkMb * 1024 * 1024)
            $pageStep = 4096
            $arrLen   = $arr.Length
            $j = 0
            while ($j -lt $arrLen) { $arr[$j] = 1; $j += $pageStep }
            $null = $script:ramLoadObjects.Add($arr)
            $allocated += $LoadChunkMb
        } catch { break }
    }

    Write-Host ""
    $csAfter   = Get-CimInstance -ClassName Win32_OperatingSystem
    $freeAfter = [int]($csAfter.FreePhysicalMemory / 1024)
    Write-Host "  Charge appliquee : $allocated Mo alloues -- RAM libre restante : $freeAfter Mo" -ForegroundColor Green
    Write-Host ""
}

function Stop-RamLoad {
    $script:ramLoadObjects.Clear()
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    [System.GC]::Collect()
    Start-Sleep -Milliseconds 500
}

# =============================================================================
# DEMARRAGE
# =============================================================================

Write-Header

Write-Host "  Choisissez le type de benchmark :" -ForegroundColor Cyan
Write-Host ""
Write-Host "    1 - Benchmark de base        (Phase 1 : Sans RAM-AI  +  Phase 2 : Mode Auto)" -ForegroundColor White
Write-Host "    2 - Benchmark + Gaming        (+ Phase 3 : Mode Gaming)" -ForegroundColor White
Write-Host "    3 - Benchmark + Tournoi       (+ Phase 3 : Mode Tournoi -- licence Ultra requise)" -ForegroundColor White
Write-Host "    4 - Benchmark complet         (Phases 1, 2, 3 Gaming, 4 Tournoi)" -ForegroundColor White
Write-Host ""

$menuChoice = ""
while ($menuChoice -notmatch '^[1-4]$') {
    $menuChoice = Read-Host "  Votre choix [1-4]"
    $menuChoice = $menuChoice.Trim()
}

if ($menuChoice -eq "1") {
    $hasGaming  = $false
    $hasTournoi = $false
} elseif ($menuChoice -eq "2") {
    $hasGaming  = $true
    $hasTournoi = $false
} elseif ($menuChoice -eq "3") {
    $hasGaming  = $false
    $hasTournoi = $true
} else {
    $hasGaming  = $true
    $hasTournoi = $true
}

Write-Host ""
Write-Host "  [!]  AVANT DE LANCER CE BENCHMARK :" -ForegroundColor Yellow
Write-Host "    *  Fermez les jeux en cours" -ForegroundColor White
Write-Host "    *  Laissez Chrome ouvert avec quelques onglets" -ForegroundColor White
Write-Host "    *  Mode Automatique sera active automatiquement" -ForegroundColor White
Write-Host "    *  Pour le Mode Gaming : lancez un jeu AVANT de lancer le script" -ForegroundColor White
Write-Host "    *  Ne pas interrompre le benchmark en cours d'execution" -ForegroundColor White
Write-Host ""

$nbPhases = 2
if ($hasGaming)  { $nbPhases++ }
if ($hasTournoi) { $nbPhases++ }

if ($hasGaming -and $hasTournoi) {
    $phasesLabel = "4 phases : Sans RAM-AI + Auto + Gaming + Tournoi"
} elseif ($hasGaming) {
    $phasesLabel = "3 phases : Sans RAM-AI + Auto + Gaming"
} elseif ($hasTournoi) {
    $phasesLabel = "3 phases : Sans RAM-AI + Auto + Tournoi"
} else {
    $phasesLabel = "2 phases : Sans RAM-AI + Auto"
}

$dureeMin = $nbPhases * 3
Write-Host "  Benchmark : $phasesLabel" -ForegroundColor DarkGray
Write-Host "  Duree estimee : ~$dureeMin minutes (+ transitions)" -ForegroundColor DarkGray
Write-Host ""

# =============================================================================
# PHASE 1 : SANS RAM-AI
# =============================================================================

Write-Phase "PHASE 1 / $nbPhases  --  SANS RAM-AI  (Reference)" "Red"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Fermez le dashboard RAM-AI (clic droit icone systray -> Quitter)" -ForegroundColor White
Write-Host "  2. Attendez que le service Phase 3 soit arrete" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est ferme..." -ForegroundColor Yellow
$null = Read-Host

try {

Start-RamLoad

Write-Host "  Arret automatique du service RamAI-Phase3..." -ForegroundColor Yellow
Start-Process -FilePath "net.exe" -ArgumentList "stop RamAI-Phase3" -Wait -NoNewWindow -ErrorAction SilentlyContinue
Start-Sleep -Seconds 10
Write-Host "  Service arrete." -ForegroundColor Green
Write-Host ""

Write-Host "  Debut mesure dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

$p1Start = [DateTime]::UtcNow
$p1Data  = Measure-Phase -PhaseName "SANS RAM-AI  " -BarColor "Red"
$p1End   = [DateTime]::UtcNow

$p1Ram  = $p1Data.Ram
$p1Swap = $p1Data.Swap

$p1AvgRam  = [math]::Round(($p1Ram  | Measure-Object -Average).Average, 2)
$p1MinRam  = [math]::Round(($p1Ram  | Measure-Object -Minimum).Minimum, 2)
$p1MaxRam  = [math]::Round(($p1Ram  | Measure-Object -Maximum).Maximum, 2)
$p1AvgSwap = [math]::Round((Get-Median $p1Swap), 1)
$p1P95Swap = [math]::Round((Get-P95 $p1Swap), 1)
$p1MaxSwap = [math]::Round(($p1Swap | Measure-Object -Maximum).Maximum, 1)
$p1Actions = Get-PhaseActions -PhaseStart $p1Start -PhaseEnd $p1End

Write-Host ""
Write-Host "  Phase 1 OK : RAM moy=$p1AvgRam Go  Swap moy=$p1AvgSwap p/s" -ForegroundColor Green

Write-Host "  Redemarrage automatique du service RamAI-Phase3..." -ForegroundColor Yellow
Start-Process -FilePath "net.exe" -ArgumentList "start RamAI-Phase3" -Wait -NoNewWindow -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host "  Service redemarre." -ForegroundColor Green

# =============================================================================
# PAUSE : Lancement RAM-AI
# =============================================================================

Write-Phase "PAUSE  --  Lancez RAM-AI en Mode Automatique" "Yellow"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Lancez le dashboard RAM-AI" -ForegroundColor White
Write-Host "  2. Attendez que le service Phase 3 soit actif (point vert)" -ForegroundColor White
Write-Host "  3. Verifiez que le mode est Automatique (pas Gaming, pas Tournoi)" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est actif en mode Automatique..." -ForegroundColor Yellow
$null = Read-Host

# Nettoyer les flags au cas ou
Disable-GamingMode 2>$null
Disable-TournoiMode 2>$null

# =============================================================================
# PHASE 2 : MODE AUTOMATIQUE
# =============================================================================

Write-Phase "PHASE 2 / $nbPhases  --  MODE AUTOMATIQUE" "Green"

$p2Correction = Get-RamAiCorrectionGb
if ($p2Correction -gt 0) {
    Write-Host "  Correction WorkingSet RAM-AI : +$p2Correction Go" -ForegroundColor DarkGray
}
Write-Host "  Debut mesure dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

$p2Start = [DateTime]::UtcNow
$p2Data  = Measure-Phase -PhaseName "MODE AUTO    " -BarColor "Green"
$p2End   = [DateTime]::UtcNow

$p2Ram = $p2Data.Ram
if ($p2Correction -gt 0) {
    $corrected = @()
    foreach ($s in $p2Ram) { $corrected += [math]::Round($s + $p2Correction, 2) }
    $p2Ram = $corrected
}
$p2Swap      = $p2Data.Swap
$p2CpuRamAi  = $p2Data.CpuRamAi

$p2AvgRam    = [math]::Round(($p2Ram     | Measure-Object -Average).Average, 2)
$p2MinRam    = [math]::Round(($p2Ram     | Measure-Object -Minimum).Minimum, 2)
$p2MaxRam    = [math]::Round(($p2Ram     | Measure-Object -Maximum).Maximum, 2)
$p2AvgSwap   = [math]::Round((Get-Median $p2Swap), 1)
$p2P95Swap   = [math]::Round((Get-P95 $p2Swap), 1)
$p2MaxSwap   = [math]::Round(($p2Swap    | Measure-Object -Maximum).Maximum, 1)
$p2AvgCpuRai = [math]::Round(($p2CpuRamAi | Measure-Object -Average).Average, 2)
$p2MaxCpuRai = [math]::Round(($p2CpuRamAi | Measure-Object -Maximum).Maximum, 2)
$p2Actions   = Get-PhaseActions -PhaseStart $p2Start -PhaseEnd $p2End

$p2GainAbs = [math]::Round($p2AvgRam - $p1AvgRam, 2)
if ($p1AvgRam -gt 0) { $p2GainPct = [math]::Round(($p2GainAbs / $p1AvgRam) * 100, 1) } else { $p2GainPct = 0.0 }
if ($p2GainAbs -ge 0) { $p2GainSign = "+" } else { $p2GainSign = "" }

Write-Host ""
Write-Host "  Phase 2 OK : RAM moy=$p2AvgRam Go  Gain=$p2GainSign$p2GainAbs Go  Swap moy=$p2AvgSwap p/s" -ForegroundColor Green

# =============================================================================
# PHASE 3 : MODE GAMING (optionnel)
# =============================================================================

$p3Ram = @(); $p3Swap = @(); $p3CpuRamAi = @(); $p3Actions = @()
$p3AvgRam = 0.0; $p3MinRam = 0.0; $p3MaxRam = 0.0
$p3AvgSwap = 0.0; $p3P95Swap = 0.0; $p3MaxSwap = 0.0
$p3AvgCpuRai = 0.0; $p3MaxCpuRai = 0.0
$p3GainAbs = 0.0; $p3GainPct = 0.0; $p3GainSign = "+"

if ($hasGaming) {
    if ($hasTournoi) { $pN = 3 } else { $pN = $nbPhases }
    Write-Phase "PHASE $pN / $nbPhases  --  MODE GAMING" "Cyan"

    Enable-GamingMode

    $p3Correction = Get-RamAiCorrectionGb
    Write-Host "  Debut mesure dans 3 secondes..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 3

    $p3Start = [DateTime]::UtcNow
    $p3Data  = Measure-Phase -PhaseName "MODE GAMING  " -BarColor "Cyan"
    $p3End   = [DateTime]::UtcNow

    $p3Ram = $p3Data.Ram
    if ($p3Correction -gt 0) {
        $corrected = @()
        foreach ($s in $p3Ram) { $corrected += [math]::Round($s + $p3Correction, 2) }
        $p3Ram = $corrected
    }
    $p3Swap     = $p3Data.Swap
    $p3CpuRamAi = $p3Data.CpuRamAi

    $p3AvgRam    = [math]::Round(($p3Ram      | Measure-Object -Average).Average, 2)
    $p3MinRam    = [math]::Round(($p3Ram      | Measure-Object -Minimum).Minimum, 2)
    $p3MaxRam    = [math]::Round(($p3Ram      | Measure-Object -Maximum).Maximum, 2)
    $p3AvgSwap   = [math]::Round((Get-Median $p3Swap), 1)
    $p3P95Swap   = [math]::Round((Get-P95 $p3Swap), 1)
    $p3MaxSwap   = [math]::Round(($p3Swap     | Measure-Object -Maximum).Maximum, 1)
    $p3AvgCpuRai = [math]::Round(($p3CpuRamAi | Measure-Object -Average).Average, 2)
    $p3MaxCpuRai = [math]::Round(($p3CpuRamAi | Measure-Object -Maximum).Maximum, 2)
    $p3Actions   = Get-PhaseActions -PhaseStart $p3Start -PhaseEnd $p3End

    $p3GainAbs = [math]::Round($p3AvgRam - $p1AvgRam, 2)
    if ($p1AvgRam -gt 0) { $p3GainPct = [math]::Round(($p3GainAbs / $p1AvgRam) * 100, 1) } else { $p3GainPct = 0.0 }
    if ($p3GainAbs -ge 0) { $p3GainSign = "+" } else { $p3GainSign = "" }

    Disable-GamingMode
    Write-Host ""
    Write-Host "  Phase 3 Gaming OK : RAM moy=$p3AvgRam Go  Gain=$p3GainSign$p3GainAbs Go" -ForegroundColor Cyan
}

# =============================================================================
# PHASE 4 : MODE TOURNOI (optionnel)
# =============================================================================

$p4Ram = @(); $p4Swap = @(); $p4CpuRamAi = @(); $p4Actions = @()
$p4AvgRam = 0.0; $p4MinRam = 0.0; $p4MaxRam = 0.0
$p4AvgSwap = 0.0; $p4P95Swap = 0.0; $p4MaxSwap = 0.0
$p4AvgCpuRai = 0.0; $p4MaxCpuRai = 0.0
$p4GainAbs = 0.0; $p4GainPct = 0.0; $p4GainSign = "+"

if ($hasTournoi) {
    Write-Phase "PHASE $nbPhases / $nbPhases  --  MODE TOURNOI  (Ultra)" "Magenta"

    Enable-TournoiMode

    $p4Correction = Get-RamAiCorrectionGb
    Write-Host "  Debut mesure dans 3 secondes..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 3

    $p4Start = [DateTime]::UtcNow
    $p4Data  = Measure-Phase -PhaseName "MODE TOURNOI " -BarColor "Magenta"
    $p4End   = [DateTime]::UtcNow

    $p4Ram = $p4Data.Ram
    if ($p4Correction -gt 0) {
        $corrected = @()
        foreach ($s in $p4Ram) { $corrected += [math]::Round($s + $p4Correction, 2) }
        $p4Ram = $corrected
    }
    $p4Swap     = $p4Data.Swap
    $p4CpuRamAi = $p4Data.CpuRamAi

    $p4AvgRam    = [math]::Round(($p4Ram      | Measure-Object -Average).Average, 2)
    $p4MinRam    = [math]::Round(($p4Ram      | Measure-Object -Minimum).Minimum, 2)
    $p4MaxRam    = [math]::Round(($p4Ram      | Measure-Object -Maximum).Maximum, 2)
    $p4AvgSwap   = [math]::Round((Get-Median $p4Swap), 1)
    $p4P95Swap   = [math]::Round((Get-P95 $p4Swap), 1)
    $p4MaxSwap   = [math]::Round(($p4Swap     | Measure-Object -Maximum).Maximum, 1)
    $p4AvgCpuRai = [math]::Round(($p4CpuRamAi | Measure-Object -Average).Average, 2)
    $p4MaxCpuRai = [math]::Round(($p4CpuRamAi | Measure-Object -Maximum).Maximum, 2)
    $p4Actions   = Get-PhaseActions -PhaseStart $p4Start -PhaseEnd $p4End

    $p4GainAbs = [math]::Round($p4AvgRam - $p1AvgRam, 2)
    if ($p1AvgRam -gt 0) { $p4GainPct = [math]::Round(($p4GainAbs / $p1AvgRam) * 100, 1) } else { $p4GainPct = 0.0 }
    if ($p4GainAbs -ge 0) { $p4GainSign = "+" } else { $p4GainSign = "" }

    Disable-TournoiMode
    Write-Host ""
    Write-Host "  Phase 4 Tournoi OK : RAM moy=$p4AvgRam Go  Gain=$p4GainSign$p4GainAbs Go" -ForegroundColor Magenta
}

# =============================================================================
# LECTURE stats.json
# =============================================================================

$statsPath = Join-Path $OutputDir "stats.json"

$rptFirstLaunch       = "Inconnu"
$rptTotalSessions     = 0
$rptTotalUsageMin     = 0
$rptTotalRamFreedGb   = 0.0
$rptTotalProcs        = 0
$rptTodayRamFreed     = 0.0
$rptTodayProcs        = 0
$rptGamingActivations = 0
$rptGamingRam         = 0.0
$rptEcoActivations    = 0
$rptEcoRam            = 0.0
$rptTurbo             = 0
$rptTournament        = 0
$rptBestSession       = "-"
$rptBestSessionDate   = "-"

if (Test-Path $statsPath) {
    try {
        $statsJson = Get-Content $statsPath -Raw | ConvertFrom-Json
        try { $rptFirstLaunch       = [DateTime]::Parse($statsJson.FirstLaunch).ToLocalTime().ToString("dd/MM/yyyy HH:mm") } catch {}
        try { $rptTotalSessions     = [int]$statsJson.TotalSessions } catch {}
        try { $rptTotalUsageMin     = [long]$statsJson.TotalUsageMinutes } catch {}
        try { $rptTotalRamFreedGb   = [math]::Round([double]$statsJson.TotalRamFreedGb, 2) } catch {}
        try { $rptTotalProcs        = [long]$statsJson.TotalProcessesOptimized } catch {}
        try { $rptTodayRamFreed     = [math]::Round([double]$statsJson.TodayRamFreedGb, 2) } catch {}
        try { $rptTodayProcs        = [long]$statsJson.TodayProcessesOptimized } catch {}
        try { $rptGamingActivations = [int]$statsJson.GamingActivations } catch {}
        try { $rptGamingRam         = [math]::Round([double]$statsJson.GamingRamFreedGb, 2) } catch {}
        try { $rptEcoActivations    = [int]$statsJson.EcoActivations } catch {}
        try { $rptEcoRam            = [math]::Round([double]$statsJson.EcoRamFreedGb, 2) } catch {}
        try { $rptTurbo             = [int]$statsJson.TurboUseCount } catch {}
        try { $rptTournament        = [int]$statsJson.TournamentUseCount } catch {}
        try {
            if ($statsJson.BestSessionRamFreedGb -gt 0) {
                $bsd = [DateTime]::Parse($statsJson.BestSessionDate).ToLocalTime().ToString("dd/MM/yyyy")
                $bsv = [math]::Round([double]$statsJson.BestSessionRamFreedGb, 2)
                $rptBestSession     = "$bsv Go"
                $rptBestSessionDate = $bsd
            }
        } catch {}
    } catch {
        Write-Warning "  Impossible de lire stats.json."
    }
}

$rptTotalDays  = [int]($rptTotalUsageMin / 1440)
$rptTotalHours = [int](($rptTotalUsageMin % 1440) / 60)
$rptTotalMins  = [int]($rptTotalUsageMin % 60)
$rptDuree      = "${rptTotalDays}j ${rptTotalHours}h " + $rptTotalMins.ToString("D2") + "m"

$rptVerdict = "Performance correcte sur ce systeme."
if ($rptTotalRamFreedGb -gt 10.0) { $rptVerdict = "Performance excellente sur ce systeme." }
elseif ($rptTotalRamFreedGb -gt 5.0) { $rptVerdict = "Bonne performance sur ce systeme." }

# =============================================================================
# VERDICT Phase 2 (metrique principale)
# =============================================================================

if ($p2GainAbs -gt 0.5) {
    $verdictText  = "RAM-AI a significativement ameliore la RAM disponible sur ce systeme."
    $verdictColor = "#4CAF50"
} elseif ($p2GainAbs -gt 0.1) {
    $verdictText  = "RAM-AI a legerement ameliore la RAM disponible sur ce systeme."
    $verdictColor = "#8BC34A"
} elseif ($p2GainAbs -ge -0.1) {
    $verdictText  = "Aucune difference RAM mesuree -- la memoire etait deja optimale."
    $verdictColor = "#F5A623"
} else {
    $verdictText  = "Aucune amelioration RAM mesuree sur cette configuration."
    $verdictColor = "#F44336"
}

if ($p1AvgSwap -gt 0 -and $p2AvgSwap -eq 0) {
    $verdictSwapText  = "RAM-AI a elimine le swap sur ce systeme !"
    $verdictSwapColor = "#4CAF50"
} elseif ($p1AvgSwap -gt 0 -and $p2AvgSwap -lt $p1AvgSwap) {
    $swapReducPct     = [math]::Round(($p1AvgSwap - $p2AvgSwap) / $p1AvgSwap * 100, 0)
    $verdictSwapText  = "RAM-AI a reduit le swap de $swapReducPct% ($p1AvgSwap -> $p2AvgSwap p/s en moyenne)."
    $verdictSwapColor = "#8BC34A"
} elseif ($p1AvgSwap -eq 0 -and $p2AvgSwap -eq 0) {
    $verdictSwapText  = "Aucun swap detecte -- la RAM est suffisante sur ce systeme."
    $verdictSwapColor = "#4CAF50"
} elseif ($p2AvgSwap -gt $p1AvgSwap) {
    $swapHausseAbs    = [math]::Round($p2AvgSwap - $p1AvgSwap, 1)
    $verdictSwapText  = "Attention : le swap a augmente de $swapHausseAbs p/s avec RAM-AI actif. RAM liberee mais transferee sur disque -- normal si le systeme manque de RAM."
    $verdictSwapColor = "#ef5350"
} else {
    $verdictSwapText  = "Swap stable -- aucune variation significative detectee."
    $verdictSwapColor = "#F5A623"
}

# =============================================================================
# CONSTRUCTION DES TABLEAUX JS (un tableau par phase, axe X partage 0-170s)
# =============================================================================

$inv = [System.Globalization.CultureInfo]::InvariantCulture

# Axe X partage : 0s, 10s, 20s, ..., 170s
$xAxisArr = @()
for ($i = 0; $i -lt $TotalPoints; $i++) { $xAxisArr += ($i * $IntervalSec).ToString() }
$xAxisJs = $xAxisArr -join ","

# Helper : convertir tableau double[] en chaine JS
function ConvertTo-JsArray {
    param([double[]]$Data, [string]$Fmt = "F2")
    $inv2  = [System.Globalization.CultureInfo]::InvariantCulture
    $parts = @()
    foreach ($v in $Data) { $parts += $v.ToString($Fmt, $inv2) }
    return $parts -join ","
}

# Arrays RAM par phase (vide si phase inactive)
$p1RamJs = ConvertTo-JsArray -Data $p1Ram
$p2RamJs = ConvertTo-JsArray -Data $p2Ram
if ($hasGaming)  { $p3RamJs = ConvertTo-JsArray -Data $p3Ram } else { $p3RamJs = "" }
if ($hasTournoi) { $p4RamJs = ConvertTo-JsArray -Data $p4Ram } else { $p4RamJs = "" }

# Arrays CPU RAM-AI par phase
$p2CpuJs = ConvertTo-JsArray -Data $p2CpuRamAi -Fmt "F2"
if ($hasGaming)  { $p3CpuJs = ConvertTo-JsArray -Data $p3CpuRamAi -Fmt "F2" } else { $p3CpuJs = "" }
if ($hasTournoi) { $p4CpuJs = ConvertTo-JsArray -Data $p4CpuRamAi -Fmt "F2" } else { $p4CpuJs = "" }

# Swap bar chart : labels, data, couleurs dynamiques (vert=amelioration, rouge=degradation vs P1)
function Get-SwapBarColor {
    param([double]$Val, [double]$Ref, [string]$NeutralColor)
    $tol = 0.5  # < 0.5 p/s de difference = neutre
    if ($Ref -le 0 -and $Val -le 0) { return '"rgba(66,165,245,0.75)"' }  # pas de swap = bleu
    if ($Val -le ($Ref - $tol))     { return '"rgba(102,187,106,0.75)"' }  # mieux = vert
    if ($Val -ge ($Ref + $tol))     { return '"rgba(239,83,80,0.75)"'   }  # pire  = rouge
    return $NeutralColor                                                     # stable = couleur neutre
}

$swapLabelsArr = @('"Sans RAM-AI"', '"Mode Auto"')
$swapDataArr   = @($p1AvgSwap.ToString("F1", $inv), $p2AvgSwap.ToString("F1", $inv))
# Phase 1 = reference : neutre gris (c'est la baseline, pas "mauvaise" en soi)
$swapColorsArr = @('"rgba(150,150,150,0.6)"', (Get-SwapBarColor $p2AvgSwap $p1AvgSwap '"rgba(102,187,106,0.75)"'))
if ($hasGaming) {
    $swapLabelsArr += '"Mode Gaming"'
    $swapDataArr   += $p3AvgSwap.ToString("F1", $inv)
    $swapColorsArr += (Get-SwapBarColor $p3AvgSwap $p1AvgSwap '"rgba(245,166,35,0.75)"')
}
if ($hasTournoi) {
    $swapLabelsArr += '"Mode Tournoi"'
    $swapDataArr   += $p4AvgSwap.ToString("F1", $inv)
    $swapColorsArr += (Get-SwapBarColor $p4AvgSwap $p1AvgSwap '"rgba(171,71,188,0.75)"')
}
$swapLabelsJs = $swapLabelsArr -join ","
$swapDataJs   = $swapDataArr   -join ","
$swapColorsJs = $swapColorsArr -join ","

# Titre dynamique du graphique swap
if ($p2AvgSwap -lt ($p1AvgSwap - 0.5)) {
    $swapChartTitle = "Reduction du Swap &mdash; Impact RAM-AI"
} elseif ($p2AvgSwap -gt ($p1AvgSwap + 0.5)) {
    $swapChartTitle = "Swap &mdash; Augmentation detectee avec RAM-AI actif"
} else {
    $swapChartTitle = "Swap &mdash; Aucune variation significative"
}

# Stat : reduction swap % — vert si reduit (bon), rouge si augmente (mauvais)
if ($p1AvgSwap -eq 0 -and $p2AvgSwap -eq 0) {
    $swapStatLabel   = "Swap absent"
    $swapReducText   = "Aucun swap"
    $swapReducColor  = "#42A5F5"
    $swapBorderColor = "#42A5F5"
} elseif ($p1AvgSwap -gt 0) {
    $sr = [math]::Round(($p1AvgSwap - $p2AvgSwap) / $p1AvgSwap * 100, 0)
    if ($sr -gt 0) {
        $swapStatLabel   = "Swap reduit"
        $swapReducText   = "$sr% ↓"
        $swapReducColor  = "#00D4AA"
        $swapBorderColor = "#00D4AA"
    } elseif ($sr -lt 0) {
        $absVal          = [math]::Abs($sr)
        $swapStatLabel   = "Swap augmente !"
        $swapReducText   = "+$absVal% ↑"
        $swapReducColor  = "#ef5350"
        $swapBorderColor = "#ef5350"
    } else {
        $swapStatLabel   = "Swap inchange"
        $swapReducText   = "0%"
        $swapReducColor  = "#888"
        $swapBorderColor = "#888"
    }
} else {
    $swapStatLabel   = "Swap"
    $swapReducText   = "N/A"
    $swapReducColor  = "#555"
    $swapBorderColor = "#555"
}

# Visibilite colonnes optionnelles (CSS)
if ($hasGaming)  { $p3ColStyle = "" }   else { $p3ColStyle = "display:none" }
if ($hasTournoi) { $p4ColStyle = "" }   else { $p4ColStyle = "display:none" }

# Totaux actions
$p1TotalAct = 0; foreach ($v in $p1Actions) { $p1TotalAct += $v }
$p2TotalAct = 0; foreach ($v in $p2Actions) { $p2TotalAct += $v }
$p3TotalAct = 0; foreach ($v in $p3Actions) { $p3TotalAct += $v }
$p4TotalAct = 0; foreach ($v in $p4Actions) { $p4TotalAct += $v }

$dateStr = (Get-Date).ToString("dd/MM/yyyy HH:mm:ss")

# =============================================================================
# RAPPORT TXT
# =============================================================================

$inv2 = [System.Globalization.CultureInfo]::InvariantCulture

$reportTxtLines = @(
    "================================",
    "RAM-AI -- Rapport Benchmark",
    "Genere le : $dateStr",
    "================================",
    "",
    "-- PHASES --",
    "Phase 1  Sans RAM-AI  : RAM moy=$($p1AvgRam.ToString('F2',$inv2)) Go  Swap moy=$p1AvgSwap p/s",
    "Phase 2  Mode Auto    : RAM moy=$($p2AvgRam.ToString('F2',$inv2)) Go  Gain=$p2GainSign$($p2GainAbs.ToString('F2',$inv2)) Go ($p2GainSign$($p2GainPct.ToString('F1',$inv2))%)  CPU-AI moy=$p2AvgCpuRai %",
    "Phase 3  Mode Gaming  : RAM moy=$($p3AvgRam.ToString('F2',$inv2)) Go  Gain=$p3GainSign$($p3GainAbs.ToString('F2',$inv2)) Go ($p3GainSign$($p3GainPct.ToString('F1',$inv2))%)",
    "Phase 4  Mode Tournoi : RAM moy=$($p4AvgRam.ToString('F2',$inv2)) Go  Gain=$p4GainSign$($p4GainAbs.ToString('F2',$inv2)) Go ($p4GainSign$($p4GainPct.ToString('F1',$inv2))%)",
    "",
    "-- RAPPORT GLOBAL --",
    "Premier lancement     : $rptFirstLaunch",
    "Sessions totales      : $rptTotalSessions",
    "RAM recuperee total   : $rptTotalRamFreedGb Go",
    "Mode Gaming           : $rptGamingActivations sessions",
    "Mode Tournoi          : $rptTournament utilisations",
    "",
    "================================",
    "RAM-AI Smart Memory Optimizer -- $dateStr",
    "================================"
)
$reportTxtEscaped = ($reportTxtLines -join "\\n") -replace "'", "\'"

# =============================================================================
# TEMPLATE HTML
# =============================================================================

$htmlTemplate = @'
<!DOCTYPE html>
<html lang="fr">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>RAM-AI - Benchmark</title>
  <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.2/dist/chart.umd.min.js"></script>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: #0d0d0d; color: #e0e0e0; font-family: 'Segoe UI', Arial, sans-serif; min-height: 100vh; padding: 32px 24px; }
    .container { max-width: 1100px; margin: 0 auto; }
    .header { display: flex; align-items: center; gap: 16px; margin-bottom: 28px; }
    .logo { width: 52px; height: 52px; background: linear-gradient(135deg,#3A7BD5,#1a3a7a); border-radius: 12px; display: flex; align-items: center; justify-content: center; font-size: 24px; font-weight: 900; color: #fff; }
    .header h1 { font-size: 22px; font-weight: 700; color: #fff; }
    .header h1 span { color: #3A7BD5; }
    .header p { font-size: 13px; color: #888; margin-top: 3px; }
    .card { background: #161616; border: 1px solid #2a2a2a; border-radius: 14px; padding: 28px 24px; margin-bottom: 20px; }
    .chart-wrap-swap { position: relative; height: 300px; }
    .chart-wrap-ram  { position: relative; height: 350px; }
    .chart-wrap-cpu  { position: relative; height: 250px; }
    .card-title { font-size: 13px; font-weight: 600; color: #aaa; text-transform: uppercase; letter-spacing: 0.7px; margin-bottom: 6px; }
    .card-subtitle { font-size: 12px; color: #555; margin-bottom: 16px; }

    /* Stats cards */
    .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px,1fr)); gap: 14px; margin-bottom: 20px; }
    .stat-card { background: #161616; border: 1px solid #2a2a2a; border-radius: 10px; padding: 18px 20px; border-top-width: 3px; }
    .stat-card.s-swap { border-top-color: ##SWAP_BORDER_COLOR##; }
    .stat-card.s-ram  { border-top-color: #66bb6a; }
    .stat-card.s-act  { border-top-color: #F5A623; }
    .stat-card.s-cpu  { border-top-color: #AB47BC; }
    .stat-card h3 { font-size: 11px; text-transform: uppercase; letter-spacing: 0.8px; color: #666; margin-bottom: 8px; }
    .stat-card .big { font-size: 32px; font-weight: 700; margin-bottom: 4px; }
    .stat-card.s-swap .big { color: ##SWAP_REDUC_COLOR##; }
    .stat-card.s-ram  .big { color: #66bb6a; }
    .stat-card.s-act  .big { color: #F5A623; }
    .stat-card.s-cpu  .big { color: #AB47BC; }
    .stat-card .sub { font-size: 11px; color: #555; }

    /* Comparative table */
    .comp-table { width: 100%; border-collapse: collapse; font-size: 13px; margin-top: 4px; }
    .comp-table th { text-align: left; padding: 8px 10px; color: #666; font-size: 11px; text-transform: uppercase; letter-spacing: 0.6px; border-bottom: 1px solid #2a2a2a; }
    .comp-table td { padding: 7px 10px; border-bottom: 1px solid #1e1e1e; color: #ccc; }
    .comp-table td:first-child { color: #888; font-size: 12px; }
    .comp-table tr:last-child td { border-bottom: none; }
    .c-red { color: #ef5350 !important; font-weight: 600; }
    .c-grn { color: #66bb6a !important; font-weight: 600; }
    .c-org { color: #F5A623 !important; font-weight: 600; }
    .c-pur { color: #AB47BC !important; font-weight: 600; }
    .c-dim { color: #555 !important; }

    /* Verdict fusionné */
    .verdict-card { background: #161616; border: 1px solid #2a2a2a; border-radius: 10px; padding: 16px 20px; margin-bottom: 20px; }
    .verdict-row  { display: flex; align-items: center; gap: 12px; padding: 6px 0; }
    .verdict-row + .verdict-row { border-top: 1px solid #222; margin-top: 4px; padding-top: 10px; }
    .verdict-icon { font-size: 20px; flex-shrink: 0; }
    .verdict-label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.8px; color: #555; margin-bottom: 2px; }
    .verdict-text { font-size: 13px; font-weight: 600; }

    /* Rapport */
    .rapport-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-bottom: 16px; }
    .rapport-block { background: #0d0d0d; border: 1px solid #2a2a2a; border-radius: 10px; padding: 14px 16px; }
    .rapport-block h4 { font-size: 11px; text-transform: uppercase; letter-spacing: 0.8px; color: #555; margin-bottom: 10px; }
    .rapport-block table { width: 100%; border-collapse: collapse; font-size: 12px; }
    .rapport-block td { padding: 4px 0; color: #aaa; }
    .rapport-block td:last-child { text-align: right; font-weight: 600; color: #e0e0e0; }
    .rapport-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 20px; }
    .rapport-header h2 { font-size: 16px; font-weight: 700; }
    .export-btn { background: #21262D; color: #e0e0e0; border: 1px solid #30363D; border-radius: 6px; padding: 7px 16px; font-size: 12px; cursor: pointer; }
    .export-btn:hover { background: #30363D; }
    .conclusion-block { background: #0a1520; border: 1px solid #1e3a5f; border-radius: 10px; padding: 14px 16px; font-size: 13px; color: #90caf9; line-height: 1.7; }
    .footer { text-align: center; font-size: 11px; color: #444; padding-top: 8px; }
  </style>
</head>
<body>
<div class="container">

  <div class="header">
    <div class="logo">R</div>
    <div>
      <h1><span>RAM-AI</span> &mdash; Benchmark ##NB_PHASES## phases</h1>
      <p>Smart Memory Optimizer &nbsp;&bull;&nbsp; ##DATE##</p>
    </div>
  </div>

  <!-- Graphique 1 : Swap par phase (barres) -->
  <div class="card">
    <div class="card-title">##SWAP_CHART_TITLE##</div>
    <div class="card-subtitle">Le swap = utilisation du disque comme memoire de secours. Moins c'est bas, mieux c'est.</div>
    <div class="chart-wrap-swap"><canvas id="swapChart"></canvas></div>
  </div>

  <!-- Graphique 2 : RAM disponible, courbes separees par phase -->
  <div class="card">
    <div class="card-title">RAM disponible dans le temps</div>
    <div class="card-subtitle">Une courbe independante par phase &mdash; axe X : 0 a 180 secondes</div>
    <div class="chart-wrap-ram"><canvas id="ramChart"></canvas></div>
  </div>

  <!-- Graphique 3 : CPU RAM-AI -->
  <div class="card">
    <div class="card-title">Consommation CPU de RAM-AI</div>
    <div class="card-subtitle">Phases avec RAM-AI actif uniquement &mdash; ligne de reference a 2%</div>
    <div class="chart-wrap-cpu"><canvas id="cpuChart"></canvas></div>
  </div>

  <!-- Cartes de stats : ordre Swap / RAM / Actions / CPU -->
  <div class="stats-grid">
    <div class="stat-card s-swap">
      <h3>##SWAP_STAT_LABEL##</h3>
      <div class="big">##SWAP_REDUC_TEXT##</div>
      <div class="sub">##P1_AVG_SWAP## &rarr; ##P2_AVG_SWAP## p/s (moy)</div>
    </div>
    <div class="stat-card s-ram">
      <h3>Gain RAM (Mode Auto)</h3>
      <div class="big">##P2_GAIN_SIGN####P2_GAIN_ABS## Go</div>
      <div class="sub">##P2_GAIN_SIGN####P2_GAIN_PCT##% vs sans RAM-AI</div>
    </div>
    <div class="stat-card s-act">
      <h3>Actions RAM-AI</h3>
      <div class="big">##P2_TOTAL_ACT## Mo</div>
      <div class="sub">recuperes en Phase 2 (Mode Auto)</div>
    </div>
    <div class="stat-card s-cpu">
      <h3>CPU RAM-AI</h3>
      <div class="big">##P2_AVG_CPU_RAI##%</div>
      <div class="sub">consommation moyenne Phase 2</div>
    </div>
  </div>

  <!-- Tableau comparatif : Swap en premier -->
  <div class="card">
    <div class="card-title">Tableau comparatif toutes phases</div>
    <table class="comp-table">
      <tr>
        <th>Metrique</th>
        <th class="c-red">Phase 1 (Ref.)</th>
        <th class="c-grn">Phase 2 (Auto)</th>
        <th style="##P3_COL_STYLE##" class="c-org">Phase 3 (Gaming)</th>
        <th style="##P4_COL_STYLE##" class="c-pur">Phase 4 (Tournoi)</th>
      </tr>
      <tr>
        <td>Swap median (p/s)</td>
        <td class="c-red">##P1_AVG_SWAP##</td>
        <td class="c-grn">##P2_AVG_SWAP##</td>
        <td style="##P3_COL_STYLE##" class="c-org">##P3_AVG_SWAP##</td>
        <td style="##P4_COL_STYLE##" class="c-pur">##P4_AVG_SWAP##</td>
      </tr>
      <tr>
        <td>Swap P95 (p/s)</td>
        <td class="c-red">##P1_P95_SWAP##</td>
        <td class="c-grn">##P2_P95_SWAP##</td>
        <td style="##P3_COL_STYLE##" class="c-org">##P3_P95_SWAP##</td>
        <td style="##P4_COL_STYLE##" class="c-pur">##P4_P95_SWAP##</td>
      </tr>
      <tr>
        <td>RAM moy (Go)</td>
        <td>##P1_AVG_RAM##</td>
        <td>##P2_AVG_RAM##</td>
        <td style="##P3_COL_STYLE##">##P3_AVG_RAM##</td>
        <td style="##P4_COL_STYLE##">##P4_AVG_RAM##</td>
      </tr>
      <tr>
        <td>Gain RAM vs Phase 1</td>
        <td class="c-dim">&mdash;</td>
        <td class="c-grn">##P2_GAIN_SIGN####P2_GAIN_ABS## Go (##P2_GAIN_SIGN####P2_GAIN_PCT##%)</td>
        <td style="##P3_COL_STYLE##" class="c-org">##P3_GAIN_SIGN####P3_GAIN_ABS## Go (##P3_GAIN_SIGN####P3_GAIN_PCT##%)</td>
        <td style="##P4_COL_STYLE##" class="c-pur">##P4_GAIN_SIGN####P4_GAIN_ABS## Go (##P4_GAIN_SIGN####P4_GAIN_PCT##%)</td>
      </tr>
      <tr>
        <td>CPU RAM-AI moy (%)</td>
        <td class="c-dim">&mdash;</td>
        <td>##P2_AVG_CPU_RAI##</td>
        <td style="##P3_COL_STYLE##">##P3_AVG_CPU_RAI##</td>
        <td style="##P4_COL_STYLE##">##P4_AVG_CPU_RAI##</td>
      </tr>
      <tr>
        <td>Actions RAM-AI (Mo)</td>
        <td class="c-dim">&mdash;</td>
        <td>##P2_TOTAL_ACT##</td>
        <td style="##P3_COL_STYLE##">##P3_TOTAL_ACT##</td>
        <td style="##P4_COL_STYLE##">##P4_TOTAL_ACT##</td>
      </tr>
    </table>
  </div>

  <!-- Verdict fusionné RAM + Swap -->
  <div class="verdict-card">
    <div class="verdict-row">
      <div class="verdict-icon">&#x1F4CA;</div>
      <div>
        <div class="verdict-label">RAM disponible</div>
        <div class="verdict-text" style="color:##VERDICT_COLOR##;">##VERDICT_TEXT##</div>
      </div>
    </div>
    <div class="verdict-row">
      <div class="verdict-icon">&#x1F4BE;</div>
      <div>
        <div class="verdict-label">Swap (acces disque)</div>
        <div class="verdict-text" style="color:##VERDICT_SWAP_COLOR##;">##VERDICT_SWAP_TEXT##</div>
      </div>
    </div>
  </div>

  <!-- Rapport global -->
  <div class="card">
    <div class="rapport-header">
      <h2>Rapport RAM-AI complet</h2>
      <button class="export-btn" onclick="exportTxt()">Exporter en .TXT</button>
    </div>
    <div class="rapport-grid">
      <div class="rapport-block">
        <h4>Rapport du jour</h4>
        <table>
          <tr><td>Date</td><td>##DATE##</td></tr>
          <tr><td>RAM recuperee auj.</td><td class="c-grn">##RPT_TODAY_RAM## Go</td></tr>
          <tr><td>Actions d'optimisation auj.</td><td>##RPT_TODAY_PROCS##</td></tr>
        </table>
      </div>
      <div class="rapport-block">
        <h4>Rapport global depuis 1er lancement</h4>
        <table>
          <tr><td>1er lancement</td><td>##RPT_FIRST_LAUNCH##</td></tr>
          <tr><td>Sessions totales</td><td>##RPT_TOTAL_SESSIONS##</td></tr>
          <tr><td>Duree totale</td><td>##RPT_DUREE##</td></tr>
          <tr><td>RAM recuperee total</td><td class="c-grn">##RPT_TOTAL_RAM## Go</td></tr>
          <tr><td>Meilleure session</td><td>##RPT_BEST_SESSION## (##RPT_BEST_DATE##)</td></tr>
        </table>
      </div>
      <div class="rapport-block">
        <h4>Modes utilises</h4>
        <table>
          <tr><td>Mode Gaming</td><td>##RPT_GAMING_ACT## sessions &mdash; ##RPT_GAMING_RAM## Go</td></tr>
          <tr><td>Mode Eco</td><td>##RPT_ECO_ACT## sessions &mdash; ##RPT_ECO_RAM## Go</td></tr>
          <tr><td>Mode Turbo</td><td>##RPT_TURBO## utilisation(s)</td></tr>
          <tr><td>Mode Tournoi</td><td>##RPT_TOURNAMENT## utilisation(s)</td></tr>
        </table>
      </div>
      <div class="rapport-block">
        <h4>Swap benchmark</h4>
        <table>
          <tr><td>Phase 1 (sans RAM-AI)</td><td>med ##P1_AVG_SWAP## &mdash; P95 ##P1_P95_SWAP## &mdash; max ##P1_MAX_SWAP## p/s</td></tr>
          <tr><td>Phase 2 (Mode Auto)</td><td>med ##P2_AVG_SWAP## &mdash; P95 ##P2_P95_SWAP## &mdash; max ##P2_MAX_SWAP## p/s</td></tr>
        </table>
      </div>
    </div>
    <div class="conclusion-block">
      RAM-AI a recupere <strong>##RPT_TOTAL_RAM## Go</strong> depuis le ##RPT_FIRST_LAUNCH##.<br>##RPT_VERDICT##
    </div>
  </div>

  <div class="footer">RAM-AI Smart Memory Optimizer &nbsp;&bull;&nbsp; ##DATE##</div>
</div>

<script>
(function() {
  Chart.defaults.color       = '#888';
  Chart.defaults.borderColor = '#2a2a2a';

  var xLabels = [##X_AXIS_JS##].map(function(v) { return v + 's'; });

  // ── Graphique 1 : Swap par phase (barres) ──────────────────────────────────
  var ctxSwap = document.getElementById('swapChart').getContext('2d');
  new Chart(ctxSwap, {
    type: 'bar',
    data: {
      labels: [##SWAP_LABELS_JS##],
      datasets: [{
        label: 'Swap moyen (pages/sec)',
        data: [##SWAP_DATA_JS##],
        backgroundColor: [##SWAP_COLORS_JS##],
        borderColor:     [##SWAP_COLORS_JS##],
        borderWidth: 1,
        borderRadius: 6
      }]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: { display: false },
        tooltip: {
          backgroundColor: '#1e1e1e', borderColor: '#3a3a3a', borderWidth: 1,
          titleColor: '#ccc', bodyColor: '#aaa', padding: 12,
          callbacks: { label: function(item) { return ' Swap moy : ' + item.parsed.y + ' p/s'; } }
        }
      },
      scales: {
        x: { ticks: { color: '#aaa' }, grid: { color: '#1e1e1e' } },
        y: {
          beginAtZero: true,
          title: { display: true, text: 'Pages/sec', color: '#666', font: { size: 12 } },
          ticks: { color: '#aaa', callback: function(v) { return v + ' p/s'; } },
          grid: { color: '#1e1e1e' }
        }
      }
    },
    plugins: []
  });

  // ── Graphique 2 : RAM disponible, courbes separees par phase ───────────────
  var ramDatasets = [];
  ramDatasets.push({
    label: 'P1 - Sans RAM-AI',
    data: [##P1_RAM_JS##],
    borderColor: '#ef5350', backgroundColor: 'rgba(239,83,80,0.08)',
    borderWidth: 2.5, pointRadius: 2, pointHoverRadius: 5, fill: false, tension: 0.3
  });
  ramDatasets.push({
    label: 'P2 - Mode Auto',
    data: [##P2_RAM_JS##],
    borderColor: '#66bb6a', backgroundColor: 'rgba(102,187,106,0.08)',
    borderWidth: 2.5, pointRadius: 2, pointHoverRadius: 5, fill: false, tension: 0.3
  });
  var p3RamData = [##P3_RAM_JS##];
  if (p3RamData.length > 0) {
    ramDatasets.push({
      label: 'P3 - Mode Gaming',
      data: p3RamData,
      borderColor: '#F5A623', backgroundColor: 'rgba(245,166,35,0.08)',
      borderWidth: 2.5, pointRadius: 2, pointHoverRadius: 5, fill: false, tension: 0.3
    });
  }
  var p4RamData = [##P4_RAM_JS##];
  if (p4RamData.length > 0) {
    ramDatasets.push({
      label: 'P4 - Mode Tournoi',
      data: p4RamData,
      borderColor: '#AB47BC', backgroundColor: 'rgba(171,71,188,0.08)',
      borderWidth: 2.5, pointRadius: 2, pointHoverRadius: 5, fill: false, tension: 0.3
    });
  }

  var ctxRam = document.getElementById('ramChart').getContext('2d');
  new Chart(ctxRam, {
    type: 'line',
    data: { labels: xLabels, datasets: ramDatasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color: '#ccc', font: { size: 12 }, padding: 16, usePointStyle: true, pointStyleWidth: 12 } },
        tooltip: {
          backgroundColor: '#1e1e1e', borderColor: '#3a3a3a', borderWidth: 1,
          titleColor: '#ccc', bodyColor: '#aaa', padding: 12,
          callbacks: { label: function(item) { return ' ' + item.dataset.label + ' : ' + item.parsed.y.toFixed(1) + ' Go'; } }
        }
      },
      scales: {
        x: {
          title: { display: true, text: 'Temps (secondes)', color: '#666', font: { size: 12 } },
          ticks: { color: '#555', maxTicksLimit: 18 },
          grid: { color: '#1e1e1e' }
        },
        y: {
          grace: '8%',
          afterDataLimits: function(axis) {
            var span = axis.max - axis.min;
            if (span < 0.8) {
              var mid = (axis.max + axis.min) / 2;
              axis.min = Math.max(0, mid - 0.4);
              axis.max = mid + 0.4;
            }
          },
          title: { display: true, text: 'RAM disponible (Go)', color: '#aaa', font: { size: 12 } },
          ticks: { color: '#aaa', callback: function(v) { return (Math.round(v * 10) / 10).toFixed(1) + ' Go'; } },
          grid: { color: '#1e1e1e' }
        }
      }
    }
  });

  // ── Graphique 3 : CPU RAM-AI ───────────────────────────────────────────────
  var cpuDatasets = [];
  cpuDatasets.push({
    label: 'P2 - Mode Auto',
    data: [##P2_CPU_JS##],
    borderColor: '#66bb6a', backgroundColor: 'rgba(102,187,106,0.10)',
    borderWidth: 2, pointRadius: 2, pointHoverRadius: 5, fill: true, tension: 0.3
  });
  var p3CpuData = [##P3_CPU_JS##];
  if (p3CpuData.length > 0) {
    cpuDatasets.push({
      label: 'P3 - Mode Gaming',
      data: p3CpuData,
      borderColor: '#F5A623', backgroundColor: 'rgba(245,166,35,0.10)',
      borderWidth: 2, pointRadius: 2, pointHoverRadius: 5, fill: true, tension: 0.3
    });
  }
  var p4CpuData = [##P4_CPU_JS##];
  if (p4CpuData.length > 0) {
    cpuDatasets.push({
      label: 'P4 - Mode Tournoi',
      data: p4CpuData,
      borderColor: '#AB47BC', backgroundColor: 'rgba(171,71,188,0.10)',
      borderWidth: 2, pointRadius: 2, pointHoverRadius: 5, fill: true, tension: 0.3
    });
  }

  var cpuRefLinePlugin = {
    id: 'cpuRefLine',
    afterDraw: function(chart) {
      var ctx2  = chart.ctx;
      var area  = chart.chartArea;
      var yAxis = chart.scales['y'];
      if (!yAxis) { return; }
      var yPx = yAxis.getPixelForValue(2);
      ctx2.save();
      ctx2.setLineDash([6, 4]);
      ctx2.strokeStyle = 'rgba(150,150,150,0.45)';
      ctx2.lineWidth = 1;
      ctx2.beginPath();
      ctx2.moveTo(area.left, yPx);
      ctx2.lineTo(area.right, yPx);
      ctx2.stroke();
      ctx2.setLineDash([]);
      ctx2.fillStyle = 'rgba(150,150,150,0.6)';
      ctx2.font = '11px Segoe UI, Arial, sans-serif';
      ctx2.fillText('2%', area.right + 5, yPx + 4);
      ctx2.restore();
    }
  };

  var ctxCpu = document.getElementById('cpuChart').getContext('2d');
  new Chart(ctxCpu, {
    type: 'line',
    data: { labels: xLabels, datasets: cpuDatasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color: '#ccc', font: { size: 12 }, usePointStyle: true } },
        tooltip: {
          backgroundColor: '#1e1e1e', borderColor: '#3a3a3a', borderWidth: 1,
          titleColor: '#ccc', bodyColor: '#aaa', padding: 10,
          callbacks: { label: function(item) { return ' ' + item.dataset.label + ' : ' + item.parsed.y + ' %'; } }
        }
      },
      scales: {
        x: {
          title: { display: true, text: 'Temps (secondes)', color: '#666', font: { size: 12 } },
          ticks: { color: '#555', maxTicksLimit: 18 },
          grid: { color: '#1e1e1e' }
        },
        y: {
          min: 0,
          title: { display: true, text: 'CPU (%)', color: '#aaa', font: { size: 12 } },
          ticks: { color: '#aaa', callback: function(v) { return v + '%'; } },
          grid: { color: '#1e1e1e' }
        }
      }
    },
    plugins: [cpuRefLinePlugin]
  });

})();

function exportTxt() {
  var txt = '##REPORT_TXT_ESCAPED##';
  txt = txt.replace(/\\n/g, '\n');
  var blob = new Blob([txt], { type: 'text/plain;charset=utf-8' });
  var url  = URL.createObjectURL(blob);
  var a    = document.createElement('a');
  a.href = url; a.download = 'rapport_RAM-AI.txt';
  document.body.appendChild(a); a.click();
  document.body.removeChild(a); URL.revokeObjectURL(url);
}
</script>
</body>
</html>
'@

# =============================================================================
# REMPLACEMENT DES TOKENS
# =============================================================================

$html = $htmlTemplate `
    -replace '##DATE##',               $dateStr `
    -replace '##NB_PHASES##',          $nbPhases.ToString() `
    -replace '##SWAP_STAT_LABEL##',    $swapStatLabel `
    -replace '##SWAP_REDUC_TEXT##',    $swapReducText `
    -replace '##SWAP_REDUC_COLOR##',   $swapReducColor `
    -replace '##SWAP_BORDER_COLOR##',  $swapBorderColor `
    -replace '##P1_AVG_RAM##',         $p1AvgRam.ToString($inv2) `
    -replace '##P1_MIN_RAM##',         $p1MinRam.ToString($inv2) `
    -replace '##P1_MAX_RAM##',         $p1MaxRam.ToString($inv2) `
    -replace '##P1_AVG_SWAP##',        $p1AvgSwap.ToString($inv2) `
    -replace '##P1_P95_SWAP##',        $p1P95Swap.ToString($inv2) `
    -replace '##P1_MAX_SWAP##',        $p1MaxSwap.ToString($inv2) `
    -replace '##P2_AVG_RAM##',         $p2AvgRam.ToString($inv2) `
    -replace '##P2_MIN_RAM##',         $p2MinRam.ToString($inv2) `
    -replace '##P2_MAX_RAM##',         $p2MaxRam.ToString($inv2) `
    -replace '##P2_AVG_SWAP##',        $p2AvgSwap.ToString($inv2) `
    -replace '##P2_P95_SWAP##',        $p2P95Swap.ToString($inv2) `
    -replace '##P2_MAX_SWAP##',        $p2MaxSwap.ToString($inv2) `
    -replace '##P2_AVG_CPU_RAI##',     $p2AvgCpuRai.ToString($inv2) `
    -replace '##P2_MAX_CPU_RAI##',     $p2MaxCpuRai.ToString($inv2) `
    -replace '##P2_GAIN_ABS##',        $p2GainAbs.ToString($inv2) `
    -replace '##P2_GAIN_SIGN##',       $p2GainSign `
    -replace '##P2_GAIN_PCT##',        $p2GainPct.ToString($inv2) `
    -replace '##P2_TOTAL_ACT##',       $p2TotalAct.ToString() `
    -replace '##P3_COL_STYLE##',       $p3ColStyle `
    -replace '##P3_AVG_RAM##',         $p3AvgRam.ToString($inv2) `
    -replace '##P3_MIN_RAM##',         $p3MinRam.ToString($inv2) `
    -replace '##P3_MAX_RAM##',         $p3MaxRam.ToString($inv2) `
    -replace '##P3_AVG_SWAP##',        $p3AvgSwap.ToString($inv2) `
    -replace '##P3_P95_SWAP##',        $p3P95Swap.ToString($inv2) `
    -replace '##P3_MAX_SWAP##',        $p3MaxSwap.ToString($inv2) `
    -replace '##P3_AVG_CPU_RAI##',     $p3AvgCpuRai.ToString($inv2) `
    -replace '##P3_MAX_CPU_RAI##',     $p3MaxCpuRai.ToString($inv2) `
    -replace '##P3_GAIN_ABS##',        $p3GainAbs.ToString($inv2) `
    -replace '##P3_GAIN_SIGN##',       $p3GainSign `
    -replace '##P3_GAIN_PCT##',        $p3GainPct.ToString($inv2) `
    -replace '##P3_TOTAL_ACT##',       $p3TotalAct.ToString() `
    -replace '##P4_COL_STYLE##',       $p4ColStyle `
    -replace '##P4_AVG_RAM##',         $p4AvgRam.ToString($inv2) `
    -replace '##P4_MIN_RAM##',         $p4MinRam.ToString($inv2) `
    -replace '##P4_MAX_RAM##',         $p4MaxRam.ToString($inv2) `
    -replace '##P4_AVG_SWAP##',        $p4AvgSwap.ToString($inv2) `
    -replace '##P4_P95_SWAP##',        $p4P95Swap.ToString($inv2) `
    -replace '##P4_MAX_SWAP##',        $p4MaxSwap.ToString($inv2) `
    -replace '##P4_AVG_CPU_RAI##',     $p4AvgCpuRai.ToString($inv2) `
    -replace '##P4_MAX_CPU_RAI##',     $p4MaxCpuRai.ToString($inv2) `
    -replace '##P4_GAIN_ABS##',        $p4GainAbs.ToString($inv2) `
    -replace '##P4_GAIN_SIGN##',       $p4GainSign `
    -replace '##P4_GAIN_PCT##',        $p4GainPct.ToString($inv2) `
    -replace '##P4_TOTAL_ACT##',       $p4TotalAct.ToString() `
    -replace '##SWAP_CHART_TITLE##',   $swapChartTitle `
    -replace '##VERDICT_COLOR##',      $verdictColor `
    -replace '##VERDICT_TEXT##',       $verdictText `
    -replace '##VERDICT_SWAP_COLOR##', $verdictSwapColor `
    -replace '##VERDICT_SWAP_TEXT##',  $verdictSwapText `
    -replace '##RPT_FIRST_LAUNCH##',   $rptFirstLaunch `
    -replace '##RPT_TOTAL_SESSIONS##', $rptTotalSessions.ToString() `
    -replace '##RPT_DUREE##',          $rptDuree `
    -replace '##RPT_TOTAL_RAM##',      $rptTotalRamFreedGb.ToString($inv2) `
    -replace '##RPT_TOTAL_PROCS##',    $rptTotalProcs.ToString() `
    -replace '##RPT_TODAY_RAM##',      $rptTodayRamFreed.ToString($inv2) `
    -replace '##RPT_TODAY_PROCS##',    $rptTodayProcs.ToString() `
    -replace '##RPT_GAMING_ACT##',     $rptGamingActivations.ToString() `
    -replace '##RPT_GAMING_RAM##',     $rptGamingRam.ToString($inv2) `
    -replace '##RPT_ECO_ACT##',        $rptEcoActivations.ToString() `
    -replace '##RPT_ECO_RAM##',        $rptEcoRam.ToString($inv2) `
    -replace '##RPT_TURBO##',          $rptTurbo.ToString() `
    -replace '##RPT_TOURNAMENT##',     $rptTournament.ToString() `
    -replace '##RPT_BEST_SESSION##',   $rptBestSession `
    -replace '##RPT_BEST_DATE##',      $rptBestSessionDate `
    -replace '##RPT_VERDICT##',        $rptVerdict `
    -replace '##X_AXIS_JS##',          $xAxisJs `
    -replace '##SWAP_LABELS_JS##',     $swapLabelsJs `
    -replace '##SWAP_DATA_JS##',       $swapDataJs `
    -replace '##SWAP_COLORS_JS##',     $swapColorsJs `
    -replace '##P1_RAM_JS##',          $p1RamJs `
    -replace '##P2_RAM_JS##',          $p2RamJs `
    -replace '##P3_RAM_JS##',          $p3RamJs `
    -replace '##P4_RAM_JS##',          $p4RamJs `
    -replace '##P2_CPU_JS##',          $p2CpuJs `
    -replace '##P3_CPU_JS##',          $p3CpuJs `
    -replace '##P4_CPU_JS##',          $p4CpuJs `
    -replace '##REPORT_TXT_ESCAPED##', $reportTxtEscaped

# =============================================================================
# ECRITURE + OUVERTURE
# =============================================================================

[System.IO.File]::WriteAllText($OutputFile, $html, [System.Text.UTF8Encoding]::new($true))

Write-Host ""
Write-Host "  =======================================================" -ForegroundColor Cyan
Write-Host "  Benchmark termine !" -ForegroundColor Cyan
Write-Host "  =======================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Phase 1  Sans RAM-AI : moy $p1AvgRam Go  (min $p1MinRam / max $p1MaxRam)" -ForegroundColor Red
Write-Host "  Phase 2  Mode Auto   : moy $p2AvgRam Go  Gain $p2GainSign$p2GainAbs Go ($p2GainSign$p2GainPct%)" -ForegroundColor Green
if ($hasGaming)  { Write-Host "  Phase 3  Mode Gaming : moy $p3AvgRam Go  Gain $p3GainSign$p3GainAbs Go ($p3GainSign$p3GainPct%)" -ForegroundColor Cyan }
if ($hasTournoi) { Write-Host "  Phase 4  Tournoi     : moy $p4AvgRam Go  Gain $p4GainSign$p4GainAbs Go ($p4GainSign$p4GainPct%)" -ForegroundColor Magenta }
Write-Host ""
Write-Host "  Rapport HTML : $OutputFile" -ForegroundColor Gray
Write-Host ""
Write-Host "  Ouverture du graphique dans le navigateur..." -ForegroundColor DarkGray

Start-Process $OutputFile

} finally {
    # Liberer la charge RAM dans tous les cas (meme en cas d'erreur)
    Write-Host ""
    Write-Host "  Liberation de la charge RAM..." -ForegroundColor DarkGray
    Stop-RamLoad

    # Liberer les PerformanceCounters
    if ($script:swapCounter)     { try { $script:swapCounter.Dispose()     } catch {} }
    if ($script:cpuCounter)      { try { $script:cpuCounter.Dispose()      } catch {} }
    if ($script:cpuRamAiCounter) { try { $script:cpuRamAiCounter.Dispose() } catch {} }
}
