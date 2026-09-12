# Generates the RSA key pair that fixes the extension's identity.
#
# An unpacked extension normally gets an ID derived from the folder it was loaded from, which
# changes on every machine and every move. That would make the native messaging manifest --
# which has to name the extension by ID in allowed_origins -- impossible to ship. Embedding the
# public key in manifest.json pins the ID instead, and it stays the same if the extension is
# ever published.
#
# Run once. The public half belongs in manifest.json; the private half is only needed to sign a
# .crx or to claim the same ID in a store listing, and is written outside the repository.
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$privatePath = Join-Path $projectRoot 'artifacts/extension-key.pem'

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $publicKey = $rsa.ExportSubjectPublicKeyInfo()
    $manifestKey = [Convert]::ToBase64String($publicKey)

    # Chrome derives the ID from the first 16 bytes of the SHA-256 of the DER public key,
    # rendering each nibble as a letter from 'a' to 'p'.
    $digest = [System.Security.Cryptography.SHA256]::HashData($publicKey)
    $builder = [Text.StringBuilder]::new()
    foreach ($byte in $digest[0..15]) {
        [void]$builder.Append([char](97 + ($byte -shr 4)))
        [void]$builder.Append([char](97 + ($byte -band 0x0F)))
    }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($privatePath)) | Out-Null
    $pem = [System.Security.Cryptography.PemEncoding]::Write('PRIVATE KEY', $rsa.ExportPkcs8PrivateKey())
    [IO.File]::WriteAllText($privatePath, [string]::new($pem))

    Write-Output "Extension ID: $($builder.ToString())"
    Write-Output "manifest.json key: $manifestKey"
    Write-Output "Private key (keep out of version control): $privatePath"
} finally {
    $rsa.Dispose()
}
