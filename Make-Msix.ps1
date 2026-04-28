# Make-Msix.ps1 - Pack and sign the Xbox2 MSIX without deploying

$buildOutput = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
$msixOut     = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\AppPackages"
$msixPath    = "$msixOut\FlickrSlideshow.Xbox2.msix"
$makeappx    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signtool    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$certPath    = "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\DevCert.pfx"

New-Item -ItemType Directory -Force -Path $msixOut | Out-Null

$manifest = Get-Content "C:\code\FlickrSlideshow\FlickrSlideshow.Xbox2\Package.appxmanifest" -Raw
$manifest = $manifest -replace '<\?xml Version="[^"]*"',      '<?xml version="1.0"'
$manifest = $manifest -replace '\$targetnametoken\$',          'FlickrSlideshow.Xbox2'
$manifest = $manifest -replace '<Resource Language="x-generate"\s*/>', '<Resource Language="en-US" />'
$manifest = $manifest -replace 'EntryPoint="[^"]*"',           'EntryPoint="FlickrSlideshow.Xbox2.App"'
Set-Content "$buildOutput\AppxManifest.xml" $manifest -Encoding UTF8

Write-Host "Packing MSIX..."
& $makeappx pack /d $buildOutput /p $msixPath /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx failed"; exit 1 }

Write-Host "Done: $msixPath (unsigned - ready for Store submission)" -ForegroundColor Green
