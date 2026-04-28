# Deploy-Xbox.ps1
# Packs the loose build output into an MSIX, signs it, and deploys to Xbox via Device Portal.
# Reads connection settings from Deploy-Xbox.config.ps1 (not committed to source control).

param(
    [string]$PackagePath
)

$configFile = "$PSScriptRoot\Deploy-Xbox.config.ps1"
if (-not (Test-Path $configFile)) {
    Write-Error "Missing config file: $configFile`nCopy Deploy-Xbox.config.template.ps1 to Deploy-Xbox.config.ps1 and fill in your Xbox details."
    exit 1
}
. $configFile

if (-not $PackagePath) {
    $buildOutput = "$PSScriptRoot\FlickrSlideshow.Xbox2\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
    $msixOut     = "$PSScriptRoot\FlickrSlideshow.Xbox2\AppPackages"
    $msixPath    = "$msixOut\FlickrSlideshow.Xbox2.msix"
    $makeappx    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
    $signtool    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
    $certPath    = "$PSScriptRoot\FlickrSlideshow.Xbox2\DevCert.pfx"

    if (-not (Test-Path $buildOutput)) {
        Write-Error "Build output not found at $buildOutput. Build the project (Release x64) first."
        exit 1
    }

    New-Item -ItemType Directory -Force -Path $msixOut | Out-Null

    Write-Host "Packing MSIX from loose files..."
    # makeappx requires a standard AppxManifest.xml - fix non-standard xml declaration and build tokens
    $manifestContent = Get-Content "$PSScriptRoot\FlickrSlideshow.Xbox2\Package.appxmanifest" -Raw
    $manifestContent = $manifestContent -replace '<\?xml Version="[^"]*"', '<?xml version="1.0"'
    $manifestContent = $manifestContent -replace '\$targetnametoken\$', 'FlickrSlideshow.Xbox2'
    $manifestContent = $manifestContent -replace 'EntryPoint="[^"]*"', 'EntryPoint="FlickrSlideshow.Xbox2.App"'
    $manifestContent = $manifestContent -replace '<Resource Language="x-generate"\s*/>', '<Resource Language="en-US" />'
    Set-Content "$buildOutput\AppxManifest.xml" $manifestContent -Encoding UTF8
    & $makeappx pack /d $buildOutput /p $msixPath /o
    if ($LASTEXITCODE -ne 0) { Write-Error "makeappx failed."; exit 1 }

    Write-Host "Signing MSIX..."
    & $signtool sign /fd SHA256 /a /f $certPath /p "FlickrDev123!" $msixPath
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed."; exit 1 }

    $PackagePath = $msixPath
}

if (-not $PackagePath -or -not (Test-Path $PackagePath)) {
    Write-Error "No MSIX package found. Build the project first or pass -PackagePath explicitly."
    exit 1
}

Write-Host "Deploying: $PackagePath"
Write-Host "Target Xbox: $XboxIP"

$baseUri = "https://${XboxIP}:11443"
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${XboxUser}:${XboxPass}"))
$headers = @{ Authorization = "Basic $cred" }

# Uninstall any existing version of the package before deploying
Write-Host "Checking for existing installation..."
$packages = Invoke-RestMethod -Uri "$baseUri/api/app/packagemanager/packages" -Headers $headers -SkipCertificateCheck
$existing = $packages.InstalledPackages | Where-Object { $_.PackageFamilyName -like "Dragonspeed.FlickrSlideshowRandomizer*" -or $_.PackageFamilyName -like "FlickrSlideshow.Xbox2*" }
foreach ($pkg in $existing) {
    Write-Host "Uninstalling $($pkg.PackageFullName)..."
    Invoke-RestMethod -Uri "$baseUri/api/app/packagemanager/package?package=$([System.Web.HttpUtility]::UrlEncode($pkg.PackageFullName))" -Method DELETE -Headers $headers -SkipCertificateCheck | Out-Null
    Start-Sleep -Seconds 2
}

# Upload the package using multipart form-data with filename in query string
Write-Host "Uploading package..."
$fileName = [System.IO.Path]::GetFileName($PackagePath)
$fileBytes = [System.IO.File]::ReadAllBytes($PackagePath)
$encodedName = [System.Web.HttpUtility]::UrlEncode($fileName)
$boundary = "----BoundaryXbox" + [System.Guid]::NewGuid().ToString("N")
$nl = "`r`n"

$ms = New-Object System.IO.MemoryStream
$enc = [System.Text.Encoding]::UTF8
$headerStr = "--$boundary$nl" +
             "Content-Disposition: form-data; name=`"$fileName`"; filename=`"$fileName`"$nl" +
             "Content-Type: application/octet-stream$nl$nl"
$headerBytes = $enc.GetBytes($headerStr)
$ms.Write($headerBytes, 0, $headerBytes.Length)
$ms.Write($fileBytes, 0, $fileBytes.Length)
$footerBytes = $enc.GetBytes("$nl--$boundary--$nl")
$ms.Write($footerBytes, 0, $footerBytes.Length)
$bodyBytes = $ms.ToArray()

try {
    $response = Invoke-WebRequest `
        -Uri "$baseUri/api/app/packagemanager/package?package=$encodedName" `
        -Method POST `
        -Headers $headers `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $bodyBytes `
        -UseBasicParsing `
        -SkipCertificateCheck `
        -TimeoutSec 300

    Write-Host "Upload response: $($response.StatusCode) $($response.StatusDescription)"
} catch {
    Write-Error "Upload failed: $($_.ErrorDetails.Message ?? $_)"
    exit 1
}

# Poll installation status
Write-Host "Waiting for installation to complete..."
$timeout = 180
$elapsed = 0
do {
    Start-Sleep -Seconds 2
    $elapsed += 2
    try {
        $status = Invoke-RestMethod `
            -Uri "$baseUri/api/app/packagemanager/state" `
            -Method GET `
            -Headers $headers `
            -UseBasicParsing `
            -SkipCertificateCheck
        Write-Host "  State: $($status.DeploymentStatus) ($elapsed s) - $($status | ConvertTo-Json -Compress)"
        if ($status.DeploymentStatus -eq "Ok" -or $status.Success -eq $true) {
            Write-Host "Deployment succeeded!" -ForegroundColor Green
            exit 0
        }
        if ($status.DeploymentStatus -eq "Error" -or $status.Success -eq $false) {
            Write-Error "Deployment failed: $($status.Reason)"
            exit 1
        }
    } catch {
        Write-Host "  (polling...) $_"
    }
} while ($elapsed -lt $timeout)

Write-Warning "Timed out waiting for deployment status."
