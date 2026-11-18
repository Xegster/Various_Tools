<#
.SYNOPSIS
    Check and restore Magisk root on a Samsung phone whose root "disappeared".

.DESCRIPTION
    Many Samsung phones (S10 family and others without a ramdisk in boot) keep Magisk in the
    RECOVERY partition. Root only exists when the phone boots through recovery; a normal boot
    looks like lost root even though the Magisk app is still installed.

    Modes:
      Check         Report phone state only. Changes nothing.
      RecoveryBoot  Reboot to recovery over ADB (Magisk loads, then boots Android) and verify root.
                    Cheap, no flashing. Try this first.
      Flash         Reflash the Magisk-patched AP with Odin (AP slot only), then do a RecoveryBoot.
      Auto          Check -> RecoveryBoot -> offer Flash if still not rooted. (default)

    Odin is a GUI tool and cannot be driven headless, so the Flash mode prepares everything
    (download mode, AP path on the clipboard, Odin launched, exact settings listed) and waits
    for you to press Start and report PASS.

    The patched AP is auto-selected from -FirmwareRoot by matching the phone's build id
    (ro.build.version.incremental, e.g. G970FXXSEFUL1). A patched AP built from a different
    firmware is refused, because flashing the wrong firmware can bootloop the phone.

.EXAMPLE
    .\Restore-SamsungRoot.ps1                 # Auto mode on the only connected phone
.EXAMPLE
    .\Restore-SamsungRoot.ps1 -Mode Check
.EXAMPLE
    .\Restore-SamsungRoot.ps1 -Mode Flash -Serial RF8MC1N8B3N
.EXAMPLE
    .\Restore-SamsungRoot.ps1 -Mode Flash -PatchedAp 'D:\ap\magisk_patched-30600_abc.tar' -Force
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Check', 'RecoveryBoot', 'Flash')]
    [string]$Mode = 'Auto',

    # adb serial; required only when more than one phone is connected.
    [string]$Serial,

    # Explicit patched AP .tar; skips auto-discovery.
    [string]$PatchedAp,

    [string]$FirmwareRoot = 'C:\Xegster\Android Rooting',
    [string]$OdinPath = 'C:\Xegster\Android Rooting\Odin3_v3.14.4\Odin3_v3.14.4.exe',
    [string]$Adb,

    [int]$BootTimeoutSec = 240,

    # Skip y/N confirmations (the flash still needs you to press Start in Odin).
    [switch]$Yes,

    # Allow a -PatchedAp whose path does not contain the phone's build id.
    [switch]$Force
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- helpers

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "   OK  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "   !!  $Text" -ForegroundColor Yellow }

function Confirm-Action([string]$Question) {
    if ($Yes) { return $true }
    $answer = Read-Host "$Question [y/N]"
    return ($answer -match '^(y|yes)$')
}

function Find-Adb {
    if ($Adb) {
        if (-not (Test-Path -LiteralPath $Adb)) { throw "adb not found at -Adb path: $Adb" }
        return $Adb
    }
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
    if (Test-Path -LiteralPath $sdk) { return $sdk }
    throw 'adb not found. Put it on PATH or pass -Adb <path to adb.exe>.'
}

# Runs adb against the selected device. Returns trimmed text output.
function Invoke-Adb {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$NoSerial)
    $full = @()
    if ($script:DeviceSerial -and -not $NoSerial) { $full += @('-s', $script:DeviceSerial) }
    $full += $Arguments
    # adb writes "device not found" etc. to stderr while the phone reboots; that must not be fatal.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $script:AdbPath @full 2>&1 | Out-String }
    finally { $ErrorActionPreference = $prev }
    # Don't let a failed 'su' probe leak adb's exit code out as the script's exit code.
    $global:LASTEXITCODE = 0
    return $out.Trim()
}

function Get-Prop([string]$Name) { return (Invoke-Adb @('shell', 'getprop', $Name)) }

function Select-Device {
    $raw = Invoke-Adb -NoSerial @('devices')
    $rows = @()
    foreach ($line in ($raw -split "`r?`n")) {
        if ($line -match '^(\S+)\s+(device|unauthorized|offline)\b') {
            $rows += [pscustomobject]@{ Serial = $Matches[1]; State = $Matches[2] }
        }
    }
    if ($Serial) { $rows = @($rows | Where-Object { $_.Serial -eq $Serial }) }
    if ($rows.Count -eq 0) {
        throw 'No phone found. Check the cable, USB mode (File transfer) and that USB debugging is on.'
    }
    if ($rows.Count -gt 1) {
        $list = ($rows | ForEach-Object { "$($_.Serial) ($($_.State))" }) -join ', '
        throw "More than one phone connected: $list. Re-run with -Serial <serial>."
    }
    $d = $rows[0]
    if ($d.State -eq 'unauthorized') {
        throw "Phone $($d.Serial) is unauthorized. Unlock it, accept 'Allow USB debugging' (tick Always allow), then re-run."
    }
    if ($d.State -eq 'offline') {
        throw "Phone $($d.Serial) is offline. Replug the cable or run 'adb kill-server' and retry."
    }
    return $d.Serial
}

