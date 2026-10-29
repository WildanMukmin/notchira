@echo off
rem Builds Notchira.exe with the C# compiler bundled in Windows (.NET Framework 4.8, C# 5)
rem Usage: build.cmd [output.exe]
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WMD=%WINDIR%\System32\WinMetadata
set ROOT=%~dp0
set OUT=%~1
if "%OUT%"=="" set OUT=%ROOT%Notchira.exe

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /warn:4 ^
  /out:"%OUT%" ^
  /win32manifest:"%ROOT%src\app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /r:"%WMD%\Windows.Foundation.winmd" /r:"%WMD%\Windows.Media.winmd" /r:"%WMD%\Windows.Storage.winmd" /r:"%WMD%\Windows.Networking.winmd" /r:"%WMD%\Windows.UI.winmd" /r:"%WMD%\Windows.ApplicationModel.winmd" /r:"%WMD%\Windows.Devices.winmd" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:"%FW%\System.Threading.dll" /r:"%FW%\System.IO.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  "%ROOT%src\*.cs"
if errorlevel 1 (
  echo Build gagal.
  exit /b 1
)
echo Build OK: %OUT%
