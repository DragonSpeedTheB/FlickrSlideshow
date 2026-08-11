# Make-Msix.ps1 - Increment build version, pack the Xbox2 MSIX, and sign it for sideloading
param(
    [switch]$signme
)
$manifestSrc = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\Package.appxmanifest"
$buildOutput = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
$msixOut     = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\AppPackages"
$msixPath    = "$msixOut\FlickrSlideshow.Xbox2.msix"
$makeappx    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signtool    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$certPath    = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\DevCert.pfx"

# ── Increment build number (Major.Minor.Build.Revision) in the source manifest ──
$srcXml = Get-Content $manifestSrc -Raw
if ($srcXml -match '<Identity\b[^>]*\bVersion="(\d+)\.(\d+)\.(\d+)\.(\d+)"') {
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]
    $build = [int]$Matches[3] + 1
    $rev   = [int]$Matches[4]
    $newVersion = "$major.$minor.$build.$rev"
    $srcXml = $srcXml -replace '(<Identity\b[^>]*\bVersion=)"[\d.]+"', "`$1""$newVersion"""
    Set-Content $manifestSrc $srcXml -Encoding UTF8 -NoNewline
    Write-Host "Version bumped to $newVersion" -ForegroundColor Cyan
} else {
    Write-Error "Could not parse version from manifest."; exit 1
}

New-Item -ItemType Directory -Force -Path $msixOut | Out-Null

# ── Patch manifest copy for the build output folder ──
$manifest = Get-Content $manifestSrc -Raw
$manifest = $manifest -replace '<\?xml Version="[^"]*"',      '<?xml version="1.0"'
$manifest = $manifest -replace '\$targetnametoken\$',          'FlickrSlideshow.Xbox2'
$manifest = $manifest -replace '<Resource Language="x-generate"\s*/>', '<Resource Language="en-US" />'
$manifest = $manifest -replace 'EntryPoint="[^"]*"',           'EntryPoint="FlickrSlideshow.Xbox2.App"'
Set-Content "$buildOutput\AppxManifest.xml" $manifest -Encoding UTF8

# ── Pack ──
Write-Host "Packing MSIX..."
& $makeappx pack /d $buildOutput /p $msixPath /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx failed"; exit 1 }

# ── Sign (required for sideloading onto the Xbox dev-mode console) ──
if ($signme) 
{if(Test-Path $signtool) {
    Write-Host "Signing MSIX..."
    & $signtool sign /fd SHA256 /a /f $certPath /p "" $msixPath
    if ($LASTEXITCODE -ne 0) { Write-Warning "signtool failed - MSIX is unsigned." }
}
}

Write-Host "Done: $msixPath  (v$newVersion)" -ForegroundColor Green