# True when su works and reports uid=0. May show a Magisk grant prompt on the phone.
function Test-Root {
    $out = Invoke-Adb @('shell', 'su -c id 2>&1')
    return ($out -match 'uid=0')
}

function Get-DeviceInfo {
    $pkg = Invoke-Adb @('shell', 'pm list packages com.topjohnwu.magisk')
    [pscustomobject]@{
        Model         = Get-Prop 'ro.product.model'
        Android       = Get-Prop 'ro.build.version.release'
        Build         = Get-Prop 'ro.build.version.incremental'
        Patch         = Get-Prop 'ro.build.version.security_patch'
        BlLocked      = Get-Prop 'ro.boot.flash.locked'
        VerifiedBoot  = Get-Prop 'ro.boot.verifiedbootstate'
        OemUnlockOk   = Get-Prop 'sys.oem_unlock_allowed'
        MagiskApp     = ($pkg -match 'com\.topjohnwu\.magisk')
        Root          = $false
    }
}

function Show-DeviceInfo($Info) {
    Write-Host ("   Model          : {0}" -f $Info.Model)
    Write-Host ("   Android        : {0} (patch {1})" -f $Info.Android, $Info.Patch)
    Write-Host ("   Build id       : {0}" -f $Info.Build)
    $lockText = if ($Info.BlLocked -eq '0') { 'UNLOCKED' } elseif ($Info.BlLocked -eq '1') { 'LOCKED' } else { 'unknown' }
    Write-Host ("   Bootloader     : {0} (verified boot: {1})" -f $lockText, $Info.VerifiedBoot)
    Write-Host ("   Magisk app     : {0}" -f $(if ($Info.MagiskApp) { 'installed' } else { 'NOT installed' }))
    Write-Host ("   Root (su)      : {0}" -f $(if ($Info.Root) { 'YES' } else { 'no' }))
}

# Poll until adb reports the phone as 'device' and Android finished booting.
function Wait-DeviceBooted([int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    Write-Host '   waiting for the phone to boot' -NoNewline
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        Write-Host '.' -NoNewline
        $state = Invoke-Adb @('get-state')
        if ($state -eq 'device') {
            $boot = Get-Prop 'sys.boot_completed'
            if ($boot -eq '1') { Write-Host ''; Start-Sleep -Seconds 5; return }
        }
    }
    Write-Host ''
    throw "Phone did not finish booting within $TimeoutSec s."
}

# Reboot through recovery so the Magisk-patched recovery runs, then check root.
function Invoke-RecoveryBoot {
    Write-Step 'Booting through recovery (loads Magisk)'
    [void](Invoke-Adb @('reboot', 'recovery'))
    Start-Sleep -Seconds 15
    Wait-DeviceBooted $BootTimeoutSec
    Write-Warn 'If the phone shows a Magisk "Superuser request" for Shell, tap Grant.'
    $ok = Test-Root
    if ($ok) { Write-Ok 'Root is active (uid=0).' } else { Write-Warn 'su still not available after recovery boot.' }
    return $ok
}

# Finds a Magisk-patched AP whose firmware folder matches the phone's build id.
function Find-PatchedAp([string]$Build, [string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { throw "FirmwareRoot not found: $Root" }
    $rootTrim = $Root.TrimEnd('\')
    $cands = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter 'magisk_patched*.tar' -File -ErrorAction SilentlyContinue)
    $hits = @()
    foreach ($c in $cands) {
        $dir = $c.Directory
        for ($i = 0; $i -lt 4 -and $dir -and $dir.FullName.Length -gt $rootTrim.Length; $i++) {
            $named = $dir.FullName.Contains($Build)
            $sub = $null
            if (-not $named) {
                $sub = Get-ChildItem -LiteralPath $dir.FullName -Directory -Recurse -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name.Contains($Build) } | Select-Object -First 1
            }
            if ($named -or $sub) { $hits += $c; break }
            $dir = $dir.Parent
        }
    }
    # Prefer anything outside an "Old" folder, newest first.
    $preferred = @($hits | Where-Object { $_.FullName -notmatch '\\Old\\' })
    if ($preferred.Count -gt 0) { $hits = $preferred }
    return @($hits | Sort-Object LastWriteTime -Descending)
}

