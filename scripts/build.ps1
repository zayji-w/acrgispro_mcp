param([string]$ProInstallDir='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$finder = Join-Path $PSScriptRoot 'pro_installation.ps1'
. $finder
if (-not $ProInstallDir) {
    $exe = Get-GisAgentProExecutable -Required
    $ProInstallDir = Split-Path -Parent (Split-Path -Parent $exe)
}
& dotnet build (Join-Path $root 'Native\SafeBridge.Native.csproj') -c Release "-p:ProInstallDir=$ProInstallDir" --nologo
if($LASTEXITCODE -ne 0){throw 'Native build failed'}
$version=([xml](Get-Content -LiteralPath (Join-Path $root 'Native\SafeBridge.Native.csproj') -Raw)).Project.PropertyGroup.Version
$release=Join-Path $root ('artifacts\releases\'+$version)
New-Item -ItemType Directory -Path $release -Force | Out-Null
$target=Join-Path $release 'acrgispro_mcp.esriAddinX'
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Open($target,[IO.FileMode]::Create)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
  $entries=@(@{source=(Join-Path $root 'Native\Config.daml');name='Config.daml'})
  $output=Join-Path $root 'Native\bin\Release\net10.0-windows'
  foreach($name in 'SafeBridge.Native.dll','SafeBridge.Native.deps.json'){
    $entries+=@{source=(Join-Path $output $name);name=('Install/'+$name)}
  }
  foreach($e in $entries){$entry=$zip.CreateEntry($e.name);$dest=$entry.Open();try{$bytes=[IO.File]::ReadAllBytes($e.source);$dest.Write($bytes,0,$bytes.Length)}finally{$dest.Dispose()}}
} finally {$zip.Dispose();$stream.Dispose()}
@{version=$version;package=[IO.Path]::GetFileName($target);sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'manifest.json') -Encoding utf8
Write-Output $target
