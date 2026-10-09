param([string]$OutputName = 'Tuesday Mixer.exe')
$ErrorActionPreference = 'Stop'
$folder = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $folder 'TuesdayMixer.cs'
$output = Join-Path $folder $OutputName
$icon = Join-Path $folder 'tuesday.ico'

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Force
}

Add-Type -AssemblyName Microsoft.CSharp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $true
$parameters.GenerateInMemory = $false
$parameters.OutputAssembly = $output
$parameters.CompilerOptions = '/target:winexe /optimize+ /win32icon:"' + $icon + '"'
@('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') |
    ForEach-Object { [void]$parameters.ReferencedAssemblies.Add($_) }

$result = $provider.CompileAssemblyFromFile($parameters, $source)
if ($result.Errors.HasErrors) {
    $messages = $result.Errors | ForEach-Object { "$($_.FileName)($($_.Line),$($_.Column)): $($_.ErrorText)" }
    throw ($messages -join [Environment]::NewLine)
}

Write-Host "Built: $output"
