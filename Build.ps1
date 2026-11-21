param([string]$OutputFolder = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework C# compiler is required.'
}

$output = New-Item -ItemType Directory -Path $OutputFolder -Force
$exe = Join-Path $output.FullName 'ClipLeger.exe'
$references = @('WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll', 'System.Windows.Forms.dll')
$references = $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$options = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', "/out:$exe", "/resource:$PSScriptRoot\Clip.xaml,Clip.xaml", "/win32manifest:$PSScriptRoot\Clip.manifest")

& $compiler @options @references "$PSScriptRoot\Clip.cs" "$PSScriptRoot\Video.cs"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Write-Output $exe
