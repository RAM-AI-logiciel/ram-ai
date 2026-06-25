# ==============================================================================
#  RAM-AI - Generateur de cles Ultra manuelles
#
#  Usage :
#    .\GenerateLicenceUltra.ps1              # 1 cle
#    .\GenerateLicenceUltra.ps1 -Count 5    # 5 cles
#
#  Format : ULT-XXXX-XXXX-XXXX-CCCC
#    XXXX = 4 chiffres hexadecimaux aleatoires (12 hex = 48 bits d'entropie)
#    CCCC = 2 premiers octets HMAC-SHA256(UltSalt, XXXXXXXXXXXX)
#
#  Compatible PowerShell 5.1+
#  Compatible avec LicenseService.cs (ValidateUltraKey / ValidateAsync).
# ==============================================================================

param(
    [int] $Count = 1
)

# DOIT correspondre a la constante UltSalt dans LicenseService.cs
$UltSalt    = "RAM-AI-ULT-2026-PRIVATE"
$outputFile = Join-Path $PSScriptRoot "ultra_manual_keys.txt"

# -- Fonction HMAC-SHA256 ------------------------------------------------------

function Get-HmacSha256Bytes {
    param([string]$KeyStr, [string]$Data)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new(
        [System.Text.Encoding]::UTF8.GetBytes($KeyStr))
    return $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Data))
}

# -- Generateur de cle Ultra ---------------------------------------------------
#  Format : ULT-g1-g2-g3-CCCC
#  g1, g2, g3 = 2 octets aleatoires en hex majuscule (4 chars chacun)
#  CCCC       = 2 premiers octets HMAC-SHA256(UltSalt, g1+g2+g3)

function New-UltraKey {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $buf = [byte[]]::new(6)
    $rng.GetBytes($buf)

    $g1 = $buf[0].ToString("X2") + $buf[1].ToString("X2")
    $g2 = $buf[2].ToString("X2") + $buf[3].ToString("X2")
    $g3 = $buf[4].ToString("X2") + $buf[5].ToString("X2")

    $data = "$g1$g2$g3"
    $hmac = Get-HmacSha256Bytes -KeyStr $UltSalt -Data $data
    $chk  = $hmac[0].ToString("X2") + $hmac[1].ToString("X2")

    return "ULT-$g1-$g2-$g3-$chk"
}

# -- Generation ----------------------------------------------------------------

$generated = [System.Collections.Generic.HashSet[string]]::new()
$keys      = @()

Write-Host ""
Write-Host "RAM-AI - Cles Ultra manuelles ($Count cles)" -ForegroundColor Magenta
Write-Host ("=" * 48) -ForegroundColor DarkGray
Write-Host ""

while ($keys.Count -lt $Count) {
    $key = New-UltraKey

    if ($generated.Add($key)) {
        $keys += $key
        Write-Host "  $key" -ForegroundColor Magenta
    }
}

# -- Sauvegarde (ajout au fichier existant) ------------------------------------

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$header    = ""
$header   += "# ============================================================"
$header   += "`n# RAM-AI Ultra Manual Keys - $timestamp"
$header   += "`n# $Count cle(s) generee(s)"
$header   += "`n# ============================================================"

$lines = @($header)
foreach ($k in $keys) {
    $lines += $k
}
$lines += ""

Add-Content -Path $outputFile -Value $lines -Encoding UTF8

Write-Host ""
Write-Host ("=" * 48) -ForegroundColor DarkGray
Write-Host "  $Count cle(s) generee(s)." -ForegroundColor Green
Write-Host "  Fichier : $outputFile"      -ForegroundColor Gray
Write-Host ""
Write-Host "  IMPORTANT : ne pas partager ce script hors de l'equipe de dev." -ForegroundColor Yellow
Write-Host "  Le sel est confidentiel."                                         -ForegroundColor Yellow
Write-Host ""
