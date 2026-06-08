# ══════════════════════════════════════════════════════════════
# deploy.ps1 — RAM-AI : embed logo + déploiement GitHub Pages
# Exécuter depuis C:\projettoto\RAM-AI\
# ══════════════════════════════════════════════════════════════

Set-Location $PSScriptRoot
$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n▶ $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "  ✅ $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "  ❌ $msg" -ForegroundColor Red }

# ── ÉTAPE 1 : Intégrer le logo en base64 ─────────────────────
Write-Step "Intégration du logo en base64 dans index.html..."

$logoPath = Join-Path $PSScriptRoot "Phase4\Assets\logo ramai.png"
$htmlPath = Join-Path $PSScriptRoot "index.html"

if (-not (Test-Path $logoPath)) {
    Write-Err "Logo introuvable : $logoPath"
    exit 1
}
if (-not (Test-Path $htmlPath)) {
    Write-Err "index.html introuvable : $htmlPath"
    exit 1
}

$bytes   = [System.IO.File]::ReadAllBytes($logoPath)
$b64     = [Convert]::ToBase64String($bytes)
$dataURI = "data:image/png;base64,$b64"

$html = Get-Content $htmlPath -Raw -Encoding UTF8

# Remplace n'importe quel src="..." sur la balise hero-logo
$html = $html -replace '(?s)(class="hero-logo"[^>]*?)src="[^"]*"', '$1src="PLACEHOLDER"'
$html = $html -replace 'src="PLACEHOLDER"', "src=`"$dataURI`""

[System.IO.File]::WriteAllText($htmlPath, $html, [System.Text.Encoding]::UTF8)
Write-OK "Logo intégré ($([Math]::Round($bytes.Length/1KB)) Ko)."

# ── ÉTAPE 2 : Vérifier que git est disponible ─────────────────
Write-Step "Vérification de git..."
try {
    $gitVersion = git --version 2>&1
    Write-OK $gitVersion
} catch {
    Write-Err "git non trouvé. Installez Git for Windows : https://git-scm.com"
    exit 1
}

# ── ÉTAPE 3 : Init git si nécessaire ─────────────────────────
Write-Step "Vérification du dépôt git..."
if (-not (Test-Path (Join-Path $PSScriptRoot ".git"))) {
    Write-Host "  Aucun dépôt git trouvé. Initialisation..." -ForegroundColor Yellow
    git init
    Write-Host ""
    Write-Host "  ⚠️  Vous devez lier un dépôt GitHub remote :" -ForegroundColor Yellow
    Write-Host "       git remote add origin https://github.com/VOTRE-USER/VOTRE-REPO.git" -ForegroundColor White
    Write-Host "  Ensuite relancez ce script." -ForegroundColor Yellow
    exit 0
} else {
    $remote = git remote get-url origin 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ⚠️  Aucun remote 'origin' configuré." -ForegroundColor Yellow
        Write-Host "       Exécutez : git remote add origin https://github.com/VOTRE-USER/VOTRE-REPO.git" -ForegroundColor White
        exit 0
    }
    Write-OK "Remote origin : $remote"
}

# ── ÉTAPE 4 : git add + commit + push ─────────────────────────
Write-Step "Déploiement sur GitHub Pages..."

git add index.html
if ($LASTEXITCODE -ne 0) { Write-Err "git add échoué."; exit 1 }

$commitMsg = "Site RAM-AI - logo base64 integre - $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
git commit -m $commitMsg
if ($LASTEXITCODE -ne 0) {
    Write-Host "  (Rien à committer, ou premier commit)" -ForegroundColor Yellow
}

# Détecte la branche courante
$branch = git rev-parse --abbrev-ref HEAD 2>&1
if (-not $branch) { $branch = "main" }

git push -u origin $branch
if ($LASTEXITCODE -ne 0) {
    Write-Err "Push échoué. Vérifiez vos identifiants GitHub."
    exit 1
}
Write-OK "Push réussi sur la branche '$branch'."

# ── ÉTAPE 5 : Afficher l'URL GitHub Pages ────────────────────
Write-Step "URL de votre site GitHub Pages..."
$remoteUrl = git remote get-url origin
# Convertit l'URL git en URL Pages
$pagesUrl = $remoteUrl `
    -replace 'https://github\.com/', 'https://' `
    -replace '\.git$', '' `
    -replace 'github\.com/([^/]+)/([^/]+)', '$1.github.io/$2'

Write-Host ""
Write-Host "  🌐 Votre site sera disponible dans ~30 secondes :" -ForegroundColor White
Write-Host "     $pagesUrl" -ForegroundColor Yellow
Write-Host ""
Write-Host "  ⚙️  Si ce n'est pas encore fait, activez GitHub Pages dans :" -ForegroundColor White
Write-Host "     Settings → Pages → Branch: $branch / root" -ForegroundColor White
Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host "  Déploiement terminé avec succès ! 🚀" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════" -ForegroundColor DarkGray
