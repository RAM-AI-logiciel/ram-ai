# =============================================================================
# RAM-AI — Benchmark comparatif RAM disponible
# Compare 5 minutes SANS vs 5 minutes AVEC RAM-AI
# Génère un graphique HTML interactif via Chart.js
# Compatibilité : PowerShell 5.1+
# =============================================================================

Set-StrictMode -Version 2

# ── Configuration ─────────────────────────────────────────────────────────────

$OutputDir     = "C:\ProgramData\RAM-AI"
$OutputFile    = Join-Path $OutputDir "benchmark_result.html"
$DurationSec   = 300   # 5 minutes
$IntervalSec   = 10    # mesure toutes les 10 s
$TotalPoints   = $DurationSec / $IntervalSec   # 30 points

# ── Fonctions utilitaires ──────────────────────────────────────────────────────

function Get-RamAvailableGb {
    $cs = Get-CimInstance -ClassName Win32_OperatingSystem
    $mb = [math]::Round($cs.FreePhysicalMemory / 1024, 0)
    return [math]::Round($mb / 1024, 2)
}

function Write-Header {
    Clear-Host
    Write-Host ""
    Write-Host "  ████████████████████████████████████████████████████" -ForegroundColor Cyan
    Write-Host "  █                                                  █" -ForegroundColor Cyan
    Write-Host "  █          RAM-AI  --  Benchmark comparatif        █" -ForegroundColor Cyan
    Write-Host "  █                                                  █" -ForegroundColor Cyan
    Write-Host "  ████████████████████████████████████████████████████" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Phase {
    param([string]$Title, [string]$Color)
    Write-Host ""
    Write-Host "  ─────────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host "  ─────────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host ""
}

function Measure-Phase {
    param([string]$PhaseName, [string]$BarColor)

    $samples = @()
    $elapsed = 0

    for ($i = 0; $i -lt $TotalPoints; $i++) {
        $elapsed    = $i * $IntervalSec
        $remaining  = $DurationSec - $elapsed
        $ram        = Get-RamAvailableGb
        $samples   += $ram

        # Barre de progression
        $done       = $i + 1
        $pct        = [math]::Round(($done / $TotalPoints) * 100)
        $barFilled  = [math]::Round($pct / 4)
        $barEmpty   = 25 - $barFilled
        $bar        = ("[" + ("=" * $barFilled) + (" " * $barEmpty) + "]")

        Write-Host ("`r  $PhaseName  $bar  $pct%   RAM dispo : $ram Go   ($remaining s restantes)") `
            -NoNewline -ForegroundColor $BarColor

        if ($i -lt ($TotalPoints - 1)) {
            Start-Sleep -Seconds $IntervalSec
        }
    }

    Write-Host ""
    return $samples
}

# ══════════════════════════════════════════════════════════════════════════════
# DÉMARRAGE
# ══════════════════════════════════════════════════════════════════════════════

Write-Header

Write-Host "  Ce script mesure la RAM disponible pendant 5 minutes SANS RAM-AI," -ForegroundColor Gray
Write-Host "  puis 5 minutes AVEC RAM-AI, et génère un graphique HTML comparatif." -ForegroundColor Gray
Write-Host ""
Write-Host "  Durée totale estimée : ~12 minutes (2 x 5 min + transitions)" -ForegroundColor DarkGray
Write-Host ""

# ── PHASE 1 : SANS RAM-AI ─────────────────────────────────────────────────────

Write-Phase "PHASE 1/2 — SANS RAM-AI" "Red"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Fermez le dashboard RAM-AI (clic droit icone systray → Quitter)" -ForegroundColor White
Write-Host "  2. Arrêtez le service si nécessaire (ou laissez-le tourner pour la" -ForegroundColor White
Write-Host "     baseline naturelle — à votre choix)" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est fermé pour démarrer la mesure..." -ForegroundColor Yellow
$null = Read-Host

Write-Host ""
Write-Host "  Début mesure SANS RAM-AI dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

$samplesWithout = Measure-Phase -PhaseName "SANS RAM-AI" -BarColor "Red"

$avgWithout = [math]::Round(($samplesWithout | Measure-Object -Average).Average, 2)
$minWithout = [math]::Round(($samplesWithout | Measure-Object -Minimum).Minimum, 2)
$maxWithout = [math]::Round(($samplesWithout | Measure-Object -Maximum).Maximum, 2)

Write-Host ""
Write-Host "  Phase 1 terminee : moy=$avgWithout Go  min=$minWithout Go  max=$maxWithout Go" `
    -ForegroundColor Green

# ── PAUSE ENTRE LES DEUX PHASES ───────────────────────────────────────────────

Write-Phase "PAUSE — Lancez RAM-AI" "Yellow"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Lancez le dashboard RAM-AI" -ForegroundColor White
Write-Host "  2. Attendez que le service Phase3 soit actif (statut 'Actif')" -ForegroundColor White
Write-Host "  3. Si vous voulez tester le Mode Tournoi, activez-le maintenant" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est actif pour démarrer la mesure..." -ForegroundColor Yellow
$null = Read-Host

Write-Host ""
Write-Host "  Début mesure AVEC RAM-AI dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

# ── PHASE 2 : AVEC RAM-AI ─────────────────────────────────────────────────────

Write-Phase "PHASE 2/2 — AVEC RAM-AI" "Green"

$samplesWith = Measure-Phase -PhaseName "AVEC RAM-AI" -BarColor "Green"

$avgWith = [math]::Round(($samplesWith | Measure-Object -Average).Average, 2)
$minWith = [math]::Round(($samplesWith | Measure-Object -Minimum).Minimum, 2)
$maxWith = [math]::Round(($samplesWith | Measure-Object -Maximum).Maximum, 2)

Write-Host ""
Write-Host "  Phase 2 terminee : moy=$avgWith Go  min=$minWith Go  max=$maxWith Go" `
    -ForegroundColor Green

# ── CALCUL DU GAIN ─────────────────────────────────────────────────────────────

$gainAbs = [math]::Round($avgWith - $avgWithout, 2)

if ($avgWithout -gt 0) {
    $gainPct = [math]::Round(($gainAbs / $avgWithout) * 100, 1)
} else {
    $gainPct = 0
}

if ($gainAbs -ge 0) {
    $gainSign = "+"
} else {
    $gainSign = ""
}

# ── GÉNÉRATION DES LABELS ET DONNÉES JAVASCRIPT ───────────────────────────────

# Construire les tableaux JS sans ternaire ni Join-String (PS 5.1)
$labelsArr     = @()
$dataWithout   = @()
$dataWith      = @()

for ($i = 0; $i -lt $TotalPoints; $i++) {
    $labelsArr   += ($i * $IntervalSec).ToString()
    $dataWithout += $samplesWithout[$i].ToString("F2", [System.Globalization.CultureInfo]::InvariantCulture)
    $dataWith    += $samplesWith[$i].ToString("F2", [System.Globalization.CultureInfo]::InvariantCulture)
}

$labelsJs     = $labelsArr   -join ","
$dataWithoutJs= $dataWithout -join ","
$dataWithJs   = $dataWith    -join ","

# ── VERDICT TEXTUEL ───────────────────────────────────────────────────────────

if ($gainAbs -gt 0.5) {
    $verdictText  = "RAM-AI a significativement amélioré la RAM disponible sur ce système."
    $verdictColor = "#4CAF50"
} elseif ($gainAbs -gt 0) {
    $verdictText  = "RAM-AI a légèrement amélioré la RAM disponible sur ce système."
    $verdictColor = "#8BC34A"
} elseif ($gainAbs -eq 0) {
    $verdictText  = "Aucune différence mesurée — la RAM était déjà optimale."
    $verdictColor = "#F5A623"
} else {
    $verdictText  = "Aucune amélioration mesurée sur cette configuration."
    $verdictColor = "#F44336"
}

# Formater la date pour HTML sans ternaire
$dateStr = (Get-Date).ToString("dd/MM/yyyy HH:mm:ss")

# ── CONSTRUCTION DU HTML ──────────────────────────────────────────────────────

$html = @"
<!DOCTYPE html>
<html lang="fr">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>RAM-AI — Benchmark comparatif</title>
  <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.2/dist/chart.umd.min.js"></script>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }

    body {
      background: #0d0d0d;
      color: #e0e0e0;
      font-family: 'Segoe UI', Arial, sans-serif;
      min-height: 100vh;
      padding: 32px 24px;
    }

    .container { max-width: 960px; margin: 0 auto; }

    /* ── Header ── */
    .header {
      display: flex;
      align-items: center;
      gap: 16px;
      margin-bottom: 28px;
    }
    .logo {
      width: 48px; height: 48px;
      background: linear-gradient(135deg, #3A7BD5, #1a3a7a);
      border-radius: 10px;
      display: flex; align-items: center; justify-content: center;
      font-size: 22px; font-weight: 900; color: #fff;
      letter-spacing: -1px;
    }
    .header h1 {
      font-size: 22px; font-weight: 700;
      color: #ffffff;
    }
    .header h1 span { color: #3A7BD5; }
    .header p { font-size: 13px; color: #888; margin-top: 3px; }

    /* ── Chart card ── */
    .card {
      background: #161616;
      border: 1px solid #2a2a2a;
      border-radius: 12px;
      padding: 28px 24px;
      margin-bottom: 20px;
    }

    .chart-wrap {
      position: relative;
      height: 380px;
    }

    /* ── Stats grid ── */
    .stats-grid {
      display: grid;
      grid-template-columns: 1fr 1fr 1fr;
      gap: 14px;
      margin-bottom: 20px;
    }

    .stat-card {
      background: #161616;
      border: 1px solid #2a2a2a;
      border-radius: 10px;
      padding: 18px 20px;
      text-align: center;
    }
    .stat-card .label { font-size: 11px; color: #666; text-transform: uppercase; letter-spacing: 0.8px; margin-bottom: 8px; }
    .stat-card .value { font-size: 28px; font-weight: 700; }
    .stat-card .sub   { font-size: 11px; color: #555; margin-top: 4px; }

    .without .value { color: #ef5350; }
    .with    .value { color: #66bb6a; }
    .gain    .value { color: #F5A623; }

    /* ── Ligne comparative ── */
    .compare-row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 14px;
      margin-bottom: 20px;
    }
    .compare-block {
      background: #161616;
      border: 1px solid #2a2a2a;
      border-radius: 10px;
      padding: 18px 20px;
    }
    .compare-block h3 {
      font-size: 12px; text-transform: uppercase; letter-spacing: 0.8px;
      margin-bottom: 12px; color: #888;
    }
    .compare-block table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .compare-block td { padding: 5px 0; }
    .compare-block td:last-child { text-align: right; font-weight: 600; }
    .red   { color: #ef5350; }
    .green { color: #66bb6a; }
    .gold  { color: #F5A623; }

    /* ── Verdict ── */
    .verdict {
      background: #161616;
      border: 1px solid #2a2a2a;
      border-radius: 10px;
      padding: 18px 24px;
      display: flex;
      align-items: center;
      gap: 14px;
      margin-bottom: 20px;
    }
    .verdict-icon { font-size: 28px; }
    .verdict-text { font-size: 14px; font-weight: 600; }

    /* ── Footer ── */
    .footer { text-align: center; font-size: 11px; color: #444; padding-top: 8px; }
    .footer a { color: #3A7BD5; text-decoration: none; }
  </style>
</head>
<body>
<div class="container">

  <!-- Header -->
  <div class="header">
    <div class="logo">R</div>
    <div>
      <h1><span>RAM-AI</span> — Benchmark comparatif</h1>
      <p>RAM disponible sur 5 minutes  •  Généré le $dateStr</p>
    </div>
  </div>

  <!-- Graphique -->
  <div class="card">
    <div class="chart-wrap">
      <canvas id="ramChart"></canvas>
    </div>
  </div>

  <!-- Stats principales -->
  <div class="stats-grid">
    <div class="stat-card without">
      <div class="label">Moyenne sans RAM-AI</div>
      <div class="value">$avgWithout Go</div>
      <div class="sub">min $minWithout Go &nbsp;•&nbsp; max $maxWithout Go</div>
    </div>
    <div class="stat-card with">
      <div class="label">Moyenne avec RAM-AI</div>
      <div class="value">$avgWith Go</div>
      <div class="sub">min $minWith Go &nbsp;•&nbsp; max $maxWith Go</div>
    </div>
    <div class="stat-card gain">
      <div class="label">Gain moyen</div>
      <div class="value">${gainSign}$gainAbs Go</div>
      <div class="sub">${gainSign}$gainPct % de RAM supplémentaire</div>
    </div>
  </div>

  <!-- Détail par phase -->
  <div class="compare-row">
    <div class="compare-block">
      <h3>🔴 Phase 1 — Sans RAM-AI</h3>
      <table>
        <tr><td>RAM disponible moyenne</td><td class="red">$avgWithout Go</td></tr>
        <tr><td>Valeur minimale</td><td class="red">$minWithout Go</td></tr>
        <tr><td>Valeur maximale</td><td class="red">$maxWithout Go</td></tr>
        <tr><td>Durée mesurée</td><td>300 secondes</td></tr>
        <tr><td>Nombre de points</td><td>$TotalPoints mesures (1/10s)</td></tr>
      </table>
    </div>
    <div class="compare-block">
      <h3>🟢 Phase 2 — Avec RAM-AI</h3>
      <table>
        <tr><td>RAM disponible moyenne</td><td class="green">$avgWith Go</td></tr>
        <tr><td>Valeur minimale</td><td class="green">$minWith Go</td></tr>
        <tr><td>Valeur maximale</td><td class="green">$maxWith Go</td></tr>
        <tr><td>Durée mesurée</td><td>300 secondes</td></tr>
        <tr><td>Nombre de points</td><td>$TotalPoints mesures (1/10s)</td></tr>
      </table>
    </div>
  </div>

  <!-- Verdict -->
  <div class="verdict" style="border-color: $verdictColor;">
    <div class="verdict-icon">📊</div>
    <div class="verdict-text" style="color: $verdictColor;">$verdictText</div>
  </div>

  <!-- Footer -->
  <div class="footer">
    RAM-AI v1.0 &nbsp;•&nbsp; Benchmark généré le $dateStr
    &nbsp;•&nbsp; <a href="https://ram-ai.app">ram-ai.app</a>
  </div>

</div>

<script>
(function() {
  var labels       = [$labelsJs];
  var dataWithout  = [$dataWithoutJs];
  var dataWith     = [$dataWithJs];

  var ctx = document.getElementById('ramChart').getContext('2d');

  Chart.defaults.color = '#888';
  Chart.defaults.borderColor = '#2a2a2a';

  new Chart(ctx, {
    type: 'line',
    data: {
      labels: labels,
      datasets: [
        {
          label: 'Sans RAM-AI',
          data: dataWithout,
          borderColor:     '#ef5350',
          backgroundColor: 'rgba(239,83,80,0.08)',
          borderWidth: 2.5,
          pointRadius: 3,
          pointHoverRadius: 6,
          pointBackgroundColor: '#ef5350',
          fill: true,
          tension: 0.3
        },
        {
          label: 'Avec RAM-AI',
          data: dataWith,
          borderColor:     '#66bb6a',
          backgroundColor: 'rgba(102,187,106,0.08)',
          borderWidth: 2.5,
          pointRadius: 3,
          pointHoverRadius: 6,
          pointBackgroundColor: '#66bb6a',
          fill: true,
          tension: 0.3
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        title: {
          display: true,
          text: 'RAM disponible en fonction du temps',
          color: '#cccccc',
          font: { size: 14, weight: '600' },
          padding: { bottom: 20 }
        },
        legend: {
          labels: {
            color: '#cccccc',
            font: { size: 13 },
            padding: 20,
            usePointStyle: true,
            pointStyleWidth: 14
          }
        },
        tooltip: {
          backgroundColor: '#1e1e1e',
          borderColor: '#3a3a3a',
          borderWidth: 1,
          titleColor: '#cccccc',
          bodyColor: '#aaaaaa',
          padding: 12,
          callbacks: {
            title: function(items) {
              return 't = ' + items[0].label + 's';
            },
            label: function(item) {
              return ' ' + item.dataset.label + ' : ' + item.parsed.y + ' Go';
            }
          }
        }
      },
      scales: {
        x: {
          title: {
            display: true,
            text: 'Temps (secondes)',
            color: '#666',
            font: { size: 12 }
          },
          ticks: {
            color: '#666',
            maxTicksLimit: 16,
            callback: function(val, idx) {
              return labels[idx] + 's';
            }
          },
          grid: { color: '#1e1e1e' }
        },
        y: {
          title: {
            display: true,
            text: 'RAM disponible (Go)',
            color: '#666',
            font: { size: 12 }
          },
          ticks: {
            color: '#666',
            callback: function(val) { return val + ' Go'; }
          },
          grid: { color: '#1e1e1e' }
        }
      }
    }
  });
})();
</script>
</body>
</html>
"@

# ── ÉCRITURE DU FICHIER ET OUVERTURE ──────────────────────────────────────────

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$html | Out-File -FilePath $OutputFile -Encoding utf8 -Force

Write-Host ""
Write-Host "  ════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Benchmark terminé !" -ForegroundColor Cyan
Write-Host "  ════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Résultats :" -ForegroundColor White
Write-Host "    SANS RAM-AI : moyenne $avgWithout Go" -ForegroundColor Red
Write-Host "    AVEC RAM-AI : moyenne $avgWith Go" -ForegroundColor Green
Write-Host "    Gain        : ${gainSign}$gainAbs Go  (${gainSign}$gainPct %)" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Rapport HTML : $OutputFile" -ForegroundColor Gray
Write-Host ""
Write-Host "  Ouverture du graphique dans le navigateur..." -ForegroundColor DarkGray

Start-Process $OutputFile
