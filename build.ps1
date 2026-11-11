<#
  Builds Pipe Tagger for every Revit version and packs it into .\dist\<version>\

    .\build.ps1                     # all versions 2021-2027
    .\build.ps1 -Versions 2024,2026 # only some
    .\build.ps1 -Install            # build, then install into every Revit found on this PC

  dist\<version>\ holds exactly what goes into %APPDATA%\Autodesk\Revit\Addins\<version>\
  so you can zip it and give it to colleagues.
#>
param(
    [int[]]$Versions = (2021..2027),
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$hasNet10 = [bool]((dotnet --list-sdks) -match '^10\.')

foreach ($v in $Versions) {
    # Revit 2027 = .NET 10. Without the .NET 10 SDK, compile against the 2026 API on .NET 8
    $api = $v
    if ($v -ge 2027 -and -not $hasNet10) {
        Write-Warning "Revit ${v}: .NET 10 SDK not installed -> building against the 2026 API (.NET 8)."
        $api = 2026
    }

    Write-Host "=== Revit $v (API $api) ===" -ForegroundColor Cyan
    foreach ($proj in 'Core', 'Loader') {
        dotnet build (Join-Path $root $proj) -c Release -nologo -v q -clp:ErrorsOnly `
            "-p:RevitVersion=$v" "-p:ApiVersion=$api" "-p:DeployCore=false"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj for Revit $v" }
    }

    # Pack: dist\<v>\PipeTagger.addin + PipeTagger\PipeTagger.Loader.dll + PipeTagger\Core\*
    $out = Join-Path $dist $v
    $pt = Join-Path $out 'PipeTagger'
    New-Item -ItemType Directory -Force (Join-Path $pt 'Core') | Out-Null
    Copy-Item (Join-Path $root "Loader\bin\R$v\PipeTagger.Loader.dll") $pt -Force
    Copy-Item (Join-Path $root "Core\bin\R$v\PipeTagger.Core.dll"), (Join-Path $root "Core\bin\R$v\PipeTagger.Core.pdb") (Join-Path $pt 'Core') -Force
    Copy-Item (Join-Path $root 'Loader\PipeTagger.addin') $out -Force
}

Write-Host "`nPackages ready in $dist" -ForegroundColor Green
if ($Install) { & (Join-Path $root 'install.ps1') -Versions $Versions }
