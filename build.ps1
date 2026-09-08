param([string]$DotNet = 'dotnet')

$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'SurfaceEdge.exe'
if (Get-Process -Name SurfaceEdge -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $output }) {
    throw 'Exit SurfaceEdge from the tray before building.'
}
& $DotNet publish (Join-Path $PSScriptRoot 'SurfaceEdge.csproj') -c Release -r win-arm64 --self-contained true -o (Join-Path $PSScriptRoot 'bin\publish')
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$candidate = Join-Path $PSScriptRoot 'bin\publish\SurfaceEdge.exe'
$reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($candidate))
try {
    $reader.BaseStream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0xAA64) {
        throw 'The executable is not an ARM64 PE image.'
    }
} finally { $reader.Dispose() }
$process = Start-Process -FilePath $candidate -ArgumentList '--self-test' -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Self-test failed. See $env:LOCALAPPDATA\SurfaceEdge\self-test.txt" }
Copy-Item -LiteralPath $candidate -Destination $output -Force
Write-Host "Built: $output"
Write-Host "Test report: $env:LOCALAPPDATA\SurfaceEdge\self-test.txt"
