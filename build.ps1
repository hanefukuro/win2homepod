$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet publish src/Win2HomePod/Win2HomePod.csproj -c Release -r win-x64 --self-contained false -o app
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
