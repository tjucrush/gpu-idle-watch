# Save an SSH login password with Windows DPAPI for the current user.
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [Alias('Alias')]
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z')]
    [string]$SshAlias,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or !$env:LOCALAPPDATA -or ![IO.Path]::IsPathRooted($env:LOCALAPPDATA)) {
    throw 'Windows with a local user profile is required.'
}
$key = $SshAlias.ToLowerInvariant()
$directory = Join-Path $env:LOCALAPPDATA 'GpuWatch\credentials'
$destination = Join-Path $directory ("ssh-" + $key + '.dpapi')
if ($Remove) {
    if ([IO.File]::Exists($destination)) { [IO.File]::Delete($destination) }
    Write-Output "Credential cleared for SSH alias '$SshAlias'."
    return
}

Add-Type -AssemblyName System.Security
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$entropy = $utf8.GetBytes('GpuWatch SSH password v1:' + $key)
$secret = Read-Host "SSH login password for '$SshAlias'" -AsSecureString
if ($null -eq $secret) { throw 'No password was entered.' }
$buffer = [IntPtr]::Zero
$chars = $null
$bytes = $null
try {
    if ($secret.Length -eq 0) { throw 'An empty password cannot be saved.' }
    $chars = [char[]]::new($secret.Length)
    $buffer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secret)
    [Runtime.InteropServices.Marshal]::Copy($buffer, $chars, 0, $chars.Length)
    if ($chars -contains [char]0 -or $chars -contains [char]10 -or $chars -contains [char]13) {
        throw 'The password must fit on a single line.'
    }
    $bytes = $utf8.GetBytes($chars)
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
}
finally {
    if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    if ($null -ne $chars) { [Array]::Clear($chars, 0, $chars.Length) }
    if ($buffer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($buffer) }
    $secret.Dispose()
}

[void][IO.Directory]::CreateDirectory($directory)
$temporary = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($temporary, $encrypted)
    try {
        [IO.File]::Move($temporary, $destination)
    } catch [IO.IOException] {
        if (![IO.File]::Exists($destination)) { throw }
        [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value)
    }
} finally {
    [IO.File]::Delete($temporary)
}
Write-Output "Credential saved for SSH alias '$SshAlias'."
