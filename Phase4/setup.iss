; ─────────────────────────────────────────────────────────────────────────────
;  RAM-AI — Inno Setup 6+
;  Usage : iscc setup.iss
;  Sortie : installer\RAM-AI-Setup-1.0.0.exe
;
;  Build self-contained : .NET 10 Runtime embarqué dans l'installeur.
;  Aucune dépendance externe — pas de téléchargement de runtime nécessaire.
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
  ; Phase4 est publie SANS RID (-r win-x64) donc pas de sous-dossier win-x64
  #define ProtectedPhase4 "..\Phase4\bin\Release\net10.0-windows\publish"
#endif


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

