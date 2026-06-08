RAM-AI — Installation de ConfuserEx
=====================================

1. Télécharger la dernière release :
   https://github.com/mkbel/ConfuserEx/releases

2. Extraire l'archive ZIP dans :
   C:\projettoto\RAM-AI\Tools\ConfuserEx\

   Structure attendue :
   Tools\ConfuserEx\
     Confuser.CLI.exe        ← exécutable principal (requis)
     Confuser.Core.dll
     Confuser.Protections.dll
     ...

3. Lancer le build protégé :
   PowerShell : .\build_protected.ps1

Note compatibilité .NET 10 :
   ConfuserEx (mkbel fork) a été conçu pour .NET Framework et .NET Core ≤ 3.1.
   Avec .NET 8/9/10, il peut produire des assemblies invalides ou refuser l'obfuscation.
   
   Alternatives compatibles .NET 8+ :
   - Obfuscar    : https://github.com/obfuscar/obfuscar  (open source)
   - Babel       : https://www.babelfor.net/
   - Eazfuscator : https://www.gapotchenko.com/eazfuscator.net

   Le script build_protected.ps1 bascule automatiquement en mode
   "copie sans obfuscation" si ConfuserEx est absent ou échoue.
