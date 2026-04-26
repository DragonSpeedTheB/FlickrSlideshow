<#
.SYNOPSIS
    Build FlickrSlideshow.Xbox as an MSIX and deploy it to an Xbox Series X via Device Portal.

.DESCRIPTION
    1. Builds the project in Release configuration, producing an .msix file.
    2. Uploads and installs that .msix to the Xbox using the Xbox Device Portal REST API.

.PARAMETER XboxIP
    IP address (or hostname) of the Xbox on your network.
    Find it on Xbox: Settings > System > Console info, or Settings > General > Network settings.

.PARAMETER Username
    Xbox Device Portal username (set in Settings > Developer settings > Device Portal credentials).
    Default: "auto"

.PARAMETER Password
    Xbox Device Portal password.

.EXAMPLE
    .\Deploy-ToXbox.ps1 -XboxIP 192.168.1.42 -Username auto -Password "mypassword"
#>

param(
    [Parameter(Mandatory)]
    [string] $XboxIP,

    [string] $Username = "auto",

    [Parameter(Mandatory)]
    [string] $Password
)

$ErrorActionPreference = "Stop"

$ProjectDir  = "$PSScriptRoot"
$ProjectFile = "$ProjectDir\FlickrSlideshow.Xbox.csproj"
$OutDir      = "$ProjectDir\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\AppPackages"
$MSBuild     = "${env:ProgramFiles}\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe"

# ── 1. Build MSIX ────────────────────────────────────────────────────────────

Write-Host "`n[1/3] Building MSIX (Release|x64)..." -ForegroundColor Cyan

& $MSBuild $ProjectFile `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:GenerateAppxPackageOnBuild=true `
    /p:AppxPackageSigningEnabled=false `
    /restore `
    /v:m

if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (exit $LASTEXITCODE)" }

# ── 2. Locate the built .msix ────────────────────────────────────────────────

$msix = Get-ChildItem -Path $OutDir -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

if (-not $msix) {
    # Fallback: check AppPackages next to the exe
    $msix = Get-ChildItem -Path "$ProjectDir\bin\x64\Release" -Filter "*.msix" -Recurse |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
}

if (-not $msix) { throw "No .msix found under $ProjectDir\bin. Build may have succeeded without packaging." }

Write-Host "[2/3] Found package: $($msix.FullName)" -ForegroundColor Cyan

# ── 3. Upload to Xbox Device Portal ─────────────────────────────────────────

Write-Host "[3/3] Uploading to Xbox at $XboxIP ..." -ForegroundColor Cyan

# Xbox Device Portal listens on port 11443 (HTTPS, self-signed cert)
$baseUrl  = "https://$XboxIP`:11443"
$cred     = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${Username}:${Password}"))
$headers  = @{ Authorization = "Basic $cred" }

# Skip certificate validation for self-signed Xbox cert
if (-not ("TrustAll" -as [type])) {
    Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAll : ICertificatePolicy {
    public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int problem) { return true; }
}
"@
}
[Net.ServicePointManager]::CertificatePolicy = New-Object TrustAll
[Net.ServicePointManager]::SecurityProtocol  = [Net.SecurityProtocolType]::Tls12

# Upload the package
$uri      = "$baseUrl/api/app/packagemanager/package?package=$([Uri]::EscapeDataString($msix.Name))"
$bytes    = [IO.File]::ReadAllBytes($msix.FullName)
$boundary = [Guid]::NewGuid().ToString("N")
$body     = [Text.Encoding]::UTF8.GetBytes(
    "--$boundary`r`nContent-Disposition: form-data; name=`"file`"; filename=`"$($msix.Name)`"`r`nContent-Type: application/octet-stream`r`n`r`n"
) + $bytes + [Text.Encoding]::UTF8.GetBytes("`r`n--$boundary--`r`n")

try {
    $resp = Invoke-WebRequest -Uri $uri -Method POST -Headers $headers `
               -ContentType "multipart/form-data; boundary=$boundary" `
               -Body $body -UseBasicParsing
    Write-Host "Upload response: $($resp.StatusCode) $($resp.StatusDescription)" -ForegroundColor Green
}
catch {
    $status = $_.Exception.Response?.StatusCode.value__
    $detail = $_.ErrorDetails?.Message
    throw "Upload failed (HTTP $status): $detail`n$_"
}

Write-Host "`nDone! Launch 'Flickr Slideshow' from My games & apps on your Xbox." -ForegroundColor Green
