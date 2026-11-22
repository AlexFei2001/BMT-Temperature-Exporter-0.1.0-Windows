param(
    [string]$Dotnet = 'dotnet',
    [string]$InnoCompiler = 'ISCC.exe',
    [string]$Python = 'python',
    [string]$RegistrationSource
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $portable = Join-Path $PSScriptRoot 'dist\BmtStudio-0.2.1-win-x64'
    if (!$RegistrationSource) {
        $RegistrationSource = Join-Path $PSScriptRoot 'registration_source'
        if (!(Test-Path -LiteralPath $RegistrationSource)) {
            $RegistrationSource = Join-Path $PSScriptRoot '../../legacy_crack_pipeline'
        }
    }
    $source = (Resolve-Path -LiteralPath $RegistrationSource).Path
    $workerDist = Join-Path $PSScriptRoot 'obj/worker-dist'
    & $Python -m PyInstaller --noconfirm --onedir --console --name BmtRegistrationWorker --paths $source --distpath $workerDist --workpath obj/worker-work --specpath obj registration_worker.py
    if ($LASTEXITCODE -ne 0) { throw 'Registration worker build failed. Install registration-build-requirements.txt in the build environment.' }
    & $Dotnet publish BmtExporter.csproj -c Release -r win-x64 --self-contained true -o $portable
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    $workerTarget = Join-Path $portable 'registration'
    New-Item -ItemType Directory -Force $workerTarget | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $workerDist 'BmtRegistrationWorker') |
        Copy-Item -Destination $workerTarget -Recurse -Force
    $runtimeBase = & $Python -c 'import sys; print(sys.base_prefix)'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the Python runtime license.' }
    $pythonLicense = Join-Path $runtimeBase 'LICENSE.txt'
    if (!(Test-Path -LiteralPath $pythonLicense)) { $pythonLicense = Join-Path $runtimeBase 'LICENSE_PYTHON.txt' }
    if (!(Test-Path -LiteralPath $pythonLicense)) { throw 'Python runtime LICENSE.txt is missing.' }
    Copy-Item -LiteralPath $pythonLicense -Destination (Join-Path $workerTarget 'PYTHON-LICENSE.txt')
    $site = & $Python -c 'import site; print(site.getsitepackages()[-1])'
    foreach ($dist in Get-ChildItem -LiteralPath $site -Directory | Where-Object Name -Match '^(numpy|opencv_python_headless|pyinstaller)-.*\.dist-info$') {
        Get-ChildItem -LiteralPath $dist.FullName -Recurse -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE)' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $workerTarget "$($dist.Name)-$($_.Name)") }
    }
    Copy-Item -LiteralPath USER-GUIDE.txt,STUDIO.md,FEASIBILITY.md,COMPATIBILITY.md,VALIDATION.md,reference.example.json -Destination $portable
    $deps = Get-Content -Raw (Join-Path $portable 'BmtExporter.deps.json') | ConvertFrom-Json
    $assets = Get-Content -Raw 'obj/project.assets.json' | ConvertFrom-Json
    $pkgRoots = $assets.packageFolders.PSObject.Properties.Name
    foreach ($library in $deps.libraries.PSObject.Properties.Name) {
        if ($library -notmatch '^runtimepack\.(Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/([^/]+)$') { continue }
        $package = $Matches[1].ToLowerInvariant()
        $version = $Matches[2]
        $pack = $pkgRoots | ForEach-Object { Join-Path $_ "$package/$version" } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$pack) { throw "Runtime notices not found for $library." }
        Get-ChildItem -LiteralPath $pack -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $portable "$package-$($_.Name).txt") }
    }
    Compress-Archive -LiteralPath $portable -DestinationPath "$portable.zip" -Force
    & $InnoCompiler /Q installer.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath "$portable.zip",(Join-Path $PSScriptRoot 'dist\BmtStudio-0.2.1-Setup-x64.exe')
}
finally { Pop-Location }
