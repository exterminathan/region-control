@echo off
rem Builds and runs the unit-style self-test (no windows, no input, temp folder only).
rem The app is a Windows app with no console. Piping to "more" makes this window wait and show its output.
rem The last line says "N passed, M failed". "more" hides the exit code, so read that line.
dotnet build "%~dp0RegionControl\RegionControl.csproj" -c Release -v q -nologo || exit /b 1
"%~dp0RegionControl\bin\Release\net8.0-windows\RegionControl.exe" --selftest | more
