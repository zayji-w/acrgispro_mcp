param(
    [string]$PythonExe = '',
    [string]$RuntimeRoot = '',
    [string]$ArcGISProExe = '',
    [string]$PackagePath = '',
    [switch]$RegisterCodex,
    [switch]$PythonOnly,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deploy = Join-Path $PSScriptRoot 'deploy.ps1'
if (-not $PythonOnly -and -not $PackagePath) {
    $bundled = Join-Path $root 'acrgispro_mcp.esriAddinX'
    if (Test-Path -LiteralPath $bundled -PathType Leaf) { $PackagePath = $bundled }
}
$options = @{RuntimeRoot=$RuntimeRoot; ArcGISProExe=$ArcGISProExe; PackagePath=$PackagePath; PythonOnly=$PythonOnly}
$plan = & $deploy @options | ConvertFrom-Json
$venv = Join-Path $plan.runtime '.venv'
$runtimePython = Join-Path $venv 'Scripts/python.exe'

if ($DryRun) {
    $plan | Add-Member -NotePropertyName python -NotePropertyValue $runtimePython
    $plan | Add-Member -NotePropertyName register_codex -NotePropertyValue ([bool]$RegisterCodex)
    $plan | ConvertTo-Json
    return
}
if (-not $PythonOnly -and (Get-Process ArcGISPro -ErrorAction SilentlyContinue)) {
    throw 'Close ArcGIS Pro normally before installing the native add-in.'
}

function Test-Python312([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    & $path -I -c 'import sys; sys.exit(0 if sys.version_info[:2] == (3, 12) and sys.maxsize > 2**32 else 1)' | Out-Null
    return $LASTEXITCODE -eq 0
}

if (-not (Test-Path -LiteralPath $venv)) {
    if ($PythonExe) {
        $base = [IO.Path]::GetFullPath($PythonExe)
    } else {
        $candidates = foreach ($hive in 'HKCU:', 'HKLM:') {
            $key = Get-Item -LiteralPath "$hive\SOFTWARE\Python\PythonCore\3.12\InstallPath" -ErrorAction SilentlyContinue
            if ($key -and $key.GetValue('ExecutablePath')) { $key.GetValue('ExecutablePath') }
            elseif ($key -and $key.GetValue('')) { Join-Path $key.GetValue('') 'python.exe' }
        }
        $command = Get-Command python.exe -ErrorAction SilentlyContinue
        if ($command -and $command.Source -notlike '*\Microsoft\WindowsApps\*') { $candidates += $command.Source }
        $base = $candidates | Where-Object { Test-Python312 $_ } | Select-Object -First 1
    }
    if (-not (Test-Python312 $base)) { throw 'Install 64-bit Python 3.12 or specify it with -PythonExe.' }
    & $base -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Creating the isolated Python environment failed.' }
}
if (-not (Test-Python312 $runtimePython)) {
    throw 'The runtime .venv is incomplete or uses a different Python version. Choose a fresh runtime directory for a new installation.'
}
& $runtimePython -m pip --disable-pip-version-check --require-virtualenv install --no-input -r (Join-Path $root 'requirements.lock') -q
if ($LASTEXITCODE -ne 0) { throw 'Installing the pinned Python dependencies failed.' }
$backup = & $deploy @options -Apply
if ($RegisterCodex) {
    try {
        & $runtimePython -B (Join-Path $PSScriptRoot 'register_client.py') --runtime-root $plan.runtime --python-exe $runtimePython
        if ($LASTEXITCODE -ne 0) { throw 'Codex registration failed.' }
    } catch {
        $failure = $_
        & $deploy @options -Rollback -Apply -BackupDirectory $backup | Out-Null
        throw $failure
    }
}
@{installed=$true;runtime=$plan.runtime;config=$plan.config;python=$runtimePython;deployment_backup=$backup} | ConvertTo-Json
