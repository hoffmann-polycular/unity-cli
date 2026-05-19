param(
    [switch]$WithSkill
)

$ErrorActionPreference = "Stop"

$repo = "hoffmann-polycular/unity-cli"
$installDir = "$env:LOCALAPPDATA\unity-cli"
$exe = "$installDir\unity-cli.exe"

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$url = "https://github.com/$repo/releases/latest/download/unity-cli-windows-amd64.exe"
Write-Host "Downloading unity-cli for windows/amd64..."
Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$installDir;$userPath", "User")
    $env:Path = "$installDir;$env:Path"
    Write-Host "Added $installDir to PATH (restart shell to apply)"
}

Write-Host "Installed unity-cli to $exe"
& $exe version

if ($WithSkill) {
    $skillDir = "$env:USERPROFILE\.claude\skills\unity-cli"
    New-Item -ItemType Directory -Force -Path $skillDir | Out-Null
    $skillUrl = "https://raw.githubusercontent.com/$repo/main/.claude/skills/unity-cli/SKILL.md"
    Invoke-WebRequest -Uri $skillUrl -OutFile "$skillDir\SKILL.md" -UseBasicParsing
    Write-Host "Installed Claude Code skill to $skillDir\SKILL.md"
}
