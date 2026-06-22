# ==============================================================================
#  RAM-AI - Script de build protege (Obfuscar)
#  Usage : .\build_protected.ps1 [-Version "1.0.0-beta"] [-SkipObfuscation] [-SkipInstaller]
#
#  Etapes :
#    1. Publie Phase3 et Phase4 en Release
#    2. Lance Obfuscar sur les assemblies publiees
#    3. Copie les fichiers proteges dans Protected\
#    4. Lance Inno Setup pour generer RAM-AI-Setup-<Version>.exe
#
#  Pre-requis :
#    . .NET SDK 10 installe et dans le PATH
#    . Obfuscar  : dotnet tool install --global Obfuscar.GlobalTool
#    . Inno Setup 6+ installe dans Program Files (x86)\Inno Setup 6\
# ==============================================================================

param(
    [string] $Version         = "1.0.0",
    [switch] $SkipObfuscation,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -- Chemins -------------------------------------------------------------------

$Root          = "C:\projettoto\RAM-AI"
$Tools         = "$Root\Tools"
$ObfuscarXml   = "$Tools\obfuscar.xml"      # template config Obfuscar
$Protected     = "$Root\Protected"

$Phase3Proj    = "$Root\Phase3\Phase3.csproj"
$Phase4Proj    = "$Root\Phase4\Phase4.csproj"

# Phase3 publie avec -r win-x64 (WindowsService requis)
$Phase3Publish = "$Root\Phase3\bin\Release\net10.0-windows\win-x64\publish"
# Phase4 WPF publie sans RID explicite
$Phase4Publish = "$Root\Phase4\bin\Release\net10.0-windows\publish"

$SetupIss      = "$Root\Phase4\setup.iss"
$InstallerOut  = "$Root\installer"

# Obfuscar est installe comme dotnet global tool
$_cmd = Get-Command "obfuscar.console" -ErrorAction SilentlyContinue
$ObfuscarExe = if ($_cmd) { $_cmd.Source } else { "$env:USERPROFILE\.dotnet\tools\obfuscar.console.exe" }

# -- Helpers -------------------------------------------------------------------

function Write-Step([int]$n, [string]$msg) {
    Write-Host ""
    Write-Host "[$n/4] $msg" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor DarkGray
}

function Invoke-Cmd {
    param([string]$exe, [string[]]$arguments)
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Commande echouee (code=$LASTEXITCODE) : $exe $($arguments -join ' ')"
    }
}

function Get-WpfFrameworkPath {
    # Cherche la version la plus recente de Microsoft.WindowsDesktop.App
    $base = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    if (-not (Test-Path $base)) { return "" }
    $latest = Get-ChildItem $base -Directory |
              Sort-Object { [Version]($_.Name -replace '[^0-9.]','') } -Descending |
              Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return ""
}

function New-ObfuscarConfig {
    param(
        [string]$inPath,
        [string]$outPath,
        [string]$dllName,    # nom du fichier DLL geree (ex: RamAI.Phase4.dll)
        [string]$destXml
    )
    # Chemin absolu vers la DLL : evite toute ambiguite sur InPath
    $fullDll = Join-Path $inPath $dllName

    # Chemin WPF runtime (pour que Obfuscar resolve PresentationFramework, etc.)
    $wpfPath = Get-WpfFrameworkPath

    $content = Get-Content $ObfuscarXml -Raw -Encoding UTF8
    $content = $content.Replace('$INPATH',             $inPath)
    $content = $content.Replace('$OUTPATH',            $outPath)
    $content = $content.Replace('$FULLDLL',            $fullDll)
    $content = $content.Replace('$WPF_FRAMEWORK_PATH', $wpfPath)
    Set-Content $destXml $content -Encoding UTF8
    Write-Host "  Config generee : $destXml"
    Write-Host "  DLL cible      : $fullDll"
    Write-Host "  Sortie         : $outPath"
    if ($wpfPath) { Write-Host "  WPF runtime    : $wpfPath" }
}

function Copy-PublishDir([string]$src, [string]$dst) {
    Write-Host "  Copie : $src -> $dst"
    if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }
    Copy-Item "$src\*" $dst -Recurse -Force
}

