$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "dist\capturas"
$iconPath = Join-Path $root "src\O2CloudDrive\Assets\O2CloudDrive.ico"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function ColorRgb([int]$r, [int]$g, [int]$b) {
    [System.Drawing.Color]::FromArgb($r, $g, $b)
}

function New-Font([single]$size, [System.Drawing.FontStyle]$style = [System.Drawing.FontStyle]::Regular) {
    New-Object System.Drawing.Font("Segoe UI", $size, $style, [System.Drawing.GraphicsUnit]::Point)
}

function New-SolidBrush([System.Drawing.Color]$color) {
    New-Object System.Drawing.SolidBrush($color)
}

function New-Pen([System.Drawing.Color]$color, [single]$width = 1) {
    New-Object System.Drawing.Pen($color, $width)
}

function New-RoundPath([System.Drawing.RectangleF]$rect, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-RoundRect($g, [System.Drawing.RectangleF]$rect, [single]$radius, [System.Drawing.Color]$color) {
    $brush = New-SolidBrush $color
    $path = New-RoundPath $rect $radius
    $g.FillPath($brush, $path)
    $path.Dispose()
    $brush.Dispose()
}

function Stroke-RoundRect($g, [System.Drawing.RectangleF]$rect, [single]$radius, [System.Drawing.Color]$color, [single]$width = 1) {
    $pen = New-Pen $color $width
    $path = New-RoundPath $rect $radius
    $g.DrawPath($pen, $path)
    $path.Dispose()
    $pen.Dispose()
}

function Draw-Text($g, [string]$text, $font, [System.Drawing.Color]$color, [System.Drawing.RectangleF]$rect, [string]$align = "Near", [string]$line = "Center") {
    $brush = New-SolidBrush $color
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::$align
    $format.LineAlignment = [System.Drawing.StringAlignment]::$line
    $format.Trimming = [System.Drawing.StringTrimming]::EllipsisCharacter
    $g.DrawString($text, $font, $brush, $rect, $format)
    $format.Dispose()
    $brush.Dispose()
}

function Draw-Line($g, [int]$x1, [int]$y1, [int]$x2, [int]$y2, [System.Drawing.Color]$color, [single]$width = 1) {
    $pen = New-Pen $color $width
    $g.DrawLine($pen, $x1, $y1, $x2, $y2)
    $pen.Dispose()
}

function Draw-Button($g, [System.Drawing.RectangleF]$rect, [string]$text, [System.Drawing.Color]$back, [System.Drawing.Color]$fore, [System.Drawing.Color]$border) {
    Fill-RoundRect $g $rect 5 $back
    Stroke-RoundRect $g $rect 5 $border 1
    Draw-Text $g $text (New-Font 10 ([System.Drawing.FontStyle]::Bold)) $fore $rect "Center" "Center"
}

function Draw-Input($g, [System.Drawing.RectangleF]$rect, [string]$text) {
    $white = ColorRgb 255 255 255
    $border = ColorRgb 177 190 202
    Fill-RoundRect $g $rect 3 $white
    Stroke-RoundRect $g $rect 3 $border 1
    Draw-Text $g $text (New-Font 10) (ColorRgb 35 48 61) ([System.Drawing.RectangleF]::new($rect.X + 12, $rect.Y + 1, $rect.Width - 20, $rect.Height - 2))
}

function Draw-AppIcon($g, [int]$x, [int]$y, [int]$size) {
    Fill-RoundRect $g ([System.Drawing.RectangleF]::new($x, $y, $size, $size)) ([Math]::Max(5, $size / 5)) (ColorRgb 0 126 140)
    $pen = New-Pen (ColorRgb 255 255 255) ([Math]::Max(2, $size / 12))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sx = [single]$size
    $path.AddBezier([single]($x + 0.20 * $sx), [single]($y + 0.64 * $sx), [single]($x + 0.20 * $sx), [single]($y + 0.52 * $sx), [single]($x + 0.31 * $sx), [single]($y + 0.45 * $sx), [single]($x + 0.43 * $sx), [single]($y + 0.49 * $sx))
    $path.AddBezier([single]($x + 0.45 * $sx), [single]($y + 0.33 * $sx), [single]($x + 0.67 * $sx), [single]($y + 0.32 * $sx), [single]($x + 0.70 * $sx), [single]($y + 0.52 * $sx), [single]($x + 0.70 * $sx), [single]($y + 0.52 * $sx))
    $path.AddBezier([single]($x + 0.83 * $sx), [single]($y + 0.52 * $sx), [single]($x + 0.87 * $sx), [single]($y + 0.72 * $sx), [single]($x + 0.72 * $sx), [single]($y + 0.72 * $sx), [single]($x + 0.72 * $sx), [single]($y + 0.72 * $sx))
    $path.AddLine([single]($x + 0.30 * $sx), [single]($y + 0.72 * $sx), [single]($x + 0.72 * $sx), [single]($y + 0.72 * $sx))
    $g.DrawPath($pen, $path)
    $path.Dispose()
    $pen.Dispose()
}

function New-Canvas([string]$path, [scriptblock]$draw) {
    $bitmap = New-Object System.Drawing.Bitmap(1280, 720)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear((ColorRgb 230 236 242))

    & $draw $g

    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bitmap.Dispose()
}

function Draw-Window($g, [int]$x, [int]$y, [int]$w, [int]$h, [string]$title) {
    Fill-RoundRect $g ([System.Drawing.RectangleF]::new($x + 6, $y + 8, $w, $h)) 18 ([System.Drawing.Color]::FromArgb(46, 25, 34, 44))
    Fill-RoundRect $g ([System.Drawing.RectangleF]::new($x, $y, $w, $h)) 18 (ColorRgb 247 250 252)
    Fill-RoundRect $g ([System.Drawing.RectangleF]::new($x, $y, $w, 50)) 18 (ColorRgb 250 251 252)
    $titleBarCover = New-SolidBrush (ColorRgb 250 251 252)
    $g.FillRectangle($titleBarCover, $x, $y + 28, $w, 24)
    $titleBarCover.Dispose()
    Stroke-RoundRect $g ([System.Drawing.RectangleF]::new($x, $y, $w, $h)) 18 (ColorRgb 204 214 224) 1
    Draw-AppIcon $g ($x + 18) ($y + 12) 26
    Draw-Text $g $title (New-Font 11 ([System.Drawing.FontStyle]::Regular)) (ColorRgb 34 44 55) ([System.Drawing.RectangleF]::new($x + 52, $y + 4, $w - 100, 42))
    Draw-Text $g "X" (New-Font 14) (ColorRgb 72 82 94) ([System.Drawing.RectangleF]::new($x + $w - 54, $y + 4, 34, 40)) "Center" "Center"
    Draw-Line $g $x ($y + 50) ($x + $w) ($y + 50) (ColorRgb 229 235 241)
}

function Draw-InstallerScreenshot {
    New-Canvas (Join-Path $outDir "01-instalador.png") {
        param($g)
        Draw-Window $g 240 160 800 380 "O2 Cloud Drive 0.8.1 beta"
        Draw-AppIcon $g 282 232 44
        Draw-Text $g "Instalar O2 Cloud Drive" (New-Font 20 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 17 31 44) ([System.Drawing.RectangleF]::new(340, 226, 620, 46))
        Draw-Text $g "Carpeta de instalacion" (New-Font 10) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(282, 310, 670, 24))
        Draw-Input $g ([System.Drawing.RectangleF]::new(282, 342, 520, 38)) "C:\Program Files\O2 Cloud Drive"
        Draw-Button $g ([System.Drawing.RectangleF]::new(820, 340, 150, 42)) "Cambiar..." (ColorRgb 255 255 255) (ColorRgb 17 31 44) (ColorRgb 174 190 205)
        Draw-Button $g ([System.Drawing.RectangleF]::new(720, 460, 120, 42)) "Instalar" (ColorRgb 0 126 140) (ColorRgb 255 255 255) (ColorRgb 0 126 140)
        Draw-Button $g ([System.Drawing.RectangleF]::new(856, 460, 120, 42)) "Cancelar" (ColorRgb 255 255 255) (ColorRgb 17 31 44) (ColorRgb 174 190 205)
    }
}

