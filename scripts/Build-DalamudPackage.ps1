param(
    [string]$Configuration = "Release",
    [switch]$IncludePdb
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repo "BarGame\BarGame.csproj"
$manifestPath = Join-Path $repo "BarGame\BarGame.json"
$solutionPath = Join-Path $repo "BarGame.sln"

[xml]$projectXml = Get-Content $projectPath
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
$assemblyVersion = [string]($projectXml.Project.PropertyGroup.AssemblyVersion | Select-Object -First 1)
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($assemblyVersion)) {
    throw "BarGame project version metadata is missing."
}
if ($manifest.AssemblyVersion -ne $assemblyVersion) {
    throw "BarGame.json AssemblyVersion does not match BarGame.csproj ($assemblyVersion)."
}

Write-Host "Building BarGame $version ($Configuration)..."
$started = Get-Date
& dotnet build $solutionPath -c $Configuration -t:Rebuild
if ($LASTEXITCODE -ne 0) {
    throw "BarGame build failed (exit code $LASTEXITCODE). No package was created."
}

$buildRoot = Join-Path $repo "BarGame\bin\$Configuration"
$pluginDll = Get-ChildItem $buildRoot -Recurse -File -Filter BarGame.dll |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $pluginDll -or $pluginDll.LastWriteTime -lt $started.AddSeconds(-3)) {
    throw "The build did not produce a fresh BarGame.dll under $buildRoot."
}
$outputFolder = $pluginDll.Directory.FullName
$manifestSource = Join-Path $outputFolder "BarGame.json"
if (-not (Test-Path $manifestSource)) {
    # The manifest is a source asset; do not pick up a stale build manifest.
    $manifestSource = $manifestPath
}
$builtManifest = Get-Content $manifestSource -Raw | ConvertFrom-Json
if ($builtManifest.AssemblyVersion -ne $assemblyVersion) {
    throw "The build output contains an outdated plugin manifest."
}

$dist = Join-Path $repo "dist"
$stage = Join-Path $repo "release"
New-Item -ItemType Directory $dist -Force | Out-Null
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage -Force | Out-Null
Copy-Item $pluginDll.FullName (Join-Path $stage "BarGame.dll")
Copy-Item $manifestSource (Join-Path $stage "BarGame.json")
$deps = Join-Path $outputFolder "BarGame.deps.json"
if (Test-Path $deps) { Copy-Item $deps $stage }
if ($IncludePdb) {
    $pdb = Join-Path $outputFolder "BarGame.pdb"
    if (Test-Path $pdb) { Copy-Item $pdb $stage }
}
# Additional user dependencies (if any), never bundle the Dalamud runtime.
Get-ChildItem $outputFolder -File -Filter "*.dll" |
    Where-Object { $_.Name -ne "BarGame.dll" -and $_.Name -notlike "Dalamud*" } |
    ForEach-Object { Copy-Item $_.FullName $stage }

$zip = Join-Path $dist "BarGame-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('BarGame.dll', 'BarGame.json')) {
        if ($names -notcontains $required) { throw "Package missing required root file: $required" }
    }
} finally { $archive.Dispose() }
Write-Host "Created $zip" -ForegroundColor Green