function Invoke-Flash($Info) {
    Write-Step 'Flash preflight'
    if ($Info.BlLocked -ne '0') {
        throw 'Bootloader is not unlocked. Flashing a patched AP will be rejected; stopping.'
    }
    Write-Ok 'Bootloader is unlocked.'

    if (-not (Test-Path -LiteralPath $OdinPath)) { throw "Odin not found at $OdinPath (use -OdinPath)." }

    $ap = $null
    if ($PatchedAp) {
        if (-not (Test-Path -LiteralPath $PatchedAp)) { throw "-PatchedAp not found: $PatchedAp" }
        $ap = Get-Item -LiteralPath $PatchedAp
        if (-not $ap.FullName.Contains($Info.Build) -and -not $Force) {
            throw "Path of -PatchedAp does not contain this phone's build id ($($Info.Build)). If you are sure it matches, add -Force."
        }
    }
    else {
        $found = Find-PatchedAp -Build $Info.Build -Root $FirmwareRoot
        if ($found.Count -eq 0) {
            throw "No magisk_patched*.tar found for build $($Info.Build) under $FirmwareRoot.`nPatch the stock AP for this build in the Magisk app first, copy it to the PC, and pass -PatchedAp."
        }
        Write-Host '   Matching patched AP files (newest first):'
        $found | ForEach-Object { Write-Host ("     {0}  {1:N0} MB  {2}" -f $_.LastWriteTime.ToString('yyyy-MM-dd'), ($_.Length / 1MB), $_.FullName) }
        $ap = $found[0]
    }
    Write-Ok "Using: $($ap.FullName)"

    Write-Host ''
    Write-Host '   About to: reboot the phone to Download Mode, then you flash the AP slot ONLY in Odin.'
    Write-Host '   BL, CP and CSC stay empty (AP-only does not wipe data). Back up anything irreplaceable first.'
    if (-not (Confirm-Action 'Continue?')) { throw 'Cancelled.' }

    Write-Step 'Download Mode and Odin'
    try { Set-Clipboard -Value $ap.FullName; Write-Ok 'AP path copied to the clipboard (paste into the AP file dialog).' } catch { Write-Warn 'Could not set the clipboard.' }
    [void](Invoke-Adb @('reboot', 'download'))
    Write-Host '   Phone is going to Download Mode. If a warning screen appears, press Volume Up to continue.'
    Start-Sleep -Seconds 6
    Start-Process -FilePath $OdinPath -WorkingDirectory (Split-Path -Parent $OdinPath)
    Write-Host ''
    Write-Host '   In Odin:' -ForegroundColor White
    Write-Host '     1. Wait for the blue <ID:0/00x> box and "Added!!" (click OK on the FRP warning; no need to remove accounts).'
    Write-Host '     2. Click AP and choose the file above. Leave BL, CP and CSC EMPTY.'
    Write-Host '     3. Options: UNTICK Auto Reboot. Keep F. Reset Time ticked. Re-Partition UNTICKED.'
    Write-Host '     4. Click Start and wait for the green PASS!.'
    Write-Host '        ("succeed 1 / failed 1" with a ghost port is harmless as long as one port says PASS.)'
    Write-Host ''
    [void](Read-Host 'Press Enter once Odin shows PASS')

    Write-Host ''
    Write-Host '   Now restart the phone: hold Volume Down + Power until the screen goes black.'
    Write-Host '   (A normal boot is fine; the recovery boot over ADB comes next.)'
    Wait-DeviceBooted $BootTimeoutSec
}

# ---------------------------------------------------------------- main

$script:AdbPath = Find-Adb
$script:DeviceSerial = $null
[void](Invoke-Adb -NoSerial @('start-server'))

Write-Step 'Finding the phone'
$script:DeviceSerial = Select-Device
Write-Ok "Connected: $($script:DeviceSerial)"

Write-Step 'Phone state'
$info = Get-DeviceInfo
$info.Root = Test-Root
Show-DeviceInfo $info

if ($info.Root) {
    Write-Ok 'Already rooted. Nothing to do.'
    return
}
if ($Mode -eq 'Check') { return }

if (-not $info.MagiskApp) {
    Write-Warn 'Magisk app is not installed. Install Magisk 30.x from the Magisk GitHub releases before expecting root.'
}

if ($Mode -in @('Auto', 'RecoveryBoot')) {
    if (-not (Confirm-Action 'Reboot the phone through recovery to load Magisk?')) { throw 'Cancelled.' }
    if (Invoke-RecoveryBoot) { return }
    if ($Mode -eq 'RecoveryBoot') { return }
    Write-Warn 'Recovery boot did not give root; the recovery partition is probably stock again, so a reflash is needed.'
}

if ($Mode -in @('Auto', 'Flash')) {
    Invoke-Flash $info
    $script:DeviceSerial = Select-Device
    if (Invoke-RecoveryBoot) {
        Write-Host ''
        Write-Warn 'Keep root: avoid plain restarts. Reboot from Magisk, or run this script with -Mode RecoveryBoot.'
        return
    }
    throw 'Flashed, but root is not active. Check that the patched AP matches the firmware build and that Odin said PASS.'
}
