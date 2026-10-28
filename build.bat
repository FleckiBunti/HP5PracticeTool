@echo off
rem Builds HP5PracticeTool.exe with the C# compiler that ships with Windows (.NET Framework 4).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 /out:HP5PracticeTool.exe src\*.cs
