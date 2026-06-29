; ─────────────────────────────────────────────────────────────────────────────
;  RAM-AI — Inno Setup 6.1+
;  Usage : iscc setup.iss
;  Sortie : installer\RAM-AI-Setup-1.0.0.exe
;
;  Build framework-dependent : l'installeur est léger (~10 Mo).
;  Si .NET 10 Runtime x64 est absent, il est téléchargé automatiquement
;  depuis les serveurs Microsoft (fichier signé, ~57 Mo).
;  Section [Code] : téléchargement via CreateDownloadPage (Inno Setup 6.1+).
; ─────────────────────────────────────────────────────────────────────────────

#define AppName    "RAM-AI"
#define AppVersion "1.0.0"
#define AppExe     "RamAI.Phase4.exe"
#define ServiceExe "RamAI.Phase3.exe"

; ── Sources des assemblies ────────────────────────────────────────────────────
; Par défaut : dossiers publish standard.
; Le script build_protected.ps1 passe /DProtectedPhase3=... /DProtectedPhase4=...
; pour pointer vers les assemblies obfusquées dans Protected\.
#ifndef ProtectedPhase3
  #define ProtectedPhase3 "..\Phase3\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef ProtectedPhase4
  ; Phase4 publie sans RID → pas de sous-dossier win-x64 dans le chemin
  #define ProtectedPhase4 "..\Phase4\bin\Release\net10.0-windows\publish"
#endif

#define DotNetUrl    "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe"
#define DotNetFile   "dotnet-runtime-win-x64.exe"
#define DotNetDlPage "https://dotnet.microsoft.com/download/dotnet/10.0"

