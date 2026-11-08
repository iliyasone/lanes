#!/bin/bash
# Build and install Lanes on a PC reached as `ssh wsl` then `ssh windows` (one specific setup;
# adapt both hops). Runs `Lanes.exe --install` there, which starts it in the signed-in session.
set -e
cd "$(dirname "$0")/.."
dotnet=${DOTNET:-$HOME/.dotnet/dotnet}
$dotnet publish src/Lanes -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out >/dev/null
scp -q out/Lanes.exe wsl:/tmp/Lanes.exe
cat > /tmp/lanes-install.ps1 <<'PS'
$ProgressPreference = 'SilentlyContinue'
& "$env:USERPROFILE\Lanes.exe" --install | Out-Null
Start-Sleep 4
Remove-Item "$env:USERPROFILE\Lanes.exe" -ErrorAction SilentlyContinue
Get-Process Lanes -ErrorAction SilentlyContinue | ForEach-Object { "running pid $($_.Id) session $($_.SessionId)" }
Get-Content "$env:LOCALAPPDATA\Lanes\log.txt" -Tail 12 -ErrorAction SilentlyContinue
PS
B64=$(iconv -f UTF-8 -t UTF-16LE /tmp/lanes-install.ps1 | base64 -w0)
ssh wsl "scp -q /tmp/Lanes.exe windows:Lanes.exe && rm /tmp/Lanes.exe && ssh windows 'powershell -NoProfile -EncodedCommand $B64'" 2>&1 | grep -v CLIXML | sed 's/<[^>]*>//g'
