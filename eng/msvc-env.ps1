# Sets up the Microsoft C compiler (cl, link, the Windows SDK) for the following steps of a GitHub Actions job: what
# a Visual Studio Developer Command Prompt does. Visual Studio's vcvarsall.bat runs for the architecture, and the
# variables it sets are passed on through GITHUB_ENV and GITHUB_PATH.
#
#   pwsh eng/msvc-env.ps1 amd64      # or arm64 (on an Arm64 runner), x86, amd64_arm64 (cross)...
#
# The release uses it for the C smoke test on Windows. Native AOT does not need it: it finds Visual Studio itself.
param([Parameter(Mandatory)][string] $Arch)
$ErrorActionPreference = 'Stop'

$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$vs = & "$installer\vswhere.exe" -latest -products * -property installationPath
$vcvars = Join-Path "$vs" 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) { throw "No Visual Studio with the C++ tools (vcvarsall.bat) found: '$vs'." }

# vcvarsall.bat looks for vswhere on the PATH.
$after = cmd /c "set `"PATH=$installer;%PATH%`" && `"$vcvars`" $Arch >nul && set"
if ($LASTEXITCODE -ne 0) { throw "vcvarsall.bat $Arch failed." }

$path = $env:PATH -split ';'
$added = @()
$variables = @()
foreach ($line in $after) {
  $name, $value = $line -split '=', 2
  if ($name -eq 'Path') {
    $added = @($value -split ';' | Where-Object { $_ -and $_ -notin $path })
  } elseif ([Environment]::GetEnvironmentVariable($name) -cne $value) {
    $variables += "$name=$value"
  }
}
# vcvarsall.bat reports an unknown architecture but still succeeds: check that it found a compiler.
if (-not ($added | Where-Object { Test-Path (Join-Path $_ 'cl.exe') })) { throw "vcvarsall.bat $Arch set up no compiler (cl.exe)." }
$variables | Out-File -Append -Encoding utf8 $env:GITHUB_ENV
# The runner puts the last GITHUB_PATH line first: write them in reverse to keep vcvarsall's order.
[array]::Reverse($added)
$added | Out-File -Append -Encoding utf8 $env:GITHUB_PATH
Write-Output "MSVC $Arch from ${vs}: $($added.Count) folders added to the PATH."
