Add-Type -AssemblyName System.Drawing

$src = "e:\Work\Dimq1\Audio\nemotron-speech-csharp\VoiceType.WinUI\Assets\app-icon.png"
$ico = "e:\Work\Dimq1\Audio\nemotron-speech-csharp\VoiceType.WinUI\Assets\AppIcon.ico"
$assetsDir = "e:\Work\Dimq1\Audio\nemotron-speech-csharp\VoiceType.WinUI\Assets"

$srcBmp = [System.Drawing.Bitmap]::FromFile($src)

# Sizes needed for ICO: 256, 48, 32, 16 (required for Start Menu + taskbar)
$sizes = @(256, 128, 96, 64, 48, 32, 24, 16)

# Build ICO file manually
$mem = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($mem)

# ICO header
$writer.Write([Int16]0)   # reserved
$writer.Write([Int16]1)   # ICO type
$writer.Write([Int16]$sizes.Length)  # image count

$pngs = @()
$offset = 6 + 16 * $sizes.Length

foreach ($s in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($srcBmp, $s, $s)
    $pngStream = New-Object System.IO.MemoryStream
    $resized.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $pngStream.ToArray()
    $pngs += $pngBytes

    $w = if ($s -ge 256) { 0 } else { $s }
    $h = if ($s -ge 256) { 0 } else { $s }

    # ICO entry
    $writer.Write([Byte]$w)
    $writer.Write([Byte]$h)
    $writer.Write([Byte]0)       # color palette
    $writer.Write([Byte]0)       # reserved
    $writer.Write([Int16]1)      # color planes
    $writer.Write([Int16]32)     # bits per pixel
    $writer.Write([Int32]$pngBytes.Length)   # size
    $writer.Write([Int32]$offset)

    $offset += $pngBytes.Length
    $resized.Dispose()
    $pngStream.Dispose()
}

# Write PNG data
foreach ($png in $pngs) {
    $writer.Write($png)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($ico, $mem.ToArray())
$writer.Close()
$mem.Close()
$srcBmp.Dispose()

Write-Host "ICO: $([math]::Round((Get-Item $ico).Length/1KB,0)) KB with $($sizes.Length) sizes ($($sizes -join ', '))"

# Also generate missing PNG sizes for Store
$pngSizes = @{
    "Square44x44Logo.png" = 44
    "Square44x44Logo.scale-200.png" = 88
    "Square150x150Logo.png" = 150
    "Square150x150Logo.scale-200.png" = 300
    "Wide310x150Logo.png" = "310x150"
    "Wide310x150Logo.scale-200.png" = "620x300"
    "StoreLogo.png" = 50
    "SplashScreen.png" = "620x300"
    "SplashScreen.scale-200.png" = "1240x600"
    "LockScreenLogo.scale-200.png" = "48x48"
}

$srcBmp2 = [System.Drawing.Bitmap]::FromFile($src)
foreach ($entry in $pngSizes.GetEnumerator()) {
    $dest = Join-Path $assetsDir $entry.Key
    $sizeStr = $entry.Value.ToString()
    if ($sizeStr -match '^(\d+)x(\d+)$') {
        $w = [int]$Matches[1]; $h = [int]$Matches[2]
    } else {
        $w = [int]$sizeStr; $h = [int]$sizeStr
    }
    $resized = New-Object System.Drawing.Bitmap($srcBmp2, $w, $h)
    $resized.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $resized.Dispose()
    Write-Host "  $($entry.Key): ${w}x${h}"
}
$srcBmp2.Dispose()
Write-Host "All assets regenerated from app-icon.png (1254x1254)"
