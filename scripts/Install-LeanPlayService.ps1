[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    throw "Run this installer from an elevated PowerShell session."
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $scriptDirectory ".."))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot "artifacts\service"
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)

$projectPath = Join-Path `
    $repositoryRoot `
    "src\LeanPlay.Service\LeanPlay.Service.csproj"

dotnet publish `
    $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $PublishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$serviceExecutable = Join-Path $PublishDirectory "LeanPlay.Service.exe"
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "Published service executable was not found at $serviceExecutable."
}

$existing = Get-Service -Name "LeanPlayRuntime" -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    throw "LeanPlayRuntime is already installed. Remove or update it explicitly."
}

$quotedBinaryPath = '"' + $serviceExecutable + '"'
& sc.exe create `
    LeanPlayRuntime `
    "binPath= $quotedBinaryPath" `
    "start= delayed-auto" `
    "DisplayName= LeanPlay Runtime"
if ($LASTEXITCODE -ne 0) {
    throw "Service creation failed with exit code $LASTEXITCODE."
}

# Recovery is essential: a killed service must restart and consume its journal.
& sc.exe failure `
    LeanPlayRuntime `
    "reset= 86400" `
    "actions= restart/5000/restart/15000/restart/60000"
if ($LASTEXITCODE -ne 0) {
    throw "Could not configure service failure recovery."
}

& sc.exe failureflag LeanPlayRuntime 1
if ($LASTEXITCODE -ne 0) {
    throw "Could not enable failure actions for non-crash failures."
}

& sc.exe description `
    LeanPlayRuntime `
    "Crash-safe, reversible per-game Windows runtime coordinator."

Start-Service -Name "LeanPlayRuntime"
Write-Output "LeanPlayRuntime was installed and started from $PublishDirectory."
