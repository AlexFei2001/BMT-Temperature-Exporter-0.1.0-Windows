param([string]$Dotnet = 'dotnet', [string]$InnoCompiler = 'ISCC.exe')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $portable = Join-Path $PSScriptRoot 'dist\BmtExporter-0.1.0-win-x64'
    & $Dotnet publish BmtExporter.csproj -c Release -r win-x64 --self-contained true -o $portable
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    Copy-Item -LiteralPath USER-GUIDE.txt,FEASIBILITY.md,COMPATIBILITY.md,VALIDATION.md,reference.example.json -Destination $portable
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
    Get-FileHash -Algorithm SHA256 -LiteralPath "$portable.zip",(Join-Path $PSScriptRoot 'dist\BmtExporter-0.1.0-Setup-x64.exe')
}
finally { Pop-Location }
