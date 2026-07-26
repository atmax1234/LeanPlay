[CmdletBinding()]
param(
    [ValidateRange(10, 1800)]
    [int]$DurationSeconds = 60,

    [string]$WorkloadLabel = "",

    [string]$OutputDirectory = "",

    [switch]$NoElevation,

    [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory ".."))
$publishDirectory = Join-Path $repositoryRoot "artifacts\analyzer"
$projectPath = Join-Path `
    $repositoryRoot `
    "src\LeanPlay.Analyzer\LeanPlay.Analyzer.csproj"
$analyzerExecutable = Join-Path $publishDirectory "LeanPlay.Analyzer.exe"

if (-not (Test-Path -LiteralPath $analyzerExecutable -PathType Leaf)) {
    Write-Output "Publishing LeanPlay Analyzer..."
    dotnet publish `
        $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer publish failed with exit code $LASTEXITCODE."
    }
}

$arguments = @("--duration", $DurationSeconds.ToString())
if (-not [string]::IsNullOrWhiteSpace($WorkloadLabel)) {
    $arguments += @("--label", $WorkloadLabel)
}
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $arguments += @("--output", [IO.Path]::GetFullPath($OutputDirectory))
}
if ($NoOpen) {
    $arguments += "--no-open"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $NoElevation -and -not $isAdministrator) {
    Write-Output "Requesting elevation for kernel ETW driver attribution..."
    $argumentText = ($arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        } else {
            $_
        }
    }) -join " "
    $process = Start-Process `
        -FilePath $analyzerExecutable `
        -ArgumentList $argumentText `
        -Verb RunAs `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Analyzer exited with code $($process.ExitCode)."
    }
    return
}

if ($NoElevation) {
    $arguments += "--no-etw"
}

& $analyzerExecutable @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Analyzer exited with code $LASTEXITCODE."
}
