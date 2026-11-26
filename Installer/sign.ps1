# Signs a file with the "Manggdy" code signing certificate (Authenticode, SHA-256).
# Used by build.ps1 for Zipper.exe and by Inno Setup (SignTool) for the setup and uninstaller.
#   powershell -ExecutionPolicy Bypass -File Installer\sign.ps1 <file>
# The first run creates a self-signed certificate in the current user's store (Cert:\CurrentUser\My).
# Its private key never leaves that store. A self-signed certificate is not trusted by other PCs,
# so Windows still warns there; replace it with a certificate from a code signing CA to remove the warning.
param([Parameter(Mandatory)][string]$File)
$ErrorActionPreference = 'Stop'
$subject = 'CN=Manggdy'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName 'Manggdy Code Signing' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
    Write-Host "Created code signing certificate $subject ($($cert.Thumbprint))"
}
# A timestamp keeps the signature valid after the certificate expires; sign without one when offline.
$sig = $null
foreach ($ts in 'http://timestamp.digicert.com', 'http://timestamp.sectigo.com') {
    try { $sig = Set-AuthenticodeSignature -FilePath $File -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $ts; break } catch { }
}
if (-not $sig) { $sig = Set-AuthenticodeSignature -FilePath $File -Certificate $cert -HashAlgorithm SHA256 }
# Status is UnknownError for a self-signed chain this PC does not trust; the signature itself is in place.
if (-not $sig.SignerCertificate) { throw "Signing failed: $File ($($sig.StatusMessage))" }
Write-Host "Signed $File"