# -- Etape 1 : Publish ---------------------------------------------------------

Write-Step 1 "Publication des assemblies (.NET 10 Release)"

Write-Host "  dotnet publish Phase3 (win-x64)..."
Invoke-Cmd -exe "dotnet" -arguments @(
    "publish", $Phase3Proj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "false",
    "--nologo"
)

Write-Host "  dotnet publish Phase4..."
Invoke-Cmd -exe "dotnet" -arguments @(
    "publish", $Phase4Proj,
    "-c", "Release",
    "--nologo"
)

Write-Host ""
Write-Host "  Phase3 publie -> $Phase3Publish"
Write-Host "  Phase4 publie -> $Phase4Publish"

# -- Etape 2 : Preparer Protected\ ---------------------------------------------

Write-Step 2 "Preparation du dossier Protected\"

if (Test-Path $Protected) {
    Remove-Item $Protected -Recurse -Force
    Write-Host "  Ancien contenu Protected\ supprime."
}
New-Item -ItemType Directory -Path "$Protected\Phase3" | Out-Null
New-Item -ItemType Directory -Path "$Protected\Phase4" | Out-Null
Write-Host "  Dossiers Protected\Phase3 et Protected\Phase4 crees."

# -- Etape 3 : Obfuscation Obfuscar --------------------------------------------

Write-Step 3 "Obfuscation avec Obfuscar (compatible .NET 10)"

$obfuscationOk = $false

if ($SkipObfuscation) {
    Write-Host "  [SKIP] Obfuscation ignoree (-SkipObfuscation)" -ForegroundColor Yellow
}
elseif (-not (Test-Path $ObfuscarExe)) {
    Write-Host "  [WARN] Obfuscar introuvable : $ObfuscarExe" -ForegroundColor Yellow
    Write-Host "    Installer avec : dotnet tool install --global Obfuscar.GlobalTool" -ForegroundColor Gray
    Write-Host "    Build continue en mode copie non-obfusquee." -ForegroundColor Yellow
}
else {
    Write-Host "  Obfuscar : $ObfuscarExe"

    # Dossiers de sortie temporaires (Obfuscar exige un sous-dossier 'Obfuscated')
    $Phase3ObfOut = "$Protected\Phase3"
    $Phase4ObfOut = "$Protected\Phase4"

    # -- Phase3 ----------------------------------------------------------------
    $cfgPhase3 = "$Tools\_obfuscar_phase3.xml"
    New-ObfuscarConfig `
        -inPath  $Phase3Publish `
        -outPath $Phase3ObfOut `
        -dllName "RamAI.Phase3.dll" `
        -destXml $cfgPhase3

    Write-Host "  Obfuscation Phase3..."
    try {
        Invoke-Cmd -exe $ObfuscarExe -arguments @($cfgPhase3)
        Write-Host "  [OK] Phase3 obfusquee -> $Phase3ObfOut" -ForegroundColor Green
    }
    catch {
        Write-Warning "Obfuscation Phase3 echouee : $_"
        Write-Warning "Copie sans obfuscation pour Phase3."
        Copy-PublishDir $Phase3Publish $Phase3ObfOut
    }

    # -- Phase4 ----------------------------------------------------------------
    $cfgPhase4 = "$Tools\_obfuscar_phase4.xml"
    New-ObfuscarConfig `
        -inPath  $Phase4Publish `
        -outPath $Phase4ObfOut `
        -dllName "RamAI.Phase4.dll" `
        -destXml $cfgPhase4

    Write-Host "  Obfuscation Phase4..."
    try {
        Invoke-Cmd -exe $ObfuscarExe -arguments @($cfgPhase4)
        Write-Host "  [OK] Phase4 obfusquee -> $Phase4ObfOut" -ForegroundColor Green
        $obfuscationOk = $true
    }
    catch {
        Write-Warning "Obfuscation Phase4 echouee : $_"
        Write-Warning "Copie sans obfuscation pour Phase4."
        Copy-PublishDir $Phase4Publish $Phase4ObfOut
    }

    # Copier TOUS les fichiers publish que Obfuscar n'a pas deja ecrits dans Protected\.
    # Obfuscar ecrit uniquement la DLL cible obfusquee — les DLLs tierces,
    # l'apphost natif (.exe), les configs et les pdbs doivent tous etre copies.
    Write-Host "  Copie des fichiers annexes (DLLs tierces, apphost, configs)..."

    function Copy-MissingFiles([string]$src, [string]$dst) {
        foreach ($item in (Get-ChildItem $src -Recurse |
                            Where-Object { -not $_.PSIsContainer })) {
            $rel    = $item.FullName.Substring($src.Length + 1)
            $target = Join-Path $dst $rel
            # Ne pas ecraser ce qu'Obfuscar a deja produit (la DLL obfusquee)
            if (-not (Test-Path $target)) {
                $dir = Split-Path $target -Parent
                if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
                Copy-Item $item.FullName $target -Force
            }
        }
    }

    Copy-MissingFiles $Phase3Publish "$Protected\Phase3"
    Copy-MissingFiles $Phase4Publish "$Protected\Phase4"

    # Nettoyer configs temporaires
    Remove-Item $cfgPhase3, $cfgPhase4 -ErrorAction SilentlyContinue
}

