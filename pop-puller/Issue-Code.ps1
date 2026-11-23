param(
    [Parameter(Mandatory = $true)]
    [ValidateLength(1, 60)]
    [string]$Label
)

$ErrorActionPreference = 'Stop'
$keyFile = Join-Path $env:LOCALAPPDATA 'GFW8PopPuller\admin-key.dpapi.bin'
if (-not (Test-Path -LiteralPath $keyFile)) {
    throw 'The hub owner key is missing from this Windows profile.'
}

Add-Type -AssemblyName System.Security
$protected = [IO.File]::ReadAllBytes($keyFile)
$plain = [Security.Cryptography.ProtectedData]::Unprotect(
    $protected, [Text.Encoding]::UTF8.GetBytes('GFW8 Pop Puller admin v1'),
    [Security.Cryptography.DataProtectionScope]::CurrentUser)
try {
    $key = [Text.Encoding]::UTF8.GetString($plain)
    $body = @{ label = $Label.Trim() } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Method Post `
        -Uri 'https://gfw8-pop-puller-hub.alec2dabreen.workers.dev/admin/issue' `
        -Headers @{ Authorization = ('Bearer ' + $key) } `
        -ContentType 'application/json' -Body $body
    if ($response.code -notmatch '^GFW8H-[A-Za-z0-9_-]{43}$') {
        throw 'The hub did not return a valid access code.'
    }
    Write-Output ('Access code for ' + $Label.Trim() + ': ' + $response.code)
    Write-Output 'Copy it now. The hub stores only its hash and cannot display it again.'
} finally {
    [Array]::Clear($plain, 0, $plain.Length)
}
