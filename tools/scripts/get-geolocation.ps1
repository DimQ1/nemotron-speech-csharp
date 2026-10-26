#Requires -Version 5.1
<#
.SYNOPSIS
    Returns the current device location from a script.

.DESCRIPTION
    Two sources are supported:

      Gps  Windows.Devices.Geolocation.Geolocator (real device location).
           Requires the "Geolocation Service" (lfsvc) + Location services ON.
           WinRT projection only exists in Windows PowerShell 5.1, so when the
           script runs on PowerShell 7 it re-launches itself under
           powershell.exe (5.1) for this mode.

      Ip   IP-based lookup (ipapi.co, ipinfo.io fallback) - approximate,
           city-level, no permissions. Works on any PowerShell.

    Auto (default) tries Gps and falls back to Ip.

.PARAMETER Method
    Auto (default), Gps, or Ip.

.PARAMETER Json
    Emit the result as JSON instead of a formatted object.

.PARAMETER TimeoutSeconds
    How long to wait for the GPS fix (default 30). A Windows consent dialog may
    appear on first use for non-packaged apps - click Allow before this expires.

.EXAMPLE
    pwsh -File tools/scripts/get-geolocation.ps1
    pwsh -File tools/scripts/get-geolocation.ps1 -Method Ip -Json
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Gps', 'Ip')]
    [string]$Method = 'Auto',

    [switch]$Json,

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Get-LocationDiagnostics {
    $service = try { (Get-Service -Name lfsvc -ErrorAction Stop).Status.ToString() } catch { 'Unavailable' }

    $readConsent = {
        param([string]$Path)
        try {
            $value = (Get-ItemProperty -Path $Path -Name Value -ErrorAction Stop).Value
            if ($value) { $value } else { 'not set' }
        } catch { 'not set' }
    }

    [pscustomobject]@{
        GeolocationService = $service
        ConsentMachine     = & $readConsent 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location'
        ConsentUser        = & $readConsent 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location'
    }
}

function Wait-AsyncOperation {
    param(
        [Parameter(Mandatory)] $Operation,
        [Parameter(Mandatory)] [Type] $ResultType,
        [Parameter(Mandatory)] [int] $TimeoutSeconds
    )

    if (-not ('System.WindowsRuntimeSystemExtensions' -as [type])) {
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
    }

    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]

    $netTask = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))

    if (-not $netTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        throw "Timed out after $TimeoutSeconds s waiting for a location fix. If a Windows permission dialog appeared, allow access and retry (or raise -TimeoutSeconds)."
    }

    try {
        $netTask.GetAwaiter().GetResult()
    }
    catch {
        # Unwrap the AggregateException/Task wrapper for a readable message.
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        throw $inner.Message
    }
}

