@echo off
rem Build the single-file "LEDBRAIN Bridge.exe" (no Node.js) with the C# compiler from .NET Framework 4.8 (ships with Windows)
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (echo csc.exe from .NET Framework 4.8 not found & exit /b 1)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /unsafe /codepage:65001 /win32icon:icon.ico "/out:..\LEDBRAIN Bridge.exe" /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll App.cs Core.cs
