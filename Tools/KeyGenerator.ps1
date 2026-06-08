# ==============================================================================
#  RAM-AI - Generateur de cles Pro / Beta / Ultra
#
#  Usage :
#    .\KeyGenerator.ps1                        # 10 cles Beta
#    .\KeyGenerator.ps1 -Count 5               # 5 cles Beta
#    .\KeyGenerator.ps1 -Count 5 -Tier Pro     # 5 cles Pro  (P-XXXX-XXXX)
#    .\KeyGenerator.ps1 -Count 5 -Tier Ultra   # 5 cles Ultra (ULT-)
#    .\KeyGenerator.ps1 -Tier Ultra             # 10 cles Ultra
#
#  Format Pro   : P-XXXX-XXXX
#    XXXX = 4 chars [A-Z0-9] aleatoires ; SHA-256(cle + ProSalt)[0..2] % 251 == 0
#    (brute-force ~251 tentatives en moyenne par cle)
#
#  Format Beta  : BETA-XXXX-XXXX-XXXX-CCCC
#  Format Ultra : ULT-XXXX-XXXX-XXXX-CCCC
#    XXXX = 4 chiffres hexadecimaux aleatoires (12 hex = 48 bits d'entropie)
#    CCCC = 2 premiers octets HMAC-SHA256(Salt, XXXXXXXXXXXX)
#
#  Compatible avec LicenseService.cs (Validate / ValidateBetaKey / ValidateUltraKey).
# ==============================================================================

param(
    [int]    $Count = 10,
    [string] $Tier  = "Beta"   # "Pro", "Beta" ou "Ultra"
)

# DOIT correspondre aux constantes dans LicenseService.cs
$ProSalt  = "RAM-AI-2026"
$BetaSalt = "RAM-AI-BETA-2026-PRIVATE"
$UltSalt  = "RAM-AI-ULT-2026-PRIVATE"

# -- Derivation des parametres selon le tier -----------------------------------

switch ($Tier) {
    "Pro" {
        $outputFile = Join-Path $PSScriptRoot "pro_keys.txt"
        $tierName   = "Pro"
        $keyColor   = "Green"
        $warnColor  = "Green"
    }
    "Ultra" {
        $outputFile = Join-Path $PSScriptRoot "ultra_keys.txt"
        $tierName   = "Ultra"
        $keyColor   = "Magenta"
        $warnColor  = "Magenta"
    }
    default {
        $Tier       = "Beta"
        $outputFile = Join-Path $PSScriptRoot "beta_keys.txt"
        $tierName   = "Beta Testeur"
        $keyColor   = "Cyan"
        $warnColor  = "Yellow"
    }
}

# -- Fonction HMAC-SHA256 ------------------------------------------------------

function Get-HmacSha256 {
    param([string]$keyStr, [string]$data)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new(
        [System.Text.Encoding]::UTF8.GetBytes($keyStr))
    return $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($data))
}

# -- Generateur HMAC (Beta / Ultra) --------------------------------------------
#  Format : PREFIX-XXXX-XXXX-XXXX-CCCC

function New-HmacKey {
    param([string]$KeyPrefix, [string]$Salt)

    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $buf = [byte[]]::new(6)
    $rng.GetBytes($buf)

    $g1 = ($buf[0..1] | ForEach-Object { $_.ToString("X2") }) -join ""
    $g2 = ($buf[2..3] | ForEach-Object { $_.ToString("X2") }) -join ""
    $g3 = ($buf[4..5] | ForEach-Object { $_.ToString("X2") }) -join ""

    $data = "$g1$g2$g3"
    $hmac = Get-HmacSha256 -keyStr $Salt -data $data
    $chk  = $hmac[0].ToString("X2") + $hmac[1].ToString("X2")

    return "$KeyPrefix-$g1-$g2-$g3-$chk"
}

# -- Generateur SHA-256 % 251 (Pro) --------------------------------------------
#  Format : P-XXXX-XXXX  (XXXX = 4 chars [A-Z0-9])
#  Condition : SHA256("P-XXXX-XXXX" + ProSalt)[0] + [1] + [2]) % 251 == 0
#  Brute-force : ~251 tentatives en moyenne.

function New-ProKey {
    param([string]$Salt)

    # PS 5.1 : pas de range char — on construit la chaine directement
    $charset = [char[]]"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"  # 36 caracteres
    $rng     = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $buf     = [byte[]]::new(8)   # 4 octets par groupe × 2 groupes = 8

    while ($true) {
        $rng.GetBytes($buf)
        # Chaque groupe : 4 octets → 4 chars [A-Z0-9]
        $g1 = -join ($buf[0..3] | ForEach-Object { $charset[$_ % 36] })
        $g2 = -join ($buf[4..7] | ForEach-Object { $charset[$_ % 36] })

        $candidate = "P-$g1-$g2"
        $hashInput = [System.Text.Encoding]::UTF8.GetBytes($candidate + $Salt)
        $hash      = [System.Security.Cryptography.SHA256]::Create().ComputeHash($hashInput)

        if (($hash[0] + $hash[1] + $hash[2]) % 251 -eq 0) {
            return $candidate
        }
    }
}

# -- Generation ----------------------------------------------------------------

$generated = [System.Collections.Generic.HashSet[string]]::new()
$keys      = @()

Write-Host ""
Write-Host "RAM-AI - Cles $tierName ($Count cles)" -ForegroundColor $warnColor
Write-Host ("=" * 48) -ForegroundColor DarkGray
Write-Host ""

while ($keys.Count -lt $Count) {
    $key = switch ($Tier) {
        "Pro"   { New-ProKey  -Salt $ProSalt }
        "Ultra" { New-HmacKey -KeyPrefix "ULT"  -Salt $UltSalt  }
        default { New-HmacKey -KeyPrefix "BETA" -Salt $BetaSalt }
    }

    if ($generated.Add($key)) {
        $keys += $key
        Write-Host "  $key" -ForegroundColor $keyColor
    }
}

# -- Sauvegarde ----------------------------------------------------------------

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$header    = "# RAM-AI $tierName Keys - Genere le $timestamp`n# $Count cles`n"
$content   = $header + ($keys -join "`n")
Set-Content $outputFile $content -Encoding UTF8

Write-Host ""
Write-Host ("=" * 48) -ForegroundColor DarkGray
Write-Host "  $Count cles generees." -ForegroundColor Green
Write-Host "  Fichier : $outputFile"  -ForegroundColor Gray
Write-Host ""
Write-Host "  IMPORTANT : ne pas partager ce script hors de l'equipe de dev." -ForegroundColor $warnColor
Write-Host "  Les sels sont confidentiels."                                    -ForegroundColor $warnColor
Write-Host ""