function Draw-UninstallerScreenshot {
    New-Canvas (Join-Path $outDir "02-desinstalador.png") {
        param($g)
        Draw-Window $g 240 130 800 430 "Desinstalar O2 Cloud Drive"
        Draw-AppIcon $g 282 202 44
        Draw-Text $g "Desinstalar O2 Cloud Drive" (New-Font 20 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 17 31 44) ([System.Drawing.RectangleF]::new(340, 196, 620, 46))
        Draw-Text $g "Se eliminara la aplicacion instalada. La sesion de O2 Cloud se conserva." (New-Font 11) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(282, 278, 700, 36))
        Draw-Input $g ([System.Drawing.RectangleF]::new(282, 330, 690, 38)) "C:\Program Files\O2 Cloud Drive"
        Draw-Button $g ([System.Drawing.RectangleF]::new(680, 472, 136, 42)) "Desinstalar" (ColorRgb 199 62 44) (ColorRgb 255 255 255) (ColorRgb 199 62 44)
        Draw-Button $g ([System.Drawing.RectangleF]::new(836, 472, 136, 42)) "Cancelar" (ColorRgb 255 255 255) (ColorRgb 17 31 44) (ColorRgb 174 190 205)
    }
}

function Draw-MainAppScreenshot {
    New-Canvas (Join-Path $outDir "03-aplicacion-principal.png") {
        param($g)
        Draw-Window $g 180 92 920 560 "O2 Cloud Drive"
        $surface = ColorRgb 248 250 252
        $border = ColorRgb 202 211 222
        Draw-Text $g "O2 Cloud Drive 0.8.1 beta" (New-Font 18 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 27 34 42) ([System.Drawing.RectangleF]::new(222, 164, 520, 44))
        Draw-Button $g ([System.Drawing.RectangleF]::new(946, 164, 110, 40)) "Logout" (ColorRgb 199 62 44) (ColorRgb 255 255 255) (ColorRgb 199 62 44)

        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(222, 226, 836, 142)) 8 $surface
        Stroke-RoundRect $g ([System.Drawing.RectangleF]::new(222, 226, 836, 142)) 8 $border 1
        Draw-Text $g "Letra de unidad" (New-Font 10) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(252, 254, 122, 28))
        Draw-Input $g ([System.Drawing.RectangleF]::new(384, 252, 78, 32)) "O:"
        Draw-Text $g "Nombre" (New-Font 10) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(494, 254, 80, 28))
        Draw-Input $g ([System.Drawing.RectangleF]::new(574, 252, 410, 32)) "O2 Cloud"
        Draw-Button $g ([System.Drawing.RectangleF]::new(252, 306, 150, 40)) "Montar unidad" (ColorRgb 34 153 92) (ColorRgb 255 255 255) (ColorRgb 34 153 92)
        Draw-Button $g ([System.Drawing.RectangleF]::new(420, 306, 130, 40)) "Abrir unidad" $surface (ColorRgb 27 34 42) $border
        Draw-Button $g ([System.Drawing.RectangleF]::new(568, 306, 120, 40)) "Desmontar" $surface (ColorRgb 27 34 42) $border

        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(222, 390, 836, 196)) 8 $surface
        Stroke-RoundRect $g ([System.Drawing.RectangleF]::new(222, 390, 836, 196)) 8 $border 1
        Draw-Text $g "Sesion" (New-Font 10) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(252, 414, 78, 26))
        Draw-Text $g "Iniciada" (New-Font 10 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 34 153 92) ([System.Drawing.RectangleF]::new(330, 414, 130, 26))
        Draw-Text $g "Unidad" (New-Font 10) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(610, 414, 78, 26))
        Draw-Text $g "O:" (New-Font 10 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 0 126 140) ([System.Drawing.RectangleF]::new(690, 414, 80, 26))
        Draw-Text $g "Log" (New-Font 10 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 27 34 42) ([System.Drawing.RectangleF]::new(252, 454, 80, 24))
        $logRect = [System.Drawing.RectangleF]::new(252, 482, 776, 72)
        Fill-RoundRect $g $logRect 4 (ColorRgb 255 255 255)
        Stroke-RoundRect $g $logRect 4 (ColorRgb 190 202 214) 1
        Draw-Text $g "13:41:08  Preparada.`n13:41:15  Login: sesion validada contra O2 Cloud.`n13:41:20  Montaje: unidad O: activa." (New-Font 9) (ColorRgb 27 34 42) ([System.Drawing.RectangleF]::new(264, 490, 748, 56)) "Near" "Near"
        Draw-Text $g "(C) Chemys 2026" (New-Font 8.5) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(860, 600, 190, 24)) "Far" "Center"
    }
}

