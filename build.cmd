@echo off
setlocal
rem Bygger de tre ekses: FjernAdmin.exe (admin UI), FjernUser.exe (vennens klient) og FjernRelay.exe.
rem Kilderne skal vaere gemt som UTF-8 med BOM, ellers laeser csc ae/oe/aa forkert.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Kunne ikke finde csc.exe - .NET Framework 4 kraves.
  exit /b 1
)

set "SRC=%~dp0src"
set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"
set "SOURCES=%SRC%\Protocol.cs %SRC%\Settings.cs %SRC%\Capture.cs %SRC%\Injection.cs %SRC%\Peer.cs %SRC%\Theme.cs %SRC%\ShareForm.cs %SRC%\ViewerForm.cs %SRC%\Voice.cs %SRC%\WebCam.cs %SRC%\MainForm.cs %SRC%\Program.cs"

echo [1/3] FjernAdmin.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /define:FJERN_ADMIN ^
  /out:"%~dp0FjernAdmin.exe" ^
  /win32manifest:"%~dp0Fjern.manifest" ^
  %REFS% ^
  /res:"%~dp0tekst\hjaelp.txt","Fjern.Hjaelp" ^
  %SOURCES%
if errorlevel 1 goto fail

echo [2/3] FjernUser.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /out:"%~dp0FjernUser.exe" ^
  /win32manifest:"%~dp0Fjern.manifest" ^
  %REFS% ^
  /res:"%~dp0tekst\hjaelp.txt","Fjern.Hjaelp" ^
  %SOURCES%
if errorlevel 1 goto fail

echo [3/3] FjernRelay.exe
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /warn:4 ^
  /out:"%~dp0FjernRelay.exe" ^
  /r:System.dll /r:System.Core.dll ^
  "%SRC%\RelayServer.cs"
if errorlevel 1 goto fail

echo.
echo Bygget: FjernAdmin.exe, FjernUser.exe og FjernRelay.exe
exit /b 0

:fail
echo.
echo Build fejlede.
exit /b 1
