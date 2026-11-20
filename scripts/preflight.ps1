$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$python=Join-Path $root '.venv\Scripts\python.exe'
& $python -c "import sys,importlib.metadata as m; print(sys.executable); print(m.version('mcp'))"
if($LASTEXITCODE -ne 0){throw 'Python preflight failed'}
& dotnet --version
if($LASTEXITCODE -ne 0){throw '.NET preflight failed'}
Get-Process ArcGISPro -ErrorAction SilentlyContinue | Select-Object Id,MainWindowTitle