function Draw-ExplorerScreenshot {
    New-Canvas (Join-Path $outDir "04-unidad-explorador.png") {
        param($g)
        Draw-Window $g 120 70 1040 600 "O2 Cloud (O:)"
        $white = ColorRgb 255 255 255
        $line = ColorRgb 224 231 238
        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(150, 142, 980, 42)) 4 $white
        Stroke-RoundRect $g ([System.Drawing.RectangleF]::new(150, 142, 980, 42)) 4 (ColorRgb 204 214 224) 1
        Draw-Text $g "Este equipo  >  O2 Cloud (O:)  >  Backup" (New-Font 10) (ColorRgb 35 48 61) ([System.Drawing.RectangleF]::new(170, 150, 600, 28))

        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(150, 202, 230, 430)) 6 (ColorRgb 245 248 251)
        Draw-Text $g "Acceso rapido`n`nEscritorio`nDescargas`nDocumentos`nImagenes`n`nO2 Cloud`nEste equipo" (New-Font 10) (ColorRgb 46 60 74) ([System.Drawing.RectangleF]::new(172, 226, 180, 300)) "Near" "Near"
        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(164, 426, 190, 34)) 5 (ColorRgb 227 242 247)
        Draw-AppIcon $g 174 432 20
        Draw-Text $g "O2 Cloud" (New-Font 10 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 0 96 116) ([System.Drawing.RectangleF]::new(202, 428, 130, 28))

        $listX = 410
        $listY = 202
        Fill-RoundRect $g ([System.Drawing.RectangleF]::new($listX, $listY, 720, 430)) 6 $white
        Stroke-RoundRect $g ([System.Drawing.RectangleF]::new($listX, $listY, 720, 430)) 6 (ColorRgb 204 214 224) 1
        Draw-Text $g "Nombre" (New-Font 9 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(430, 220, 300, 24))
        Draw-Text $g "Fecha de modificacion" (New-Font 9 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(730, 220, 180, 24))
        Draw-Text $g "Tamano" (New-Font 9 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(980, 220, 120, 24))
        Draw-Line $g 430 250 1110 250 $line
        $rows = @(
            @("Backups", "Carpeta", ""),
            @("Peliculas", "Carpeta", ""),
            @("Fotos", "Carpeta", ""),
            @("Night of the Living Dead.mkv", "Archivo MKV", "541,8 MB"),
            @("Documento.pdf", "Archivo PDF", "2,3 MB"),
            @("Musica", "Carpeta", ""),
            @("Compartidos", "Carpeta", "")
        )
        $y = 266
        foreach ($row in $rows) {
            Draw-Line $g 430 ($y + 32) 1110 ($y + 32) (ColorRgb 239 243 247)
            if ($row[1] -eq "Carpeta") {
                Fill-RoundRect $g ([System.Drawing.RectangleF]::new(432, $y + 5, 24, 20)) 3 (ColorRgb 246 202 83)
            }
            else {
                Fill-RoundRect $g ([System.Drawing.RectangleF]::new(434, $y + 3, 20, 24)) 2 (ColorRgb 240 244 248)
                Stroke-RoundRect $g ([System.Drawing.RectangleF]::new(434, $y + 3, 20, 24)) 2 (ColorRgb 190 202 214)
            }
            Draw-Text $g $row[0] (New-Font 10) (ColorRgb 27 34 42) ([System.Drawing.RectangleF]::new(466, $y, 260, 30))
            Draw-Text $g "01/07/2026 13:40" (New-Font 9) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(730, $y, 180, 30))
            Draw-Text $g $row[2] (New-Font 9) (ColorRgb 75 91 107) ([System.Drawing.RectangleF]::new(980, $y, 120, 30)) "Far" "Center"
            $y += 42
        }
    }
}

