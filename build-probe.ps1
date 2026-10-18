$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet publish src/Win2HomePod.Probe/Win2HomePod.Probe.csproj -c Release -r win-x64 --self-contained false -o probe-app
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
