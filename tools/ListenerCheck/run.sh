#!/usr/bin/env sh
# Runs the stand-in listener (the plugin's own HuntListener code) outside the game.
# Usage: ./run.sh [--port 47821] [--token <token>]
dir="$(cd "$(dirname "$0")" && pwd)"
exec dotnet run --project "$dir/ListenerCheck.csproj" -- "$@"
