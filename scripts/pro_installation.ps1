function Get-GisAgentProExecutable([string]$ExplicitPath = '', [switch]$Required) {
    if ($ExplicitPath) {
        $path = [IO.Path]::GetFullPath($ExplicitPath)
        if ([IO.Path]::GetFileName($path) -ine 'ArcGISPro.exe' -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'Provide the path to an existing ArcGISPro.exe.'
        }
        return $path
    }

    foreach ($hive in 'HKCU:', 'HKLM:') {
        $key = Get-Item -LiteralPath "$hive\SOFTWARE\ESRI\ArcGISPro" -ErrorAction SilentlyContinue
        if ($key -and ($root = $key.GetValue('InstallDir'))) {
            $path = Join-Path $root 'bin/ArcGISPro.exe'
            if (Test-Path -LiteralPath $path -PathType Leaf) { return [IO.Path]::GetFullPath($path) }
        }
    }

    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    foreach ($root in @($programFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($root) {
            $path = Join-Path $root 'ArcGIS/Pro/bin/ArcGISPro.exe'
            if (Test-Path -LiteralPath $path -PathType Leaf) { return [IO.Path]::GetFullPath($path) }
        }
    }
    if ($Required) { throw 'ArcGIS Pro was not found. Install ArcGIS Pro or provide -ArcGISProExe.' }
}
