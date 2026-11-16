# Builds Music Bar, copies it to %LOCALAPPDATA%\Programs\MusicBar, and starts it.
# Run again after any code change to update the installed copy.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MusicBar'

Get-Process MusicBar -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$PSScriptRoot\MusicBar\MusicBar.csproj" -c Release -r win-x64 --self-contained false -o $dest
if ($LASTEXITCODE -ne 0) { throw 'Build failed, nothing was installed.' }

Start-Process (Join-Path $dest 'MusicBar.exe')
Write-Host "Music Bar installed to $dest and started."
