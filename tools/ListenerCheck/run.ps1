# Runs the stand-in listener (the plugin's own HuntListener code) outside the game.
# Usage: .\run.ps1 [-Port 47821] [-Token <token>]
param(
    [int]$Port = 47821,
    [string]$Token = ""
)
$proj = Join-Path $PSScriptRoot "ListenerCheck.csproj"
$extra = @("--port", $Port)
if ($Token) { $extra += @("--token", $Token) }
dotnet run --project $proj -- @extra