function Draw-ShareScreenshot {
    New-Canvas (Join-Path $outDir "05-compartir-o2-cloud.png") {
        param($g)
        Draw-Window $g 210 110 860 500 "O2 Cloud (O:)"
        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(270, 230, 320, 238)) 7 (ColorRgb 255 255 255)
        Stroke-RoundRect $g ([System.Drawing.RectangleF]::new(270, 230, 320, 238)) 7 (ColorRgb 204 214 224) 1
        Draw-Text $g "Abrir`nReproducir`nCompartir O2 Cloud`nCopiar`nEliminar" (New-Font 10) (ColorRgb 27 34 42) ([System.Drawing.RectangleF]::new(316, 252, 220, 178)) "Near" "Near"
        Fill-RoundRect $g ([System.Drawing.RectangleF]::new(284, 312, 292, 34)) 4 (ColorRgb 227 242 247)
        Draw-AppIcon $g 292 319 20
        Draw-Text $g "Compartir O2 Cloud" (New-Font 10 ([System.Drawing.FontStyle]::Bold)) (ColorRgb 0 96 116) ([System.Drawing.RectangleF]::new(324, 315, 210, 26))

        Draw-Window $g 560 318 420 170 "Enlace compartido"
        Draw-Text $g "Night of the Living Dead.mkv" (New-Font 10) (ColorRgb 39 55 72) ([System.Drawing.RectangleF]::new(590, 390, 320, 24))
        Draw-Input $g ([System.Drawing.RectangleF]::new(590, 424, 250, 32)) "https://cloud.o2online.es/share/1RKKXR..."
        Draw-Button $g ([System.Drawing.RectangleF]::new(850, 422, 58, 36)) "Abrir" (ColorRgb 255 255 255) (ColorRgb 17 31 44) (ColorRgb 174 190 205)
        Draw-Button $g ([System.Drawing.RectangleF]::new(916, 422, 68, 36)) "Copiar" (ColorRgb 0 126 140) (ColorRgb 255 255 255) (ColorRgb 0 126 140)
    }
}

Draw-InstallerScreenshot
Draw-UninstallerScreenshot
Draw-MainAppScreenshot
Draw-ExplorerScreenshot
Draw-ShareScreenshot

Get-ChildItem -LiteralPath $outDir -Filter "*.png" | Select-Object FullName,Length,LastWriteTime
