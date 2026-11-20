param([switch]$Apply,[switch]$Rollback,[string]$BackupDirectory,[switch]$PythonOnly,
      [string]$RuntimeRoot='',[string]$ConfigPath='',[string]$ArcGISProExe='',[string]$PackagePath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'pro_installation.ps1')
$documents=[Environment]::GetFolderPath('MyDocuments', 'DoNotVerify')
if(-not $documents){$documents=Join-Path $env:USERPROFILE 'Documents'}
$addin=Join-Path $documents 'ArcGIS\AddIns\ArcGISPro\{9db0f962-d76c-47bf-9130-f59f68f9b4bd}\SafeBridge.Native.esriAddinX'
$nativeConfig=Join-Path $env:LOCALAPPDATA 'GIS-Agent\bridge.config.json'
$config=if($ConfigPath){[IO.Path]::GetFullPath($ConfigPath)}else{$nativeConfig}
$existing = if (Test-Path -LiteralPath $config -PathType Leaf) {
    Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
}
if ($existing -and -not $existing.runtime_root) { throw 'Existing bridge configuration is missing runtime_root.' }
if (-not $RuntimeRoot) {
    $RuntimeRoot = if ($existing) { $existing.runtime_root } else { Join-Path (Split-Path -Parent $nativeConfig) 'runtime' }
}
$runtime=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RuntimeRoot))
if ($existing -and $runtime -ine [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($existing.runtime_root))) {
    throw 'RuntimeRoot differs from the existing configuration. Use its configured directory.'
}
if ($Apply -and -not $PythonOnly -and -not $Rollback -and $config -ine $nativeConfig) {
    throw 'ArcGIS Pro reads the default per-user configuration. Use -ConfigPath with -PythonOnly for isolated deployments.'
}
$runtimePrefix=$runtime.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
function Assert-Target([string]$path){
 $full=[IO.Path]::GetFullPath($path)
 if(-not($full.StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase) -or $full -eq $addin -or $full -eq $config)){throw "Deployment target outside approved roots: $full"}
}
function Restore-Record($record,[string]$directory){
 foreach($item in $record.items){
  Assert-Target $item.target
  if($item.backup -notmatch '^item-[0-9]+\.bak$'){throw 'Invalid backup filename'}
  if($item.existed -and -not(Test-Path -LiteralPath (Join-Path $directory $item.backup) -PathType Leaf)){throw 'Incomplete backup; restore skipped'}
 }
 foreach($item in $record.items){
  if($item.existed){Copy-Item -LiteralPath (Join-Path $directory $item.backup) -Destination $item.target -Force}
  elseif(Test-Path -LiteralPath $item.target -PathType Leaf){Remove-Item -LiteralPath $item.target -Force}
 }
}
if($Rollback){
 if(-not $BackupDirectory -or -not(Test-Path -LiteralPath (Join-Path $BackupDirectory 'deployment.json'))){throw 'A recorded deployment backup is required'}
 $record=Get-Content -LiteralPath (Join-Path $BackupDirectory 'deployment.json') -Raw|ConvertFrom-Json
 if(-not $Apply){@{dry_run=$true;rollback=$true;backup=$BackupDirectory}|ConvertTo-Json;return}
 if(-not $record.python_only -and (Get-Process ArcGISPro -ErrorAction SilentlyContinue)){throw 'Close Pro before restoring a native package'}
 Restore-Record $record $BackupDirectory
 Write-Output 'Deployment restored';return
}
$manifest=Get-Content -LiteralPath (Join-Path $root 'runtime-manifest.json') -Raw|ConvertFrom-Json
if($manifest.schema_version -ne 1 -or $manifest.protocol_version -ne 2 -or -not $manifest.files.Count){throw 'Invalid runtime manifest'}
$seen=@{};$targets=@()
foreach($file in $manifest.files){
 $relative=$file.Replace('\','/')
 if([IO.Path]::IsPathRooted($file) -or $file -match '(^|[\\/])\.\.([\\/]|$)' -or $seen.ContainsKey($relative)){throw 'Invalid or duplicate runtime file'}
 $seen[$relative]=$true
 $source=[IO.Path]::GetFullPath((Join-Path $root $file));$target=[IO.Path]::GetFullPath((Join-Path $runtime $file));Assert-Target $target
 if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing deployment artifact: $file"}
 if(Test-Path -LiteralPath $target -PathType Container){throw 'Deployment file target is a directory'}
 $targets+=@{source=$source;target=$target}
}
if(-not $PythonOnly){
 $version=([xml](Get-Content -LiteralPath (Join-Path $root 'Native\SafeBridge.Native.csproj') -Raw)).Project.PropertyGroup.Version
 $package=if($PackagePath){[IO.Path]::GetFullPath($PackagePath)}else{Join-Path $root ('artifacts\releases\'+$version+'\acrgispro_mcp.esriAddinX')}
 if(-not(Test-Path -LiteralPath $package -PathType Leaf)){throw 'Provide -PackagePath or build the native release package first.'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $archive=[IO.Compression.ZipFile]::OpenRead($package)
 try {
    $entry=$archive.GetEntry('Config.daml')
    if(-not $entry -or -not $archive.GetEntry('Install/SafeBridge.Native.dll')){throw 'SafeBridge native add-in contents are incomplete.'}
    $stream=[IO.StreamReader]::new($entry.Open())
    try { $info=([xml]$stream.ReadToEnd()).ArcGIS.AddInInfo } finally { $stream.Dispose() }
    if($info.version -ne $version -or $info.id -ine '{9db0f962-d76c-47bf-9130-f59f68f9b4bd}'){
        throw "The SafeBridge add-in must match version $version."
    }
 } finally { $archive.Dispose() }
 $targets+=@{source=$package;target=$addin}
}
if(-not $existing -or $ArcGISProExe){
 $settings = [ordered]@{
    runtime_root=$runtime; state_root=(Join-Path $runtime 'state')
    output_root=(Join-Path $runtime 'outputs'); gp_catalog_path=(Join-Path $runtime 'gp_catalog.json')
 }
 if($existing){$settings=[ordered]@{};foreach($entry in $existing.PSObject.Properties){$settings[$entry.Name]=$entry.Value}}
 $exe=Get-GisAgentProExecutable $ArcGISProExe -Required:(-not $PythonOnly)
 if($exe){$settings['arcgis_pro_exe']=$exe}
 $targets+=@{text=($settings|ConvertTo-Json -Depth 8);target=$config}
}
foreach($target in $targets){
 if($target.source -and -not(Test-Path -LiteralPath $target.source -PathType Leaf)){throw 'Missing deployment source'}
}
if(-not $Apply){@{dry_run=$true;python_only=[bool]$PythonOnly;runtime=$runtime;config=$config;file_count=$targets.Count;structure_revision=$manifest.structure_revision}|ConvertTo-Json;return}
if(-not $PythonOnly -and (Get-Process ArcGISPro -ErrorAction SilentlyContinue)){throw 'Close Pro before replacing the native package; never force close'}
$backup=Join-Path $root ('artifacts\backups\deploy-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup -Force|Out-Null
$items=@()
for($i=0;$i -lt $targets.Count;$i++){
 $t=$targets[$i];$exists=Test-Path -LiteralPath $t.target -PathType Leaf;$name="item-$i.bak"
 if($exists){Copy-Item -LiteralPath $t.target -Destination (Join-Path $backup $name)}
 $items+=@{target=$t.target;existed=$exists;backup=$name}
}
$record=@{python_only=[bool]$PythonOnly;runtime=$runtime;items=$items;created_at=(Get-Date -Format o)}
$record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $backup 'deployment.json') -Encoding utf8
try{
 # Expose the entry point only after its imports are available, and publish the configuration last.
 foreach($t in ($targets|Sort-Object {if($_.target -eq $config){2}elseif($_.target -eq (Join-Path $runtime 'server.py')){1}else{0}})){
  New-Item -ItemType Directory -Path (Split-Path -Parent $t.target) -Force|Out-Null
  if($t.source){
    Copy-Item -LiteralPath $t.source -Destination $t.target -Force
    if((Get-FileHash -LiteralPath $t.source).Hash -ne (Get-FileHash -LiteralPath $t.target).Hash){throw 'Deployment hash mismatch'}
  }else{
    [IO.File]::WriteAllText($t.target,$t.text,[Text.UTF8Encoding]::new($false))
    if([IO.File]::ReadAllText($t.target) -cne $t.text){throw 'Configuration write mismatch'}
  }
 }
}catch{
 $failure=$_
 try{Restore-Record $record $backup}catch{throw "Deployment failed and rollback needs attention. Backup: $backup; restore error: $_"}
 throw $failure
}
Write-Output $backup
