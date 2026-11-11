@echo off
rem Builds and starts Snap Layouts in the tray (no console window). Quit it from the tray menu first if it is running.
dotnet build "%~dp0RegionControl\RegionControl.csproj" -c Release -v q -nologo || exit /b 1
start "" "%~dp0RegionControl\bin\Release\net8.0-windows\RegionControl.exe"
echo Snap Layouts started in the tray. Quit from the tray menu. Log: %%APPDATA%%\RegionControl\logs
