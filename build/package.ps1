# Local equivalent of .github/workflows/release.yml (no GitHub, no tag).
#   ./build/package.ps1 -Version 0.1.0.1 -BaseUrl http://host.docker.internal:8000
# Writes dist/jellyshare_<Version>.zip and dist/manifest.json (a COPY of manifest.json with the new version
# added, sourceUrl = <BaseUrl>/jellyshare_<Version>.zip). The committed manifest.json is not touched.
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$BaseUrl,
    [string]$Python = "py",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must look like 0.1.0.0" }
    if (-not $SkipTests) {
        dotnet test -c Release; if ($LASTEXITCODE) { throw "dotnet test failed" }
        & $Python -m unittest discover -s build -p "test_*.py"; if ($LASTEXITCODE) { throw "build tests failed" }
    }
    $pub = Join-Path $root "dist/publish"
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
    dotnet publish src/Jellyfin.Plugin.JellyShare -c Release -o $pub "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version"
    if ($LASTEXITCODE) { throw "dotnet publish failed" }
    & $Python build/package.py --publish-dir $pub --version $Version --out dist
    if ($LASTEXITCODE) { throw "package failed" }
    Set-Content dist/changelog.txt "Local build $Version"
    Copy-Item manifest.json dist/manifest.json -Force
    & $Python build/update_manifest.py --manifest dist/manifest.json --version $Version `
        --zip "dist/jellyshare_$Version.zip" --source-url "$BaseUrl/jellyshare_$Version.zip" --changelog-file dist/changelog.txt
    if ($LASTEXITCODE) { throw "manifest update failed" }
    Get-Content dist/manifest.json
} finally { Pop-Location }