function Get-WinRtLocation {
    param([Parameter(Mandatory)] [int] $TimeoutSeconds)

    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [void][Windows.Devices.Geolocation.Geolocator, Windows.Devices.Geolocation, ContentType = WindowsRuntime]
    [void][Windows.Devices.Geolocation.Geoposition, Windows.Devices.Geolocation, ContentType = WindowsRuntime]
    [void][Windows.Devices.Geolocation.GeolocationAccessStatus, Windows.Devices.Geolocation, ContentType = WindowsRuntime]

    $locator = New-Object Windows.Devices.Geolocation.Geolocator
    $locator.DesiredAccuracy = [Windows.Devices.Geolocation.PositionAccuracy]::High

    # The PowerShell WinRT projection cannot call static WinRT members such as
    # Geolocator.RequestAccessAsync(), so the consent state is not queried here.
    # It is reported by Get-LocationDiagnostics from the ConsentStore registry.
    try {
        $position = Wait-AsyncOperation -Operation ($locator.GetGeopositionAsync()) `
            -ResultType ([Windows.Devices.Geolocation.Geoposition]) -TimeoutSeconds $TimeoutSeconds
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match 'access is denied|Access is denied|0x80070005|UnauthorizedAccess') {
            $message = 'Location access was denied. Enable Settings > Privacy & security > Location, then allow PowerShell to use your location.'
        }
        return [pscustomobject]@{
            Source  = 'Gps'
            Success = $false
            Access  = 'Unknown (see Diagnostics.ConsentUser / ConsentMachine)'
            Error   = $message
        }
    }

    if ($null -eq $position -or $null -eq $position.Coordinate) {
        return [pscustomobject]@{
            Source  = 'Gps'
            Success = $false
            Access  = 'Unknown (see Diagnostics.ConsentUser / ConsentMachine)'
            Error   = 'The geolocator returned no coordinate. Location services may be off or the sensor has no fix yet.'
        }
    }

    $coordinate = $position.Coordinate
    $point = $coordinate.Point.Position

    [pscustomobject]@{
        Source         = 'Gps'
        Success        = $true
        Latitude       = [double]$point.Latitude
        Longitude      = [double]$point.Longitude
        AltitudeMeters = [double]$point.Altitude
        AccuracyMeters = [double]$coordinate.Accuracy
        PositionSource = [string]$coordinate.PositionSource
        Timestamp      = $coordinate.Timestamp.ToString('o')
    }
}

function Get-WinRtLocationViaWindowsPowerShell {
    param([Parameter(Mandatory)] [int] $TimeoutSeconds)

    Write-Verbose 'Re-launching the GPS call under Windows PowerShell 5.1 (the host that projects the WinRT geolocation types).'

    $childOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
        -Method Gps -TimeoutSeconds $TimeoutSeconds -Json

    if ($LASTEXITCODE -ne 0) {
        throw (($childOutput | Out-String).Trim())
    }

    return ($childOutput | ConvertFrom-Json).Location
}

function Get-IpLocation {
    # ipapi.co currently answers 403 from some networks, so ipinfo.io is first.
    $sources = @(
        @{ Name = 'ipinfo.io/json'; Uri = 'https://ipinfo.io/json' }
        @{ Name = 'ipapi.co/json';  Uri = 'https://ipapi.co/json/' }
    )

    foreach ($source in $sources) {
        try {
            $raw = Invoke-RestMethod -Uri $source.Uri -TimeoutSec 10
        }
        catch {
            Write-Verbose "$($source.Name) failed: $($_.Exception.Message)"
            continue
        }

        $lat = $null; $lon = $null
        if ($raw.PSObject.Properties['latitude']) { $lat = $raw.latitude; $lon = $raw.longitude }
        elseif ($raw.PSObject.Properties['loc'] -and $raw.loc) {
            $parts = $raw.loc -split ','
            $lat = $parts[0]; $lon = $parts[1]
        }

        if ($lat -is [string] -and -not $lat) { continue }

        return [pscustomobject]@{
            Source    = "Ip ($($source.Name))"
            Success   = $true
            Latitude  = [double]$lat
            Longitude = [double]$lon
            City      = if ($raw.city) { $raw.city } else { $raw.City }
            Region    = if ($raw.region) { $raw.region } else { $raw.Region }
            Country   = if ($raw.country_name) { $raw.country_name } else { $raw.country }
            TimeZone  = if ($raw.timezone) { $raw.timezone } else { $raw.TimeZone }
            Isp       = if ($raw.org) { $raw.org } else { $raw.Isp }
        }
    }

    throw 'All IP geolocation sources failed (no network?).'
}

# --- Device (GPS) attempt, if requested -------------------------------------
$gpsResult = $null
$gpsError = $null

if ($Method -ne 'Ip') {
    try {
        $gpsResult = if ($PSVersionTable.PSEdition -eq 'Core') {
            Get-WinRtLocationViaWindowsPowerShell -TimeoutSeconds $TimeoutSeconds
        }
        else {
            Get-WinRtLocation -TimeoutSeconds $TimeoutSeconds
        }
    }
    catch {
        $gpsError = $_.Exception.Message
    }

    if ($gpsResult -and -not $gpsResult.Success) {
        $gpsError = $gpsResult.Error
        $gpsResult = $null
    }
}

# --- Fall back to IP lookup --------------------------------------------------
$result = $gpsResult

if (-not $result) {
    if ($Method -eq 'Gps') { throw $gpsError }

    if ($gpsError) { Write-Verbose "Device location unavailable ($gpsError) - using IP geolocation." }

    $result = Get-IpLocation
    if ($gpsError) {
        $result | Add-Member -NotePropertyName GpsError -NotePropertyValue $gpsError -Force
    }
}

$payload = [pscustomobject]@{
    Location      = $result
    Diagnostics   = Get-LocationDiagnostics
    CollectedAt   = (Get-Date).ToString('o')
    Host          = $PSVersionTable.PSVersion.ToString()
}

if ($Json) {
    $payload | ConvertTo-Json -Depth 5
}
else {
    $payload | ConvertTo-Json -Depth 5 | ConvertFrom-Json | Format-List
}
