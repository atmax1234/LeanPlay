[CmdletBinding()]
param(
    [string]$ServiceExecutable
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ServiceExecutable)) {
    $scriptDirectory = Split-Path -Parent $PSCommandPath
    $repositoryRoot = [IO.Path]::GetFullPath(
        (Join-Path $scriptDirectory ".."))
    $ServiceExecutable = Join-Path `
        $repositoryRoot `
        "artifacts\service\LeanPlay.Service.exe"
}

$ServiceExecutable = [IO.Path]::GetFullPath($ServiceExecutable)
if (-not (Test-Path -LiteralPath $ServiceExecutable -PathType Leaf)) {
    throw "LeanPlay service executable was not found at $ServiceExecutable."
}

$process = Start-Process `
    -FilePath $ServiceExecutable `
    -ArgumentList "--recover" `
    -Verb RunAs `
    -Wait `
    -PassThru

if ($process.ExitCode -ne 0) {
    throw "LeanPlay recovery failed with exit code $($process.ExitCode)."
}

Write-Output "LeanPlay recovery completed successfully."
