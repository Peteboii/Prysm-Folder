@echo off
rem Builds "Prysm Folder.exe" with the C# compiler that's built into Windows (.NET Framework 4.8).
rem Nothing to install. The see-through look uses Windows' own composition engine, whose
rem metadata ships with Windows (System32\WinMetadata).
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set MD=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /nowarn:1690,162,649 /out:"Prysm Folder.exe" ^
  /win32icon:assets\PrysmFolder.ico ^
  /r:"%MD%\Windows.Foundation.winmd" /r:"%MD%\Windows.UI.winmd" ^
  /r:System.Runtime.dll /r:System.Runtime.WindowsRuntime.dll /r:System.Numerics.dll /r:System.Numerics.Vectors.dll ^
  /r:System.Runtime.InteropServices.WindowsRuntime.dll /r:System.Management.dll ^
  PrysmFolder.cs
if errorlevel 1 (pause & exit /b 1)
echo Built "Prysm Folder.exe"
if not "%1"=="nopause" pause
