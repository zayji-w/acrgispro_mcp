param([switch]$PythonOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try{
 & (Join-Path $root '.venv\Scripts\python.exe') -m unittest discover -s tests -p 'test_*.py' -v
 if($LASTEXITCODE -ne 0){throw 'Python tests failed'}
 & (Join-Path $root 'tests\deployment_structure_checks.ps1')
 if($PythonOnly){return}
 & dotnet run --project (Join-Path $root 'tests\Native.CoreChecks\Native.CoreChecks.csproj') -c Release
 if($LASTEXITCODE -ne 0){throw 'Native core tests failed'}
 & dotnet run --project (Join-Path $root 'tests\StartupChecks\StartupChecks.csproj') -c Release
 if($LASTEXITCODE -ne 0){throw 'Startup guard tests failed'}
}finally{Pop-Location}
