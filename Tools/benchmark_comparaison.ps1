# =============================================================================
# RAM-AI - Benchmark comparatif RAM disponible
# Compare 5 minutes SANS vs 5 minutes AVEC RAM-AI
# Genere un graphique HTML interactif via Chart.js
# Compatibilite : PowerShell 5.1+
# =============================================================================

Set-StrictMode -Version 2

# -- Configuration -------------------------------------------------------------

$OutputDir   = "C:\ProgramData\RAM-AI"
$OutputFile  = Join-Path $OutputDir "benchmark_result.html"
$DurationSec = 300   # 5 minutes
$IntervalSec = 10    # mesure toutes les 10 s
$TotalPoints = $DurationSec / $IntervalSec   # 30 points

# -- Fonctions utilitaires -----------------------------------------------------

function Get-RamAvailableGb {
    $cs = Get-CimInstance -ClassName Win32_OperatingSystem
    $mb = [math]::Round($cs.FreePhysicalMemory / 1024, 0)
    return [math]::Round($mb / 1024, 2)
}

function Write-Header {
    Clear-Host
    Write-Host ""
    Write-Host "  ####################################################" -ForegroundColor Cyan
    Write-Host "  #                                                  #" -ForegroundColor Cyan
    Write-Host "  #          RAM-AI  --  Benchmark comparatif        #" -ForegroundColor Cyan
    Write-Host "  #                                                  #" -ForegroundColor Cyan
    Write-Host "  ####################################################" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Phase {
    param([string]$Title, [string]$Color)
    Write-Host ""
    Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host "  ---------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
}

function Measure-Phase {
    param([string]$PhaseName, [string]$BarColor)

    $samples = @()

    for ($i = 0; $i -lt $TotalPoints; $i++) {
        $elapsed   = $i * $IntervalSec
        $remaining = $DurationSec - $elapsed
        $ram       = Get-RamAvailableGb
        $samples  += $ram

        $done      = $i + 1
        $pct       = [math]::Round(($done / $TotalPoints) * 100)
        $barFilled = [math]::Round($pct / 4)
        $barEmpty  = 25 - $barFilled
        $bar       = "[" + ("=" * $barFilled) + (" " * $barEmpty) + "]"

        Write-Host ("`r  $PhaseName  $bar  $pct%   RAM dispo : $ram Go   ($remaining s restantes)") `
            -NoNewline -ForegroundColor $BarColor

        if ($i -lt ($TotalPoints - 1)) {
            Start-Sleep -Seconds $IntervalSec
        }
    }

    Write-Host ""
    return $samples
}

# =============================================================================
# DEMARRAGE
# =============================================================================

Write-Header

Write-Host "  Ce script mesure la RAM disponible pendant 5 min SANS RAM-AI," -ForegroundColor Gray
Write-Host "  puis 5 min AVEC RAM-AI, et genere un graphique HTML comparatif." -ForegroundColor Gray
Write-Host ""
Write-Host "  Duree totale estimee : ~12 minutes (2 x 5 min + transitions)" -ForegroundColor DarkGray
Write-Host ""

# -- PHASE 1 : SANS RAM-AI -----------------------------------------------------

Write-Phase "PHASE 1/2 - SANS RAM-AI" "Red"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Fermez le dashboard RAM-AI (clic droit icone systray -> Quitter)" -ForegroundColor White
Write-Host "  2. Attendez que le service soit arrete si necessaire" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est ferme pour demarrer la mesure..." -ForegroundColor Yellow
$null = Read-Host

Write-Host ""
Write-Host "  Debut mesure SANS RAM-AI dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

$samplesWithout = Measure-Phase -PhaseName "SANS RAM-AI" -BarColor "Red"

$avgWithout = [math]::Round(($samplesWithout | Measure-Object -Average).Average, 2)
$minWithout = [math]::Round(($samplesWithout | Measure-Object -Minimum).Minimum, 2)
$maxWithout = [math]::Round(($samplesWithout | Measure-Object -Maximum).Maximum, 2)

Write-Host ""
Write-Host "  Phase 1 terminee : moy=$avgWithout Go  min=$minWithout Go  max=$maxWithout Go" `
    -ForegroundColor Green

# -- PAUSE ENTRE LES DEUX PHASES -----------------------------------------------

Write-Phase "PAUSE - Lancez RAM-AI" "Yellow"

Write-Host "  ACTION REQUISE :" -ForegroundColor Yellow
Write-Host "  1. Lancez le dashboard RAM-AI" -ForegroundColor White
Write-Host "  2. Attendez que le service Phase3 soit actif (statut Actif)" -ForegroundColor White
Write-Host "  3. Si vous voulez tester le Mode Tournoi, activez-le maintenant" -ForegroundColor White
Write-Host ""
Write-Host "  Appuyez sur ENTREE quand RAM-AI est actif pour demarrer la mesure..." -ForegroundColor Yellow
$null = Read-Host

Write-Host ""
Write-Host "  Debut mesure AVEC RAM-AI dans 3 secondes..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

# -- PHASE 2 : AVEC RAM-AI -----------------------------------------------------

Write-Phase "PHASE 2/2 - AVEC RAM-AI" "Green"

$samplesWith = Measure-Phase -PhaseName "AVEC RAM-AI" -BarColor "Green"

$avgWith = [math]::Round(($samplesWith | Measure-Object -Average).Average, 2)
$minWith = [math]::Round(($samplesWith | Measure-Object -Minimum).Minimum, 2)
$maxWith = [math]::Round(($samplesWith | Measure-Object -Maximum).Maximum, 2)

Write-Host ""
Write-Host "  Phase 2 terminee : moy=$avgWith Go  min=$minWith Go  max=$maxWith Go" `
    -ForegroundColor Green

# -- CALCUL DU GAIN ------------------------------------------------------------

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

# -- TABLEAUX JS (sans Join-String, sans ternaire) -----------------------------

$labelsArr   = @()
$dataWithout = @()
$dataWith    = @()

for ($i = 0; $i -lt $TotalPoints; $i++) {
    $labelsArr   += ($i * $IntervalSec).ToString()
    $dataWithout += $samplesWithout[$i].ToString("F2", [System.Globalization.CultureInfo]::InvariantCulture)
    $dataWith    += $samplesWith[$i].ToString("F2", [System.Globalization.CultureInfo]::InvariantCulture)
}

$labelsJs      = $labelsArr   -join ","
$dataWithoutJs = $dataWithout -join ","
$dataWithJs    = $dataWith    -join ","

# -- VERDICT -------------------------------------------------------------------

if ($gainAbs -gt 0.5) {
    $verdictText  = "RAM-AI a significativement ameliore la RAM disponible sur ce systeme."
    $verdictColor = "#4CAF50"
} elseif ($gainAbs -gt 0) {
    $verdictText  = "RAM-AI a legerement ameliore la RAM disponible sur ce systeme."
    $verdictColor = "#8BC34A"
} elseif ($gainAbs -eq 0) {
    $verdictText  = "Aucune difference mesuree - la RAM etait deja optimale."
    $verdictColor = "#F5A623"
} else {
    $verdictText  = "Aucune amelioration mesuree sur cette configuration."
    $verdictColor = "#F44336"
}

$dateStr        = (Get-Date).ToString("dd/MM/yyyy HH:mm:ss")
$totalPointsStr = $TotalPoints.ToString()

# =============================================================================
# TEMPLATE HTML - single-quote here-string (@'...'@)
# Les variables PS ne sont PAS interpolees ici.
# On utilise des tokens ##NOM## remplaces ensuite par -replace.
# =============================================================================

$htmlTemplate = @'
<!DOCTYPE html>
<html lang="fr">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>RAM-AI - Benchmark comparatif</title>
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
    .header h1 { font-size: 22px; font-weight: 700; color: #ffffff; }
    .header h1 span { color: #3A7BD5; }
    .header p { font-size: 13px; color: #888; margin-top: 3px; }

    .card {
      background: #161616;
      border: 1px solid #2a2a2a;
      border-radius: 12px;
      padding: 28px 24px;
      margin-bottom: 20px;
    }
    .chart-wrap { position: relative; height: 380px; }

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
    .compare-block h3 { font-size: 12px; text-transform: uppercase; letter-spacing: 0.8px; margin-bottom: 12px; color: #888; }
    .compare-block table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .compare-block td { padding: 5px 0; }
    .compare-block td:last-child { text-align: right; font-weight: 600; }
    .red   { color: #ef5350; }
    .green { color: #66bb6a; }

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

    .footer { text-align: center; font-size: 11px; color: #444; padding-top: 8px; }
  </style>
</head>
<body>
<div class="container">

  <div class="header">
    <div class="logo">R</div>
    <div>
      <h1><span>RAM-AI</span> &mdash; Benchmark comparatif</h1>
      <p>RAM disponible sur 5 minutes &nbsp;&bull;&nbsp; ##DATE##</p>
    </div>
  </div>

  <div class="card">
    <div class="chart-wrap">
      <canvas id="ramChart"></canvas>
    </div>
  </div>

  <div class="stats-grid">
    <div class="stat-card without">
      <div class="label">Moyenne sans RAM-AI</div>
      <div class="value">##AVG_WITHOUT## Go</div>
      <div class="sub">min ##MIN_WITHOUT## Go &nbsp;&bull;&nbsp; max ##MAX_WITHOUT## Go</div>
    </div>
    <div class="stat-card with">
      <div class="label">Moyenne avec RAM-AI</div>
      <div class="value">##AVG_WITH## Go</div>
      <div class="sub">min ##MIN_WITH## Go &nbsp;&bull;&nbsp; max ##MAX_WITH## Go</div>
    </div>
    <div class="stat-card gain">
      <div class="label">Gain moyen</div>
      <div class="value">##GAIN_SIGN####GAIN_ABS## Go</div>
      <div class="sub">##GAIN_SIGN####GAIN_PCT## % de RAM supplementaire</div>
    </div>
  </div>

  <div class="compare-row">
    <div class="compare-block">
      <h3>Phase 1 &mdash; Sans RAM-AI</h3>
      <table>
        <tr><td>RAM disponible moyenne</td><td class="red">##AVG_WITHOUT## Go</td></tr>
        <tr><td>Valeur minimale</td>        <td class="red">##MIN_WITHOUT## Go</td></tr>
        <tr><td>Valeur maximale</td>        <td class="red">##MAX_WITHOUT## Go</td></tr>
        <tr><td>Duree mesuree</td>          <td>300 secondes</td></tr>
        <tr><td>Nombre de points</td>       <td>##TOTAL_POINTS## mesures (1/10s)</td></tr>
      </table>
    </div>
    <div class="compare-block">
      <h3>Phase 2 &mdash; Avec RAM-AI</h3>
      <table>
        <tr><td>RAM disponible moyenne</td> <td class="green">##AVG_WITH## Go</td></tr>
        <tr><td>Valeur minimale</td>         <td class="green">##MIN_WITH## Go</td></tr>
        <tr><td>Valeur maximale</td>         <td class="green">##MAX_WITH## Go</td></tr>
        <tr><td>Duree mesuree</td>           <td>300 secondes</td></tr>
        <tr><td>Nombre de points</td>        <td>##TOTAL_POINTS## mesures (1/10s)</td></tr>
      </table>
    </div>
  </div>

  <div class="verdict" style="border-color: ##VERDICT_COLOR##;">
    <div class="verdict-icon">&#x1F4CA;</div>
    <div class="verdict-text" style="color: ##VERDICT_COLOR##;">##VERDICT_TEXT##</div>
  </div>

  <div class="footer">RAM-AI v1.0 &nbsp;&bull;&nbsp; ##DATE##</div>

</div>
<script>
(function() {
  var labels      = [##LABELS_JS##];
  var dataWithout = [##DATA_WITHOUT_JS##];
  var dataWith    = [##DATA_WITH_JS##];

  var ctx = document.getElementById('ramChart').getContext('2d');

  Chart.defaults.color       = '#888';
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
'@

# =============================================================================
# REMPLACEMENT DES TOKENS par les valeurs calculees
# Chaque -replace travaille sur la chaine resultante en cascade.
# =============================================================================

$html = $htmlTemplate `
    -replace '##DATE##',           $dateStr `
    -replace '##AVG_WITHOUT##',    $avgWithout.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##MIN_WITHOUT##',    $minWithout.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##MAX_WITHOUT##',    $maxWithout.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##AVG_WITH##',       $avgWith.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##MIN_WITH##',       $minWith.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##MAX_WITH##',       $maxWith.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##GAIN_ABS##',       $gainAbs.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##GAIN_SIGN##',      $gainSign `
    -replace '##GAIN_PCT##',       $gainPct.ToString([System.Globalization.CultureInfo]::InvariantCulture) `
    -replace '##TOTAL_POINTS##',   $totalPointsStr `
    -replace '##VERDICT_COLOR##',  $verdictColor `
    -replace '##VERDICT_TEXT##',   $verdictText `
    -replace '##LABELS_JS##',      $labelsJs `
    -replace '##DATA_WITHOUT_JS##',$dataWithoutJs `
    -replace '##DATA_WITH_JS##',   $dataWithJs

# -- ECRITURE ET OUVERTURE -----------------------------------------------------

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$html | Out-File -FilePath $OutputFile -Encoding utf8 -Force

Write-Host ""
Write-Host "  =================================================" -ForegroundColor Cyan
Write-Host "  Benchmark termine !" -ForegroundColor Cyan
Write-Host "  =================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Resultats :" -ForegroundColor White
Write-Host "    SANS RAM-AI : moyenne $avgWithout Go" -ForegroundColor Red
Write-Host "    AVEC RAM-AI : moyenne $avgWith Go" -ForegroundColor Green
Write-Host "    Gain        : $gainSign$gainAbs Go  ($gainSign$gainPct %)" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Rapport HTML : $OutputFile" -ForegroundColor Gray
Write-Host ""
Write-Host "  Ouverture du graphique dans le navigateur..." -ForegroundColor DarkGray

Start-Process $OutputFile
