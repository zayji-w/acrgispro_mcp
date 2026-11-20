[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$AddinPath,
    [string]$ReleaseZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory)
if ((Test-Path -LiteralPath $destination) -and
    (!(Test-Path -LiteralPath $destination -PathType Container) -or
        @(Get-ChildItem -LiteralPath $destination -Force).Count)) {
    throw 'The source export needs a new or empty directory.'
}
if ([bool]$AddinPath -ne [bool]$ReleaseZip) {
    throw 'Supply both -AddinPath and -ReleaseZip to create an install ZIP.'
}

# Use relative paths so the public checkout and ZIP have the same source tree.
$manifest = Get-Content -LiteralPath (Join-Path $root 'runtime-manifest.json') -Raw | ConvertFrom-Json
$files = @($manifest.files) + @(
    '.gitignore', 'LICENSE', 'bridge.config.example.json', 'requirements.lock',
    'docs/INSTALL.md',
    'scripts/build.ps1', 'scripts/install.ps1', 'scripts/deploy.ps1',
    'scripts/package-release.ps1', 'scripts/preflight.ps1', 'scripts/pro_installation.ps1',
    'scripts/register_client.py', 'scripts/verify.ps1',
    'tests/deployment_structure_checks.ps1', 'tests/fixtures/contracts.json',
    'tests/Native.CoreChecks/Native.CoreChecks.csproj', 'tests/Native.CoreChecks/Program.cs',
    'tests/StartupChecks/StartupChecks.csproj', 'tests/StartupChecks/Program.cs'
)
$files += Get-ChildItem -LiteralPath (Join-Path $root 'Native') -File |
    Where-Object { $_.Extension -eq '.cs' -or $_.Name -in @('SafeBridge.Native.csproj', 'Config.daml') } |
    ForEach-Object { 'Native/' + $_.Name }
$files += Get-ChildItem -LiteralPath (Join-Path $root 'tests') -File -Filter 'test_*.py' |
    ForEach-Object { 'tests/' + $_.Name }
$files = @($files | Sort-Object -Unique)

foreach ($file in $files) {
    if ([IO.Path]::IsPathRooted($file) -or $file -notmatch '^[A-Za-z0-9._/-]+$' -or
        $file -match '(^|/)\.\.?(/|$)' -or $file.Split('/')[0] -in @('artifacts', 'outputs', 'state', '.venv', '.git')) {
        throw "Invalid public source path: $file"
    }
    $source = Join-Path $root $file
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing public source file: $file"
    }
    foreach ($segment in $file.Split('/')) {
        if ($segment -in @('bin', 'obj', '__pycache__')) {
            throw "Generated path in public source list: $file"
        }
    }
}

if ($ReleaseZip) {
    $addin = (Resolve-Path -LiteralPath $AddinPath).ProviderPath
    if (!(Test-Path -LiteralPath $addin -PathType Leaf) -or
        [IO.Path]::GetExtension($addin) -ine '.esriAddinX') {
        throw 'Supply a built acrgispro_mcp.esriAddinX file.'
    }
    $addinArchive = [IO.Compression.ZipFile]::OpenRead($addin)
    try {
        $expectedEntries = @('Config.daml', 'Install/SafeBridge.Native.dll', 'Install/SafeBridge.Native.deps.json')
        $entries = @($addinArchive.Entries | ForEach-Object FullName)
        if ($entries.Count -ne $expectedEntries.Count -or (Compare-Object $expectedEntries $entries -CaseSensitive)) {
            throw 'The add-in must contain only the expected SafeBridge build outputs.'
        }
        $damlReader = [IO.StreamReader]::new($addinArchive.GetEntry('Config.daml').Open())
        try {
            $daml = [xml]$damlReader.ReadToEnd()
        } finally {
            $damlReader.Dispose()
        }
        if ($daml.ArcGIS.AddInInfo.version -ne $manifest.native_min_version) {
            throw 'The add-in version does not match the release manifest.'
        }
    } finally {
        $addinArchive.Dispose()
    }
    $zipPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseZip)
    if ([IO.Path]::GetExtension($zipPath) -ne '.zip' -or (Test-Path -LiteralPath $zipPath)) {
        throw 'Choose a new .zip path for the portable release.'
    }
    $destinationPrefix = $destination.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($zipPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose a release ZIP path outside the public checkout.'
    }
}

foreach ($file in $files) {
    $target = Join-Path $destination $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
}
Write-Output "Public source exported to $destination"

if ($ReleaseZip) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $zipPath) -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $destination $file), $file) | Out-Null
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $addin, 'acrgispro_mcp.esriAddinX') | Out-Null
        $sum = (Get-FileHash -LiteralPath $addin -Algorithm SHA256).Hash.ToLowerInvariant()
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('SHA256SUMS.txt').Open())
        try {
            $writer.WriteLine("$sum  acrgispro_mcp.esriAddinX")
        } finally {
            $writer.Dispose()
        }
    } finally {
        $archive.Dispose()
    }
    Write-Output "Install ZIP created at $zipPath"
}
