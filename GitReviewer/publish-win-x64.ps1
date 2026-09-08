$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "GitReviewer.csproj"
[xml]$project = Get-Content $projectPath
$version = $project.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $version) {
    throw "The project version is not defined."
}

$outputDirectory = Join-Path $PSScriptRoot "dist"
$assemblyName = "GitReviewer-$version-win-x64"
$executablePath = Join-Path $outputDirectory "$assemblyName.exe"
Remove-Item $executablePath -Force -ErrorAction SilentlyContinue

& dotnet publish `
    $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:AssemblyName=$assemblyName `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path $executablePath)) {
    throw "The published executable was not created."
}

Write-Host "Published: $executablePath"
