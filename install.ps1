<#
  Installs Pipe Tagger from .\dist into every Revit version found on this PC.

    .\install.ps1
    .\install.ps1 -Versions 2026

  Core is always updated (it is never locked).
  The Loader is locked while that Revit is open: close Revit and run again to update it.
#>
param([int[]]$Versions = (2021..2027))
$root = $PSScriptRoot

foreach ($v in $Versions) {
    $src = Join-Path $root "dist\$v"
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $v\Revit.exe")) { continue }
    if (-not (Test-Path $src)) { Write-Warning "Revit ${v}: no package in dist - run build.ps1 first"; continue }

    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    New-Item -ItemType Directory -Force (Join-Path $dest 'PipeTagger\Core') | Out-Null

    Copy-Item (Join-Path $src 'PipeTagger.addin') $dest -Force
    Copy-Item (Join-Path $src 'PipeTagger\Core\*') (Join-Path $dest 'PipeTagger\Core') -Force

    try {
        Copy-Item (Join-Path $src 'PipeTagger\PipeTagger.Loader.dll') (Join-Path $dest 'PipeTagger') -Force -ErrorAction Stop
        Write-Host "Revit ${v}: installed" -ForegroundColor Green
    }
    catch {
        Write-Warning "Revit ${v}: Core updated, but the Loader is in use (Revit is open). Close Revit $v and run install.ps1 again if the Loader changed."
    }

    # Clean up the DLL from the very first version (no longer used)
    $old = Join-Path $dest 'PipeTagger\PipeTagger.dll'
    if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
}
