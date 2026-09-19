<#
.SYNOPSIS
    Install / remove / inspect the RamDrive Windows service from a published folder.

.DESCRIPTION
    Why a service instead of a console window: the RAM disk should exist for the whole Windows
    session, independently of any window you happen to have open.

      * It is mounted at boot, before anyone logs in — so %TEMP%, browser caches and app data
        directories that point at the RAM disk already exist when the first user process starts.
      * It survives logoff, locking the screen, closing the console, and Remote Desktop
        disconnects. A console-hosted instance dies with its window and takes the volume (and
        everything on it) with it.
      * No extra console/terminal window sitting in the taskbar.
      * The SCM restarts it if it crashes (5 s / 10 s / 30 s, same as the installer configures).
      * It runs as LocalSystem, so it does not depend on any user being logged in.

    Console mode (`publish-fx\RamDrive.exe` launched by hand) is still useful for a quick test —
    press Ctrl+C to unmount — but it is not the mode to rely on.

    This script has two ways to be used:

    1. Double-clicked from a folder that contains RamDrive.exe (the deployed folder). It elevates
       itself (UAC), shows a one-letter menu and installs the service for THAT folder:

           [I] install (default)   [U] uninstall   [R] restart   [S] status

       That is the "copied the folder to C:\Program Files\RamDrive, now double-click" flow — no
       copying, no arguments.

    2. From the repo, as a command line: `stage` builds the copy-me folder, `install -Source ...
       -Target ...` copies a publish output somewhere and registers it, `status`/`restart`/
       `uninstall` manage the service.

    What it registers is exactly what setup/RamDrive.iss registers, so a script-installed service is
    indistinguishable from an installer-installed one:

      binPath        <folder>\RamDrive.exe   (start = auto, LocalSystem)
      DisplayName    "RamDrive RAM Disk"
      Description    "High-performance RAM disk using WinFsp."
      Recovery       restart after 5 s / 10 s / 30 s, failure counter reset every 60 s
      Group          "FSFilter Activity Monitor"  — starts early in boot
      WinFsp         HKLM\SOFTWARE\WOW6432Node\WinFsp\MountUseMountmgrFromFSD = 1
                     (without it the mount is DefineDosDevice-only: invisible to disk tools)

    Two invariants this script enforces, both learned the hard way:

      * The service binary must NOT live on the drive the service mounts. Windows must read the
        binary before the service starts; the drive only exists after it starts. `install` refuses
        that combination (relevant because a scratch checkout may itself sit on the RAM disk).
      * Files are copied BEFORE the old service is stopped. The source folder may live on the RAM
        disk, and stopping the service unmounts it; copy first or the source vanishes mid-copy.
        Same ordering rationale as "install WinFsp before unmounting" in setup/RamDrive.iss.

.PARAMETER Action
    stage      build the copy-me folder: <publish folder>\RamDrive\
               (payload + appsettings.jsonc + this script + launcher)
    status     (default in a console without arguments: the interactive menu) show service state
    install    register + start the service for the folder (see -Source / -Target / -NoCopy)
    uninstall  stop + remove the service (installed files are left in place)
    restart    stop + start; normally unnecessary — editing appsettings.jsonc reloads in place

.PARAMETER Source
    Folder to install from. On its own it is registered in place; combined with -Target it is the
    copy source. Defaults to this script's own folder when it sits next to RamDrive.exe (the
    double-click flow), otherwise to the repo's publish folder (publish-fx, then publish-aot).

.PARAMETER Target
    Folder the service is installed into, copy mode only. Passing it is what turns "install from
    here" into "copy to there, then install". Default C:\Program Files\RamDrive (same as the
    installer). Ignored when installing in place.

.PARAMETER NoCopy
    Force in-place registration (never copy), even if this script sits in the repo.

.EXAMPLE
    # From the repo, build the copy-me folder, then drag it to C:\Program Files:
    .\scripts\RamDrive-Service.ps1 stage
    #   -> publish-fx\RamDrive\{RamDrive.exe, *.dll, appsettings.jsonc, RamDrive-Service.*}
    #   -> copy that folder to C:\Program Files\  and double-click RamDrive-Service.cmd inside it

