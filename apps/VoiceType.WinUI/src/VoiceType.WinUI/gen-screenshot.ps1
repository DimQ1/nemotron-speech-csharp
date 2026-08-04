Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(1366, 768)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'HighQuality'
$g.Clear([System.Drawing.Color]::FromArgb(28, 28, 28))

$titleFont = New-Object System.Drawing.Font("Segoe UI", 26, [System.Drawing.FontStyle]::Bold)
$subFont = New-Object System.Drawing.Font("Segoe UI", 14)
$bodyFont = New-Object System.Drawing.Font("Segoe UI", 12)
$blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::DodgerBlue)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$gray = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160,160,160))
$darkGray = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80,80,80))

$g.DrawString("VoiceType", $titleFont, $blue, 30, 20)
$g.DrawString("AI Dictation  •  On-device  •  Offline  •  17 Languages", $subFont, $gray, 32, 60)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::DodgerBlue, 2)
$g.DrawLine($pen, 30, 90, 450, 90)

$g.DrawString("STATUS: Ready", $bodyFont, $blue, 32, 110)
$g.DrawString("MODEL: Nemotron ASR (local)", $bodyFont, $darkGray, 32, 138)
$g.DrawString("HOTKEY: Ctrl+Shift+V", $bodyFont, $darkGray, 32, 166)

# Output area border
$borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,60,60), 1)
$g.DrawRectangle($borderPen, 32, 210, 1300, 440)

# Simulated text
$recogFont = New-Object System.Drawing.Font("Consolas", 14)
$g.DrawString("This is a real-time speech recognition demo.", $recogFont, $white, 50, 230)
$g.DrawString("VoiceType types what you say into any application.", $recogFont, $gray, 50, 258)
$g.DrawString("", $recogFont, $white, 50, 280)
$g.DrawString("> Start speaking...", $recogFont, $darkGray, 50, 310)

# Bottom bar
$bottomRect = New-Object System.Drawing.Rectangle(32, 670, 1300, 50)
$bottomBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40,40,40))
$g.FillRectangle($bottomBrush, $bottomRect)
$g.DrawString("[Ctrl+Shift+V] Start/Stop    [Settings]    [Download Model]    Model: CPU", $bodyFont, $gray, 45, 682)

$outDir = "e:\Work\Dimq1\Audio\nemotron-speech-csharp\VoiceType.WinUI\Assets\StoreListing"
New-Item -Force -ItemType Directory $outDir | Out-Null
$path = Join-Path $outDir "screenshot-01.png"
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $blue.Dispose(); $white.Dispose(); $gray.Dispose(); $darkGray.Dispose(); $borderPen.Dispose(); $pen.Dispose(); $bottomBrush.Dispose()
Write-Host "OK: $path ($([math]::Round((Get-Item $path).Length/1KB,0)) KB)"
