# Compile a native Windows GUI using the installed .NET Framework compiler.
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $project 'GPU-Idle-Watch.exe' }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$references = @('System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Security.dll', 'WPF\WindowsBase.dll',
    'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$flags = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$output")
$icon = Join-Path $PSScriptRoot 'GpuWatch.ico'
$flags += "/win32icon:$icon", "/resource:$icon,GpuWatch.ico"
$flags += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$flags += '/resource:' + (Join-Path $PSScriptRoot 'Dashboard.xaml') + ',Dashboard.xaml'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'IdleNotice.xaml') + ',IdleNotice.xaml'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'AddServerWindow.xaml') + ',AddServerWindow.xaml'
$flags += '/resource:' + (Join-Path $project 'collect.py') + ',collect.py'
$sources = @('GpuMonitor.cs', 'Dashboard.cs', 'IdleNotice.cs', 'TrayController.cs', 'ServerConfig.cs',
    'AddServerWindow.cs', 'ServerStore.cs', 'HostTrust.cs', 'AskPass.cs')
$flags += $sources | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @flags
if ($LASTEXITCODE -ne 0) { throw "Compiler exited with code $LASTEXITCODE" }
Get-Item -LiteralPath $output | Select-Object FullName, Length
