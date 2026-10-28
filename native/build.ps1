# Build quiche as a shared library for one Windows .NET runtime
# identifier (RID) and stage it under native/out/<rid>/.
#
# Usage: native/build.ps1 -Rid <win-x64|win-arm64> [-QuicheDir <path>]
#
# This is the Windows counterpart of native/build.sh. It exists as a
# separate script on purpose: running cargo under Git Bash on Windows
# breaks in two documented ways, and working around both from bash is
# more fragile than not using bash at all:
#   - Git for Windows' Unix `link` shadows MSVC's link.exe in PATH
#     (ilammy/msvc-dev-cmd's own README documents this).
#   - MSYS2's environment conversion mangles the semicolon-separated
#     LIB/INCLUDE paths vcvarsall sets, so MSVC's link.exe then fails
#     with LNK1181 "cannot open input file 'kernel32.lib'".
# Under PowerShell, msvc-dev-cmd's environment (LIB, INCLUDE, PATH)
# reaches cargo unchanged, and link.exe resolves to MSVC's.
#
# The script clones quiche at the pinned tag from
# native/QUICHE_VERSION (unless -QuicheDir points at an existing
# checkout), verifies the commit SHA, and runs:
#   cargo build --release -p quiche --features ffi --target <target>
#
# As belt-and-braces, cargo's linker is pinned to MSVC's link.exe via
# VCToolsInstallDir (exported by msvc-dev-cmd), and LIBCLANG_PATH is
# pointed at the Chocolatey LLVM install when present (boring-sys's
# bindgen needs libclang).
#
# Output layout (what the NuGet runtime packages consume):
#   native/out/<rid>/quiche.dll
# plus LICENSE-quiche.txt and LICENSE-boringssl.txt alongside.
# No stripping: Windows has no strip in the standard toolchain.
#
# A post-build dumpbin check fails the build if the staged DLL does
# not export the core C API entry points, so a bad artifact can never
# silently ride into a package.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid,

    [string]$QuicheDir = ''
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

# --- RID -> cargo target + MSVC host/target dirs --------------------------
switch ($Rid) {
    'win-x64'   { $CargoTarget = 'x86_64-pc-windows-msvc';   $HostArch = 'Hostx64/x64' }
    'win-arm64' { $CargoTarget = 'aarch64-pc-windows-msvc';  $HostArch = 'Hostarm64/arm64' }
}
$Lib = 'quiche.dll'

# --- Pinned version ---------------------------------------------------------
$VersionFile = Get-Content "$ScriptDir/QUICHE_VERSION" | Where-Object { $_ -match '=' -and $_ -notmatch '^\s*#' }
$Version = @{}
foreach ($Line in $VersionFile) {
    $Key, $Value = $Line -split '=', 2
    $Version[$Key.Trim()] = $Value.Trim()
}
$QuicheTag = $Version['QUICHE_TAG']
$QuicheCommit = $Version['QUICHE_COMMIT']
$QuicheRepo = $Version['QUICHE_REPO']

# --- Obtain quiche source ---------------------------------------------------
if (-not $QuicheDir) {
    $QuicheDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '\quiche')
    Write-Host "cloning quiche $QuicheTag ..."
    git clone --quiet --depth 1 --branch $QuicheTag $QuicheRepo $QuicheDir
}

$ActualCommit = (git -C $QuicheDir rev-parse HEAD).Trim()
if ($ActualCommit -ne $QuicheCommit) {
    throw "FATAL: quiche checkout is $ActualCommit, expected $QuicheCommit"
}
Write-Host "quiche $QuicheTag @ $QuicheCommit verified"

# --- Toolchain environment ---------------------------------------------------
if ($env:VCToolsInstallDir) {
    $Linker = Join-Path $env:VCToolsInstallDir "bin\$HostArch\link.exe"
    $LinkerVar = 'CARGO_TARGET_' + ($CargoTarget.ToUpper() -replace '-', '_') + '_LINKER'
    Set-Item -Path "env:$LinkerVar" -Value $Linker
    Write-Host "Windows linker pinned: $Linker"
} else {
    Write-Warning 'VCToolsInstallDir unset; linker resolves via PATH'
}

$LlvmBin = 'C:\Program Files\LLVM\bin'
if ((Test-Path "$LlvmBin\libclang.dll") -and (-not $env:LIBCLANG_PATH)) {
    $env:LIBCLANG_PATH = $LlvmBin
    Write-Host "LIBCLANG_PATH set to $LlvmBin"
}

# --- Build --------------------------------------------------------------------
Write-Host "cargo build --release -p quiche --features ffi --target $CargoTarget"
cargo build --release -p quiche --features ffi --target $CargoTarget `
    --manifest-path "$QuicheDir\Cargo.toml"

$BuiltLib = Join-Path $QuicheDir "target\$CargoTarget\release\$Lib"
if (-not (Test-Path $BuiltLib)) {
    throw "FATAL: expected artifact missing: $BuiltLib"
}

# --- Stage ----------------------------------------------------------------------
$OutDir = Join-Path $RepoRoot "native\out\$Rid"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item $BuiltLib $OutDir
Copy-Item (Join-Path $QuicheDir 'COPYING') (Join-Path $OutDir 'LICENSE-quiche.txt')
$BoringLicense = Get-ChildItem -Recurse -Filter 'LICENSE' $QuicheDir -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'boringssl' } | Select-Object -First 1
if ($BoringLicense) {
    Copy-Item $BoringLicense.FullName (Join-Path $OutDir 'LICENSE-boringssl.txt')
} else {
    Write-Warning 'BoringSSL LICENSE not found in checkout; package step must supply it'
}

# --- Symbol smoke check ---------------------------------------------------------
# dumpbin lists exports; the check is skipped (with a warning) when
# dumpbin is unavailable, and the .NET-side load test in step 4 covers it.
$Symbols = @('quiche_config_new', 'quiche_conn_new_with_tls', 'quiche_version', 'quiche_conn_send', 'quiche_conn_recv')
$Dumpbin = Get-Command dumpbin -ErrorAction SilentlyContinue
if ($Dumpbin) {
    $Exports = & dumpbin /exports (Join-Path $OutDir $Lib) 2>$null
    foreach ($Sym in $Symbols) {
        if ($Exports -notmatch [regex]::Escape($Sym)) {
            throw "FATAL: $Lib does not export $Sym"
        }
    }
    Write-Host 'symbol check passed: core C API exports present'
} else {
    Write-Warning 'dumpbin unavailable; skipping symbol check'
}

Get-ChildItem $OutDir | Format-Table Name, Length -AutoSize
Write-Host "staged: $(Join-Path $OutDir $Lib)"
