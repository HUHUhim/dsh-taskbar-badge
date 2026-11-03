# Rebuild bin/dsh-taskbar-badge.exe from bin/dsh-taskbar-badge.cs.
# Requires Windows with .NET Framework 4.x and the WPF UI Automation
# reference assemblies (installed with the .NET Framework 3.0/3.5 tooling).
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "bin\dsh-taskbar-badge.cs"
$output = Join-Path $root "bin\dsh-taskbar-badge.exe"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "csc.exe not found. Install the .NET Framework 4.x developer tools."
}

$programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
$referenceDirectories = @(
    (Join-Path $env:ProgramFiles "Reference Assemblies\Microsoft\Framework\v3.0"),
    (Join-Path $programFilesX86 "Reference Assemblies\Microsoft\Framework\v3.0")
)

function Find-Reference([string]$name) {
    foreach ($directory in $referenceDirectories) {
        $candidate = Join-Path $directory $name
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Reference assembly not found: $name"
}

$references = @(
    (Find-Reference "UIAutomationClient.dll"),
    (Find-Reference "UIAutomationTypes.dll"),
    (Find-Reference "WindowsBase.dll"),
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll"
)

$arguments = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/out:$output"
) + ($references | ForEach-Object { "/reference:$_" }) + @($source)

& $csc @arguments
if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Get-Item $output | Select-Object FullName, Length, LastWriteTime
