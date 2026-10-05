[CmdletBinding()]
param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$environmentRoot = Join-Path $repositoryRoot "artifacts/test-tools/sftp/venv"
$fixturePython = Join-Path $environmentRoot "Scripts/python.exe"
$requirements = Join-Path $PSScriptRoot "sftp-fixture/requirements.txt"

if (-not $IsWindows) { throw "The SFTP process fixtures require Windows." }
if (-not (Test-Path -LiteralPath $fixturePython -PathType Leaf)) {
    New-Item -ItemType Directory -Path (Split-Path $environmentRoot -Parent) -Force | Out-Null
    & $Python -m venv $environmentRoot
    if ($LASTEXITCODE -ne 0) { throw "Could not create the isolated SFTP fixture environment." }
}

& $fixturePython -m pip install --disable-pip-version-check --only-binary=:all: -r $requirements
if ($LASTEXITCODE -ne 0) { throw "SFTP fixture dependency installation failed. Check Python compatibility and network access." }
& $fixturePython -m pip check
if ($LASTEXITCODE -ne 0) { throw "SFTP fixture dependencies are inconsistent." }
& $fixturePython -c "import paramiko, cryptography; assert paramiko.__version__ == '5.0.0'; assert cryptography.__version__ == '46.0.3'"
if ($LASTEXITCODE -ne 0) { throw "SFTP fixture dependency preflight failed." }

Write-Output "SFTP fixture environment is ready. Run the Release tests with eng/verify-build.ps1."
