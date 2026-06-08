# embed_logo.ps1 — Intègre le logo en base64 dans index.html
# Double-cliquez ou exécutez dans PowerShell depuis C:\projettoto\RAM-AI\

$logoPath  = Join-Path $PSScriptRoot "Phase4\Assets\logo ramai.png"
$htmlPath  = Join-Path $PSScriptRoot "index.html"

if (-not (Test-Path $logoPath)) {
    Write-Host "ERREUR : logo introuvable à $logoPath" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $htmlPath)) {
    Write-Host "ERREUR : index.html introuvable à $htmlPath" -ForegroundColor Red
    exit 1
}

$bytes  = [System.IO.File]::ReadAllBytes($logoPath)
$b64    = [Convert]::ToBase64String($bytes)
$dataURI = "data:image/png;base64,$b64"

$html = Get-Content $htmlPath -Raw -Encoding UTF8

# Remplace le src du logo
$html = $html -replace 'src="logo_ram_ai\.png"', "src=`"$dataURI`""

[System.IO.File]::WriteAllText($htmlPath, $html, [System.Text.Encoding]::UTF8)

Write-Host "✅ Logo intégré en base64 dans index.html !" -ForegroundColor Green
Write-Host "   Vous pouvez maintenant déployer index.html seul sur GitHub Pages."