[Setup]
AppId={{E4B3F2A1-9C7D-4E8F-B1A2-3C5D6E7F8A9B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=RAM-AI Technologies
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=installer
OutputBaseFilename=RAM-AI-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.19041
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\Phase4\{#AppExe}
SetupIconFile=Assets\RAM-AI.ico

[Tasks]
Name: desktopicon;          Description: "Raccourci RAM-AI Dashboard sur le Bureau";   GroupDescription: "Raccourcis :"
Name: benchmarkdesktopicon; Description: "Raccourci RAM-AI Benchmark sur le Bureau";   GroupDescription: "Raccourcis :"; Flags: unchecked
Name: autostart;            Description: "Démarrer RAM-AI automatiquement avec Windows"; GroupDescription: "Options :"; Flags: checkedonce

[Dirs]
Name: "{app}\Phase2\model"
Name: "{app}\Phase3\cache"
Name: "{app}\Phase3\logs"
Name: "{app}\Phase4"

[Files]
; Phase4 et Phase3 pointent vers Protected\ si build_protected.ps1 a passé /D,
; sinon vers les dossiers publish standard.
Source: "{#ProtectedPhase4}\*"; DestDir: "{app}\Phase4"; Flags: recursesubdirs ignoreversion
Source: "{#ProtectedPhase3}\*"; DestDir: "{app}\Phase3"; Flags: recursesubdirs ignoreversion
Source: "..\Phase2\model\ram-ai.zip"; DestDir: "{app}\Phase2\model"; Flags: ignoreversion
; Outil benchmark comparatif (script PowerShell)
Source: "..\Tools\benchmark_comparaison.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName} Dashboard";          Filename: "{app}\Phase4\{#AppExe}"; IconFilename: "{app}\Phase4\{#AppExe}"
Name: "{group}\{#AppName} — Benchmark comparatif"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Tools\benchmark_comparaison.ps1"""; WorkingDir: "{app}\Tools"; Comment: "Compare la RAM disponible avec et sans RAM-AI"
Name: "{group}\Desinstaller {#AppName}";       Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}";            Filename: "{app}\Phase4\{#AppExe}"; IconFilename: "{app}\Phase4\{#AppExe}"; Tasks: desktopicon
Name: "{commondesktop}\{#AppName} Benchmark";  Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Tools\benchmark_comparaison.ps1"""; WorkingDir: "{app}\Tools"; Comment: "Compare la RAM disponible avec et sans RAM-AI"; Tasks: benchmarkdesktopicon

[Run]
Filename: "{app}\Phase3\{#ServiceExe}"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Enregistrement du service RAM-AI Phase 3..."
Filename: "{sys}\sc.exe"; Parameters: "start RamAI-Phase3"; Flags: runhidden waituntilterminated; StatusMsg: "Démarrage du service RAM-AI Phase 3..."
Filename: "{app}\Phase4\{#AppExe}"; Description: "Lancer RAM-AI Dashboard"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\Phase4\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[UninstallRun]
Filename: "{app}\Phase3\{#ServiceExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated

[Code]
// ─────────────────────────────────────────────────────────────────────────────
//  Téléchargement automatique de .NET 10 Runtime (Inno Setup 6.1+)
//
//  Flux :
//    InitializeSetup()   → vérifie si .NET 10 est présent (registre)
//    InitializeWizard()  → crée la page de progression du téléchargement
//    NextButtonClick()   → lance le dl quand l'utilisateur valide wpReady
//    CurStepChanged()    → installe le runtime silencieusement avant ssInstall
//
//  Si le téléchargement ou l'installation échoue, un message clair indique
//  l'URL de téléchargement manuel pour que l'utilisateur puisse se débloquer.
// ─────────────────────────────────────────────────────────────────────────────

var
  DotNetMissing: Boolean;
  DownloadPage:  TDownloadWizardPage;

// Vérifie si .NET 10 Runtime x64 est déjà installé (clé registre Windows)
function IsDotNet10Installed(): Boolean;
var
  RegBase: String;
  SubKeys: TArrayOfString;
  I: Integer;
begin
  Result := False;
  RegBase := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App';
  if not RegGetSubkeyNames(HKLM, RegBase, SubKeys) then
    Exit;
  for I := 0 to GetArrayLength(SubKeys) - 1 do
    if Copy(SubKeys[I], 1, 3) = '10.' then
    begin
      Result := True;
      Exit;
    end;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(
    'Téléchargement de .NET 10 Runtime',
    'Installation du prérequis .NET 10 Runtime x64 (serveurs Microsoft)...',
    nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ManualMsg: String;
begin
  Result := True;

  if (CurPageID <> wpReady) or not DotNetMissing then
    Exit;

  ManualMsg := '.NET 10 Runtime x64 est nécessaire pour faire fonctionner RAM-AI.' + #13#10 +
               'Lien de téléchargement manuel :' + #13#10 +
               '{#DotNetDlPage}';

  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetUrl}', '{#DotNetFile}', '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        MsgBox('Téléchargement annulé.' + #13#10 + #13#10 + ManualMsg,
               mbError, MB_OK)
      else
        MsgBox('Échec du téléchargement de .NET 10 Runtime :' + #13#10 +
               GetExceptionMessage() + #13#10 + #13#10 + ManualMsg,
               mbError, MB_OK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  TempExe:    String;
  ResultCode: Integer;
  ManualMsg:  String;
begin
  if (CurStep <> ssInstall) or not DotNetMissing then
    Exit;

  ManualMsg := 'Veuillez installer .NET 10 Runtime x64 manuellement depuis :' + #13#10 +
               '{#DotNetDlPage}';

  TempExe := ExpandConstant('{tmp}\{#DotNetFile}');
  if not FileExists(TempExe) then
  begin
    MsgBox('Fichier .NET 10 Runtime introuvable.' + #13#10 + ManualMsg, mbError, MB_OK);
    Exit;
  end;

  if not Exec(TempExe, '/quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Impossible de lancer l''installeur .NET 10 Runtime.' + #13#10 +
           ManualMsg, mbError, MB_OK);
    Exit;
  end;

  // 0 = succès, 3010 = succès avec redémarrage requis (Windows Update classique)
  if (ResultCode <> 0) and (ResultCode <> 3010) then
    MsgBox('L''installation de .NET 10 Runtime a retourné le code ' +
           IntToStr(ResultCode) + '.' + #13#10 + ManualMsg, mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
begin
  DotNetMissing := not IsDotNet10Installed();
  Result := True;
end;