.EXAMPLE
    # Or let the script do the copy, from the repo root:
    .\scripts\RamDrive-Service.ps1 install
    .\scripts\RamDrive-Service.ps1 status
    .\scripts\RamDrive-Service.ps1 uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('stage', 'status', 'install', 'uninstall', 'restart')]
    [string]$Action,

    [string]$Source,
    [string]$Target = 'C:\Program Files\RamDrive',
    [switch]$NoCopy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'RamDrive'
$DisplayName = 'RamDrive RAM Disk'
$ServiceDescription = 'High-performance RAM disk using WinFsp.'
$WinFspKey = 'HKLM:\SOFTWARE\WOW6432Node\WinFsp'
$ServiceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

# ── generic helpers ──────────────────────────────────────────────────────────────

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Administrator {
    if (-not (Test-Administrator)) {
        throw ("'{0}' needs administrator rights. Open an elevated PowerShell and re-run:`n" -f $Action) +
              ("  powershell -NoProfile -ExecutionPolicy Bypass -File `"{0}`" {1}" -f $PSCommandPath, $Action)
    }
}

function Restart-Elevated {
    # Double-click support: relaunch this script in an elevated window (UAC) and leave that window
    # open (-NoExit) so the menu, the progress output and the final status stay readable.
    $hostExe = (Get-Process -Id $PID).Path
    if (-not $hostExe) { $hostExe = 'powershell.exe' }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', ('"{0}"' -f $PSCommandPath))
    Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $arguments | Out-Null
}

function Get-ScriptFolderExe {
    $exe = Join-Path $PSScriptRoot 'RamDrive.exe'
    if (Test-Path $exe) { return $exe }
    return $null
}

function Get-LiveInstallFolder {
    $info = Get-ServiceInfo
    if ($info) { return (Split-Path -Parent $info.PathName) }
    return $null
}

function Get-ServiceInfo {
    Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
}

# Reads "MountPoint"/"CapacityMb"/"PageSizeKb" out of appsettings.jsonc without a JSON parser
# (the file is JSONC, so ConvertFrom-Json cannot be used directly).
function Get-ConfigValue([string]$folder, [string]$key) {
    $config = Join-Path $folder 'appsettings.jsonc'
    if (-not (Test-Path $config)) { return $null }
    $match = [regex]::Match((Get-Content $config -Raw), '"' + $key + '"\s*:\s*"?([^",\r\n]+)"?')
    if ($match.Success) { return ($match.Groups[1].Value.Trim() -replace '\\\\', '\') }
    return $null
}

function Assert-PayloadComplete([string]$folder) {
    if (-not (Test-Path (Join-Path $folder 'RamDrive.exe'))) {
        throw "No RamDrive.exe in '$folder'."
    }
    # A framework-dependent build is an apphost plus sibling assemblies; a lone exe is an AOT build.
    if (Test-Path (Join-Path $folder 'RamDrive.dll')) {
        foreach ($required in 'RamDrive.dll', 'RamDrive.deps.json', 'RamDrive.runtimeconfig.json') {
            if (-not (Test-Path (Join-Path $folder $required))) {
                throw "'$folder' looks like a framework-dependent build but '$required' is missing — copy the whole publish folder."
            }
        }
    }
}

# The chicken-and-egg guard: a service cannot live on the drive it mounts.
function Assert-NotSelfHosted([string]$binaryFolder) {
    $mountPoint = Get-ConfigValue $binaryFolder 'MountPoint'
    if (-not $mountPoint) { return }

    $mountDrive = $mountPoint.TrimEnd('\')
    if ($mountDrive.Length -lt 2) { return }

    $binaryRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($binaryFolder))
    if ($binaryRoot.TrimEnd('\') -ieq $mountDrive) {
        throw ("Refusing to host the RamDrive service on $binaryRoot — that is the very drive its own " +
               "appsettings.jsonc mounts ($mountPoint). Windows must read the binary before the service " +
               "starts, and the drive only exists after it starts: neither would ever come up. " +
               "Install to another drive with -Target, e.g. the default C:\Program Files\RamDrive.")
    }
}

# ── service helpers ──────────────────────────────────────────────────────────────

function Wait-ServiceGone([int]$TimeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-ServiceInfo)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (-not (Get-ServiceInfo))
}

function Stop-RamDriveProcess {
    # The WinFsp unmount can be slow and the SCM would wait for it; ask the service to stop, then
    # make sure the process is gone (same as the installer's StopAndDeleteService).
    Get-Process -Name $ServiceName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Remove-ServiceRegistration {
    $info = Get-ServiceInfo
    if (-not $info) { Write-Host "Service '$ServiceName' is not registered."; return }

    if ($info.State -ne 'Stopped') {
        Write-Host "Stopping '$ServiceName' (the mounted drive will disappear)..."
        & sc.exe stop $ServiceName | Out-Null
    }
    Stop-RamDriveProcess

    Write-Host 'Deleting the service registration...'
    & sc.exe delete $ServiceName | Out-Null

    if (-not (Wait-ServiceGone)) {
        throw "Service '$ServiceName' did not disappear within 20 s — a pending delete is in the way. Retry in a moment."
    }
}

function Enable-MountManager {
    if (-not (Test-Path $WinFspKey)) {
        throw "WinFsp registry key '$WinFspKey' not found — install WinFsp 2.x from https://winfsp.dev/rel/ first."
    }
    Set-ItemProperty -Path $WinFspKey -Name MountUseMountmgrFromFSD -Value 1 -Type DWord
}

function New-ServiceRegistration([string]$exePath) {
    Write-Host "Registering '$ServiceName' -> $exePath"
    New-Service -Name $ServiceName -BinaryPathName $exePath -DisplayName $DisplayName -StartupType Automatic | Out-Null

    Set-ItemProperty -Path $ServiceKey -Name Description -Value $ServiceDescription
    # Early-boot group, same as the installer: the RAM disk should be there before user-mode services
    # that expect %TEMP% / cache directories on it.
    Set-ItemProperty -Path $ServiceKey -Name Group -Value 'FSFilter Activity Monitor'
    # Restart 5 s / 10 s / 30 s after a crash; reset the failure counter after 60 s.
    & sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000 | Out-Null
}

function Start-RamDriveService {
    $folder = Get-LiveInstallFolder
    $mountPoint = if ($folder) { Get-ConfigValue $folder 'MountPoint' } else { $null }

    Write-Host "Starting '$ServiceName'..."
    Start-Service -Name $ServiceName

    if ($mountPoint) {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and -not (Test-Path $mountPoint)) { Start-Sleep -Milliseconds 500 }
        if (Test-Path $mountPoint) { Write-Host "Mounted: $mountPoint" }
        else { Write-Warning "The service is running but '$mountPoint' did not appear within 20 s — check the EventLog ('Application', source 'RamDrive')." }
    }
}

function Show-Status {
    $info = Get-ServiceInfo
    if ($info) {
        Write-Host ("Service     : {0}  {1}  ({2} / {3})" -f $info.Name, $info.State, $info.StartMode, $info.StartName)
        Write-Host ("Binary      : {0}" -f $info.PathName)

        $folder = Split-Path -Parent $info.PathName
        $kind = 'AOT single file'
        if (Test-Path (Join-Path $folder 'RamDrive.dll')) { $kind = 'framework-dependent folder' }
        Write-Host ("              ({0})" -f $kind)

        if (Test-Path (Join-Path $folder 'appsettings.jsonc')) {
            Write-Host ("Config      : {0}" -f (Join-Path $folder 'appsettings.jsonc'))
            Write-Host ("              MountPoint {0}  CapacityMb {1}  PageSizeKb {2}" -f `
                (Get-ConfigValue $folder 'MountPoint'), (Get-ConfigValue $folder 'CapacityMb'), (Get-ConfigValue $folder 'PageSizeKb'))
        }
        else {
            Write-Host 'Config      : (no appsettings.jsonc next to the binary — code defaults apply)'
        }
    }
    else {
        Write-Host ("Service     : {0} is NOT registered" -f $ServiceName)
    }

    $winFsp = (Get-ItemProperty -Path $WinFspKey -ErrorAction SilentlyContinue)
    if ($winFsp) {
        Write-Host ("WinFsp      : {0}   MountUseMountmgrFromFSD = {1}" -f $winFsp.InstallDir, $winFsp.MountUseMountmgrFromFSD)
    }
    else {
        Write-Host 'WinFsp      : not installed (https://winfsp.dev/rel/)'
    }

    $process = Get-Process -Name $ServiceName -ErrorAction SilentlyContinue
    Write-Host ("Process     : {0}" -f ($(if ($process) { "PID $($process.Id)" } else { 'not running' })))
    Write-Host ("This folder : {0}" -f $PSScriptRoot)

    if ($info) {
        # Report the same hazard the install path refuses, so a hand-edited config cannot silently
        # produce a service that stops mounting at the next reboot.
        try { Assert-NotSelfHosted (Split-Path -Parent $info.PathName) }
        catch { Write-Warning $_.Exception.Message }
    }
}

