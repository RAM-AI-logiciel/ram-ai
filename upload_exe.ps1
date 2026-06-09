# ==============================================================
# upload_exe.ps1 -- Upload RAM-AI-Setup-1.0.0.exe sur GitHub
# Executer depuis C:\projettoto\RAM-AI\
# ==============================================================
# ETAPES :
#   1. Creez un token GitHub sur https://github.com/settings/tokens/new
#      Cochez : repo (acces complet)
#   2. Collez votre token a la ligne GITHUB_TOKEN ci-dessous
#   3. Clic droit -> "Executer avec PowerShell"
# ==============================================================

$GITHUB_TOKEN = "COLLER_VOTRE_TOKEN_ICI"   # <- remplacez ceci
$OWNER        = "RAM-AI-logiciel"
$REPO         = "ram-ai"
$FILE_PATH    = "RAM-AI-Setup-1.0.0.exe"
$LOCAL_FILE   = Join-Path $PSScriptRoot "installer\RAM-AI-Setup-1.0.0.exe"
$BRANCH       = "main"
$COMMIT_MSG   = "Ajout installeur RAM-AI"

# -- Validation -------------------------------------------------------

if ($GITHUB_TOKEN -eq "COLLER_VOTRE_TOKEN_ICI") {
    Write-Host "[ERREUR] Remplacez COLLER_VOTRE_TOKEN_ICI par votre token GitHub." `
        -ForegroundColor Red
    Write-Host "         Creez un token sur : https://github.com/settings/tokens/new" `
        -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $LOCAL_FILE)) {
    Write-Host "[ERREUR] Fichier introuvable : $LOCAL_FILE" -ForegroundColor Red
    exit 1
}

# -- Encodage base64 --------------------------------------------------

Write-Host ""
Write-Host "[1/3] Lecture de l'installeur..." -ForegroundColor Cyan
$bytes  = [System.IO.File]::ReadAllBytes($LOCAL_FILE)
$base64 = [Convert]::ToBase64String($bytes)
$sizeMb = [Math]::Round($bytes.Length / 1MB, 1)
Write-Host "  OK  $sizeMb Mo encode en base64." -ForegroundColor Green

# -- Headers API GitHub -----------------------------------------------

$headers = @{
    "Authorization"        = "token $GITHUB_TOKEN"
    "Accept"               = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
}

$apiUrl = "https://api.github.com/repos/$OWNER/$REPO/contents/$FILE_PATH"

# -- Verifier si le fichier existe deja (SHA requis pour mise a jour) -

Write-Host ""
Write-Host "[2/3] Verification du fichier existant..." -ForegroundColor Cyan

$sha = $null
try {
    $existing = Invoke-RestMethod -Uri $apiUrl -Headers $headers `
        -Method Get -ErrorAction SilentlyContinue
    $sha      = $existing.sha
    $shaShort = $sha.Substring(0, 8)
    Write-Host "  INFO Fichier existant detecte (SHA: $shaShort...) -- mise a jour." `
        -ForegroundColor Yellow
}
catch {
    Write-Host "  INFO Nouveau fichier -- creation." -ForegroundColor Yellow
}

# -- Upload -----------------------------------------------------------

Write-Host ""
Write-Host "[3/3] Upload vers GitHub..." -ForegroundColor Cyan

$body = @{
    message = $COMMIT_MSG
    content = $base64
    branch  = $BRANCH
}
if ($sha) { $body.sha = $sha }

$bodyJson = $body | ConvertTo-Json -Depth 3

try {
    $result = Invoke-RestMethod -Uri $apiUrl -Headers $headers `
        -Method Put -Body $bodyJson -ContentType "application/json"

    $downloadUrl = "https://github.com/$OWNER/$REPO/raw/main/$FILE_PATH"

    Write-Host ""
    Write-Host "=================================================" -ForegroundColor DarkGray
    Write-Host "  OK  Installeur uploade avec succes !" -ForegroundColor Green
    Write-Host "  URL de telechargement direct :" -ForegroundColor White
    Write-Host "  $downloadUrl" -ForegroundColor Yellow
    Write-Host "=================================================" -ForegroundColor DarkGray
}
catch {
    $errMsg = $_.Exception.Message
    Write-Host ""
    Write-Host "[ERREUR] Upload echoue : $errMsg" -ForegroundColor Red
    Write-Host "         Verifiez que votre token a les droits 'repo'." -ForegroundColor Yellow
    exit 1
}