# Fallback total : copie brute si obfuscation non disponible
if (-not $obfuscationOk -and $SkipObfuscation) {
    Copy-PublishDir $Phase3Publish "$Protected\Phase3"
    Copy-PublishDir $Phase4Publish "$Protected\Phase4"
}
elseif (-not $obfuscationOk -and -not (Test-Path $ObfuscarExe)) {
    Copy-PublishDir $Phase3Publish "$Protected\Phase3"
    Copy-PublishDir $Phase4Publish "$Protected\Phase4"
}

# Rapport
$cntP3 = (Get-ChildItem "$Protected\Phase3" -Recurse -File).Count
$cntP4 = (Get-ChildItem "$Protected\Phase4" -Recurse -File).Count
Write-Host ""
Write-Host "  Protected\Phase3 : $cntP3 fichiers"
Write-Host "  Protected\Phase4 : $cntP4 fichiers"

# -- Etape 4 : Inno Setup ------------------------------------------------------

Write-Step 4 "Generation de l'installeur Inno Setup"

if ($SkipInstaller) {
    Write-Host "  [SKIP] Installeur ignore (-SkipInstaller)" -ForegroundColor Yellow
}
else {
    $isccCandidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup introuvable."
        Write-Warning "Telecharger depuis https://jrsoftware.org/isinfo.php"
        Write-Warning "Fichiers proteges disponibles dans : $Protected\"
    }
    else {
        Write-Host "  ISCC : $iscc"
        if (-not (Test-Path $InstallerOut)) {
            New-Item -ItemType Directory -Path $InstallerOut | Out-Null
        }

        Invoke-Cmd -exe $iscc -arguments @(
            $SetupIss,
            "/DProtectedPhase3=$Protected\Phase3",
            "/DProtectedPhase4=$Protected\Phase4",
            "/DAppVersion=$Version",
            "/O$InstallerOut"
        )

        $installer = Get-ChildItem $InstallerOut -Filter "RAM-AI-Setup*" |
                     Sort-Object LastWriteTime -Descending |
                     Select-Object -First 1

        if ($installer) {
            Write-Host ""
            Write-Host "  [OK] $($installer.FullName)" -ForegroundColor Green
        }
    }
}

# -- Resume --------------------------------------------------------------------

Write-Host ""
Write-Host ("=" * 54) -ForegroundColor Green
Write-Host "  Build protege termine -> RAM-AI-Setup.exe pret  " -ForegroundColor Green
Write-Host ("=" * 54) -ForegroundColor Green
Write-Host ""
Write-Host "  Obfuscation : $(if ($obfuscationOk) { 'Obfuscar OK' } else { 'Copie (sans obfuscation)' })"
Write-Host "  Protected\  : $Protected"
Write-Host "  Installer\  : $InstallerOut"
Write-Host ""
