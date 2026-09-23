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

# The skill is compiled into the binary we just installed, so let it write
# the copy: fetching it from main would pair a release binary with whatever
# the default branch happens to say, which is the drift this avoids.
if ($WithSkill) {
    & $exe skill install
}
