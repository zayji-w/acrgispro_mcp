$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('artifacts\deploy-check-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $fixture 'source';$runtime=Join-Path $fixture 'runtime';$config=Join-Path $fixture 'bridge.config.json'
New-Item -ItemType Directory -Path $source,$runtime -Force|Out-Null
$manifest=Get-Content -LiteralPath (Join-Path $root 'runtime-manifest.json') -Raw|ConvertFrom-Json
foreach($file in @($manifest.files)+@('bridge.config.example.json','scripts/deploy.ps1','scripts/pro_installation.ps1')){
 $target=Join-Path $source $file;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
 Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
}
[IO.File]::WriteAllText((Join-Path $runtime 'server.py'),'previous-entry')
[IO.File]::WriteAllText((Join-Path $runtime 'keep.dat'),'untouched')
[IO.File]::WriteAllText($config,(@{runtime_root=$runtime;preserve='exact config bytes'}|ConvertTo-Json))
$configHash=(Get-FileHash -LiteralPath $config).Hash
$script=Join-Path $source 'scripts/deploy.ps1'
$backup=& $script -PythonOnly -Apply -RuntimeRoot $runtime -ConfigPath $config
$checks=@()
foreach($file in $manifest.files){if((Get-FileHash -LiteralPath (Join-Path $source $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $runtime $file)).Hash){throw 'Runtime hash mismatch'}}
$checks+='all runtime files deployed'
if((Get-FileHash -LiteralPath $config).Hash -ne $configHash){throw 'Existing config changed'};$checks+='existing config preserved'
& $script -Rollback -Apply -BackupDirectory $backup -RuntimeRoot $runtime -ConfigPath $config|Out-Null
if([IO.File]::ReadAllText((Join-Path $runtime 'server.py')) -ne 'previous-entry'){throw 'Rollback did not restore entry'}
if(Test-Path -LiteralPath (Join-Path $runtime 'gis_agent/app.py') -PathType Leaf){throw 'Rollback retained a newly deployed module'}
if([IO.File]::ReadAllText((Join-Path $runtime 'keep.dat')) -ne 'untouched'){throw 'Unrelated runtime file changed'}
$checks+='rollback restored old entry and removed own new files';$checks+='unrelated file preserved'
# Inject one copy failure after backup. The real restore implementation must undo prior writes.
$script:copyFailureInjected=$false
function Copy-Item {
 param([string]$LiteralPath,[string]$Destination,[switch]$Force)
 if(-not $script:copyFailureInjected -and $Destination -eq (Join-Path $runtime 'gis_agent/client.py')){$script:copyFailureInjected=$true;throw 'Injected copy failure'}
 Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
try{
 $rejected=$false
 try{& $script -PythonOnly -Apply -RuntimeRoot $runtime -ConfigPath $config|Out-Null}catch{if($_.Exception.Message -ne 'Injected copy failure'){throw};$rejected=$true}
 if(-not $rejected -or [IO.File]::ReadAllText((Join-Path $runtime 'server.py')) -ne 'previous-entry'){throw 'Automatic restore failed'}
 foreach($file in $manifest.files){if($file -ne 'server.py' -and (Test-Path -LiteralPath (Join-Path $runtime $file) -PathType Leaf)){throw 'Partial deployment remained after copy failure'}}
 $checks+='copy failure automatically restored prior runtime'
}finally{Remove-Item -LiteralPath Function:\Copy-Item}
$blocked=Join-Path $runtime 'gis_agent/client.py'
New-Item -ItemType Directory -Path $blocked -Force|Out-Null
$rejected=$false
try{& $script -PythonOnly -Apply -RuntimeRoot $runtime -ConfigPath $config|Out-Null}catch{if($_.Exception.Message -ne 'Deployment file target is a directory'){throw};$rejected=$true}
if(-not $rejected){throw 'Directory at file target accepted'}
Remove-Item -LiteralPath $blocked
$checks+='directory collision rejected before writes'
$missing=Join-Path $source 'gis_agent/tools/maps.py'
Move-Item -LiteralPath $missing -Destination ($missing+'.off')
$rejected=$false
try{& $script -PythonOnly -Apply -RuntimeRoot $runtime -ConfigPath $config|Out-Null}catch{if($_.Exception.Message -notlike 'Missing deployment artifact*'){throw};$rejected=$true}
if(-not $rejected -or [IO.File]::ReadAllText((Join-Path $runtime 'server.py')) -ne 'previous-entry'){throw 'Missing-module preflight failed'}
$checks+='missing module rejected before writes'
Move-Item -LiteralPath ($missing+'.off') -Destination $missing
$fresh=Join-Path $fixture 'alternate runtime 新安装'
$freshConfig=Join-Path $fixture 'new config.json'
$firstBackup=& $script -PythonOnly -Apply -RuntimeRoot $fresh -ConfigPath $freshConfig
$created=Get-Content -LiteralPath $freshConfig -Raw|ConvertFrom-Json
if($created.runtime_root -ne $fresh -or $created.state_root -ne (Join-Path $fresh 'state') -or
   $created.output_root -ne (Join-Path $fresh 'outputs') -or
   $created.gp_catalog_path -ne (Join-Path $fresh 'gp_catalog.json') -or
   -not (Test-Path -LiteralPath $created.gp_catalog_path -PathType Leaf)){
    throw 'Fresh configuration points outside its runtime'
}
$checks+='fresh install configuration follows chosen runtime'
& $script -Rollback -Apply -RuntimeRoot $fresh -ConfigPath $freshConfig -BackupDirectory $firstBackup|Out-Null
if(Test-Path -LiteralPath $freshConfig){throw 'First-install rollback retained its generated configuration'}
$checks+='fresh install configuration is removed on rollback'
$manifest.files=@('../escape.py')
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $source 'runtime-manifest.json') -Encoding utf8
$rejected=$false
try{& $script -PythonOnly -Apply -RuntimeRoot $runtime -ConfigPath $config|Out-Null}catch{if($_.Exception.Message -ne 'Invalid or duplicate runtime file'){throw};$rejected=$true}
if(-not $rejected){throw 'Path escape accepted'};$checks+='path escape rejected'
$result=@{passed=$true;checks=$checks;fixture=$fixture}
$result|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'artifacts/deployment-structure-checks.json') -Encoding utf8
$result|ConvertTo-Json -Depth 4
