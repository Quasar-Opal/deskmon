@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 /target:winexe /out:deskmon.exe /win32icon:brain.ico /resource:brain.ico,brain.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll deskmon.cs