# ── actions ──────────────────────────────────────────────────────────────────────

function Invoke-Stage {
    # Build the folder you copy to C:\Program Files: payload + config + this script + launcher.
    if (Get-ScriptFolderExe) {
        throw "stage needs the repo checkout (scripts\ + a publish folder); this copy runs from a deploy folder."
    }

    $publish = $null
    foreach ($candidate in 'publish-fx', 'publish-aot') {
        $path = Join-Path (Split-Path -Parent $PSScriptRoot) $candidate
        if (Test-Path (Join-Path $path 'RamDrive.exe')) { $publish = $path; break }
    }
    if (-not $publish) {
        throw ("No publish folder next to this script's parent (looked for publish-fx and publish-aot). " +
               "Publish first:`n  dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 " +
               "-p:PublishAot=false --self-contained false -o ./publish-fx")
    }

    $stage = Join-Path $publish $ServiceName
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null

    Get-ChildItem -Path $publish -File | Where-Object { $_.Name -notin @('RamDrive-Service.ps1', 'RamDrive-Service.cmd') } |
        Copy-Item -Destination $stage -Force

    # Configuration: keep what the running installation uses, so this copy is a drop-in replacement
    # (same MountPoint / CapacityMb / InitialDirectories). Falls back to the published template.
    $liveFolder = Get-LiveInstallFolder
    $configNote = 'published template'
    if ($liveFolder -and (Test-Path (Join-Path $liveFolder 'appsettings.jsonc'))) {
        Copy-Item (Join-Path $liveFolder 'appsettings.jsonc') (Join-Path $stage 'appsettings.jsonc') -Force
        $configNote = "inherited from the current installation at $liveFolder"
    }

    Copy-Item (Join-Path $PSScriptRoot 'RamDrive-Service.ps1') $stage -Force
    if (Test-Path (Join-Path $PSScriptRoot 'RamDrive-Service.cmd')) {
        Copy-Item (Join-Path $PSScriptRoot 'RamDrive-Service.cmd') $stage -Force
    }

    $mountPoint = Get-ConfigValue $stage 'MountPoint'
    $capacity = Get-ConfigValue $stage 'CapacityMb'
    Write-Host ''
    Write-Host "Staged: $stage" -ForegroundColor Green
    Write-Host ("  {0} files, appsettings.jsonc {1} (MountPoint {2}, CapacityMb {3})" -f `
        (Get-ChildItem $stage -File).Count, $configNote, $mountPoint, $capacity)
    Write-Host ''
    Write-Host 'To deploy:'
    Write-Host '  1. uninstall / stop the current RamDrive installation (the old exe is locked while it runs)'
    Write-Host ("  2. copy '{0}' to C:\Program Files\  ->  C:\Program Files\{1}\" -f $stage, $ServiceName)
    Write-Host '  3. double-click RamDrive-Service.cmd in the copied folder and press Enter (install)'
}

function Invoke-Install {
    Assert-Administrator

    # Copy mode only when -Target is given explicitly; everything else registers a folder in place.
    $inPlace = $null
    if ($NoCopy -or -not $PSBoundParameters.ContainsKey('Target')) {
        if ($Source) { $inPlace = $Source }
        elseif (Get-ScriptFolderExe) { $inPlace = $PSScriptRoot }   # deployed folder: double-click flow
        elseif ($NoCopy) { $inPlace = $PSScriptRoot }
    }

    if ($inPlace) {
        $sourceFolder = [System.IO.Path]::GetFullPath($inPlace)
        $installFolder = $sourceFolder
    }
    else {
        # Copy mode: source is -Source or the repo's publish folder, destination is -Target.
        $sourceFolder = if ($Source) { [System.IO.Path]::GetFullPath($Source) } else {
            $publish = $null
            foreach ($candidate in 'publish-fx', 'publish-aot') {
                $path = Join-Path (Split-Path -Parent $PSScriptRoot) $candidate
                if (Test-Path (Join-Path $path 'RamDrive.exe')) { $publish = $path; break }
            }
            if (-not $publish) {
                throw "No publish folder found. Publish first, or pass -Source <folder> (or double-click me from the deployed folder)."
            }
            $publish
        }
        $installFolder = [System.IO.Path]::GetFullPath($Target)
    }

    # Validate everything before the first side effect.
    Assert-PayloadComplete $sourceFolder
    Assert-NotSelfHosted $installFolder

    if ($installFolder -ne $sourceFolder) {
        Write-Host "Installing $sourceFolder -> $installFolder"
        New-Item -ItemType Directory -Force -Path $installFolder | Out-Null
        Copy-Item -Path (Join-Path $sourceFolder '*') -Destination $installFolder -Recurse -Force
        Assert-PayloadComplete $installFolder
    }
    else {
        Write-Host "Installing in place: $installFolder"
    }

    Enable-MountManager
    Remove-ServiceRegistration
    New-ServiceRegistration (Join-Path $installFolder 'RamDrive.exe')
    Start-RamDriveService

    Write-Host ''
    Write-Host "Done. Drive letter and capacity come from $(Join-Path $installFolder 'appsettings.jsonc');" -ForegroundColor Green
    Write-Host 'editing that file while the service runs reloads the volume in place.'
}

function Invoke-Uninstall {
    Assert-Administrator

    $folder = Get-LiveInstallFolder
    $mountPoint = if ($folder) { Get-ConfigValue $folder 'MountPoint' } else { $null }

    if ($mountPoint) {
        $answer = Read-Host "Remove the '$ServiceName' service? $mountPoint is unmounted and everything on it is lost [y/N]"
        if ($answer -notmatch '^[Yy]') { Write-Host 'Cancelled.'; return }
    }

    Remove-ServiceRegistration
    Write-Host ''
    Write-Host 'Service removed. The installed files were left in place.' -ForegroundColor Green
    if ($folder) { Write-Host "Delete '$folder' manually if you no longer need them." }
}

function Invoke-Restart {
    Assert-Administrator
    if (-not (Get-ServiceInfo)) { throw "Service '$ServiceName' is not registered — run 'install' first." }

    Write-Host "Restarting '$ServiceName'..."
    & sc.exe stop $ServiceName | Out-Null
    Stop-RamDriveProcess
    Start-Sleep -Milliseconds 500
    Start-RamDriveService
}

function Read-MenuAction {
    Write-Host ''
    Write-Host 'RamDrive service setup' -ForegroundColor Cyan
    Write-Host ("  folder: {0}" -f $PSScriptRoot)
    Write-Host ''
    Write-Host '  [I] install   register + start the service for this folder (default)'
    Write-Host '  [U] uninstall stop + remove the service'
    Write-Host '  [R] restart   stop + start the service'
    Write-Host '  [S] status    show what is registered'
    Write-Host ''

    while ($true) {
        [string]$key = Read-Host 'Choose (I/U/R/S)'
        switch ($key.Trim().ToUpperInvariant()) {
            ''  { return 'install' }
            'I' { return 'install' }
            'U' { return 'uninstall' }
            'R' { return 'restart' }
            'S' { return 'status' }
            default { Write-Host 'Type I, U, R or S.' }
        }
    }
}

# ── entry point ──────────────────────────────────────────────────────────────────

$interactive = -not $Action
if ($interactive) {
    # Double-click flow: elevate first, then ask, so the menu, the output and the result all live in
    # the elevated window (which stays open).
    if (-not (Test-Administrator)) {
        Restart-Elevated
        exit 0
    }
    $Action = Read-MenuAction
}

switch ($Action) {
    'stage'     { Invoke-Stage }
    'status'    { Show-Status }
    'install'   { Invoke-Install }
    'uninstall' { Invoke-Uninstall }
    'restart'   { Invoke-Restart }
}

if ($interactive) { Write-Host '' }
