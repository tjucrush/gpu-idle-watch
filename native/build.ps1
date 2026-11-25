# Compile a native Windows GUI using the installed .NET Framework compiler.
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $project 'GPU空闲卡监控.exe' }
$askPass = Join-Path ([IO.Path]::GetDirectoryName($output)) 'GpuWatch.AskPass.exe'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
if ($output -eq $askPass) { throw 'Choose a dashboard output name other than GpuWatch.AskPass.exe.' }
$references = @('System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'WPF\WindowsBase.dll',
    'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$flags = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$output")
$icon = Join-Path $PSScriptRoot 'GpuWatch.ico'
$flags += "/win32icon:$icon", "/resource:$icon,GpuWatch.ico"
$flags += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$flags += '/resource:' + (Join-Path $PSScriptRoot 'Dashboard.xaml') + ',Dashboard.xaml'
$flags += '/resource:' + (Join-Path $PSScriptRoot 'IdleNotice.xaml') + ',IdleNotice.xaml'
$flags += '/resource:' + (Join-Path $project 'collect.py') + ',collect.py'
$flags += (Join-Path $PSScriptRoot 'GpuMonitor.cs'), (Join-Path $PSScriptRoot 'Dashboard.cs'), (Join-Path $PSScriptRoot 'IdleNotice.cs'), (Join-Path $PSScriptRoot 'TrayController.cs'), (Join-Path $PSScriptRoot 'ServerConfig.cs')
& $compiler @flags
if ($LASTEXITCODE -ne 0) { throw "Compiler exited with code $LASTEXITCODE" }
& $compiler '/nologo' '/target:winexe' '/optimize+' '/codepage:65001' "/out:$askPass" ('/reference:' + (Join-Path $framework 'System.Security.dll')) (Join-Path $PSScriptRoot 'AskPass.cs')
if ($LASTEXITCODE -ne 0) { throw "AskPass compiler exited with code $LASTEXITCODE" }
Get-Item -LiteralPath $output, $askPass | Select-Object FullName, Length
