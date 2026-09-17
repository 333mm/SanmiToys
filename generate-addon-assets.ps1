# ==============================================================================
# SanmiToys - Microsoft Store Add-on Assets Generator
# Types: Coffee, Cake, Lunch, Supporter
# Sizes: 300x300 (Partner Center Requirement), 512x512, 1024x1024
# ==============================================================================
param (
    [string]$SourceIcon = "$PSScriptRoot/src/SanmiToys.Host/Assets/app.png",
    [string]$OutputDir  = "$PSScriptRoot/Releases/StoreListingAssets/Addons"
)

$sourceIconResolved = (Resolve-Path $SourceIcon).Path
if (!(Test-Path $sourceIconResolved)) {
    Write-Error "Source icon not found at: $SourceIcon"
    exit 1
}

if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$outputDirResolved = (Resolve-Path $OutputDir).Path

$csharpCode = @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class AddonAssetGenerator
{
    private static GraphicsPath CreateRoundedRectanglePath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1.0f, r * 2);
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawCoffee(Graphics g, float cx, float cy, float size, Color color)
    {
        using (var pen = new Pen(color, size * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var brush = new SolidBrush(color))
        {
            // Cup body
            float w = size * 0.65f;
            float h = size * 0.58f;
            float x = cx - w * 0.60f;
            float y = cy - h * 0.25f;
            using (var cupPath = CreateRoundedRectanglePath(x, y, w, h, size * 0.16f))
            {
                g.FillPath(brush, cupPath);
            }
            // Handle
            float hw = size * 0.32f;
            float hh = size * 0.38f;
            float hx = x + w - size * 0.05f;
            float hy = y + size * 0.10f;
            using (var handlePath = new GraphicsPath())
            {
                handlePath.AddArc(hx, hy, hw, hh, -60, 160);
                g.DrawPath(pen, handlePath);
            }
            // Steam lines
            float sy = y - size * 0.08f;
            float s1x = cx - size * 0.15f;
            float s2x = cx + size * 0.05f;
            g.DrawLine(pen, s1x, sy, s1x, sy - size * 0.18f);
            g.DrawLine(pen, s2x, sy, s2x, sy - size * 0.22f);
        }
    }

    private static void DrawCake(Graphics g, float cx, float cy, float size, Color color)
    {
        using (var brush = new SolidBrush(color))
        {
            // Cupcake base
            float bw = size * 0.65f;
            float bh = size * 0.42f;
            float bx = cx - bw / 2;
            float by = cy + size * 0.02f;
            PointF[] pts = {
                new PointF(bx, by),
                new PointF(bx + bw, by),
                new PointF(bx + bw * 0.85f, by + bh),
                new PointF(bx + bw * 0.15f, by + bh)
            };
            g.FillPolygon(brush, pts);

            // Frosting top
            float fw = size * 0.82f;
            float fh = size * 0.44f;
            float fx = cx - fw / 2;
            float fy = cy - fh * 0.72f;
            g.FillEllipse(brush, fx, fy, fw, fh);

            // Cherry on top
            float cr = size * 0.16f;
            g.FillEllipse(brush, cx - cr / 2, fy - cr * 0.65f, cr, cr);
        }
    }

    private static void DrawLunch(Graphics g, float cx, float cy, float size, Color color)
    {
        using (var brush = new SolidBrush(color))
        {
            // Cloche / Food Dome
            float pw = size * 0.85f;
            float ph = size * 0.20f;
            g.FillEllipse(brush, cx - pw / 2, cy + size * 0.16f, pw, ph);

            // Dome cover
            using (var path = new GraphicsPath())
            {
                path.AddArc(cx - pw * 0.40f, cy - size * 0.32f, pw * 0.80f, size * 0.60f, 180, 180);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
            // Dome handle knob
            float kr = size * 0.14f;
            g.FillEllipse(brush, cx - kr / 2, cy - size * 0.38f - kr / 2, kr, kr);
        }
    }

    private static void DrawCrown(Graphics g, float cx, float cy, float size, Color color)
    {
        using (var brush = new SolidBrush(color))
        {
            float w = size * 0.88f;
            float h = size * 0.60f;
            float x = cx - w / 2;
            float y = cy - h / 2 + size * 0.06f;

            PointF[] pts = {
                new PointF(x, y + h),                          // bottom-left
                new PointF(x + w, y + h),                      // bottom-right
                new PointF(x + w * 0.95f, y + size * 0.06f),   // top-right
                new PointF(x + w * 0.70f, y + h * 0.50f),     // dip-right
                new PointF(cx, y - size * 0.08f),             // center peak
                new PointF(x + w * 0.30f, y + h * 0.50f),     // dip-left
                new PointF(x + w * 0.05f, y + size * 0.06f)    // top-left
            };
            g.FillPolygon(brush, pts);

            // 3 dots on peaks
            float dotR = size * 0.13f;
            g.FillEllipse(brush, cx - dotR / 2, y - size * 0.08f - dotR * 0.7f, dotR, dotR);
            g.FillEllipse(brush, x + w * 0.05f - dotR / 2, y + size * 0.06f - dotR * 0.7f, dotR, dotR);
            g.FillEllipse(brush, x + w * 0.95f - dotR / 2, y + size * 0.06f - dotR * 0.7f, dotR, dotR);
        }
    }

    public static void GenerateAll(string sourcePath, string outputDir)
    {
        using (var source = Image.FromFile(sourcePath))
        {
            string[] types = { "Coffee", "Cake", "Lunch", "Supporter" };
            int[] sizes = { 300, 512, 1024 };

            foreach (var type in types)
            {
                foreach (var sz in sizes)
                {
                    string filename = string.Format("Addon_{0}_{1}x{1}.png", type, sz);
                    string outPath = Path.Combine(outputDir, filename);
                    GenerateSingle(source, type, sz, outPath);
                    Console.WriteLine("Generated: " + outPath);
                }
            }
        }
    }

    private static void GenerateSingle(Image source, string type, int canvasSize, string outPath)
    {
        using (var bmp = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float scale = canvasSize / 300.0f;

            // 1. Draw base application icon centered
            int iconSize = (int)(canvasSize * 0.82f);
            int iconX = (canvasSize - iconSize) / 2;
            int iconY = (canvasSize - iconSize) / 2;

            // Subtle drop shadow behind app icon
            using (var shadowBrush = new SolidBrush(Color.FromArgb(65, 0, 0, 0)))
            using (var shadowPath = CreateRoundedRectanglePath(iconX + 3 * scale, iconY + 5 * scale, iconSize, iconSize, 36 * scale))
            {
                g.FillPath(shadowBrush, shadowPath);
            }

            g.DrawImage(source, iconX, iconY, iconSize, iconSize);

            // 2. Add-on badge setup
            float bw, bh, bx, by, br;
            Color cGrad1, cGrad2, cBorder, cText;
            string label;

            if (type == "Coffee")
            {
                // Amber / Coffee Gold
                bw = 160 * scale;
                bh = 46 * scale;
                bx = canvasSize - bw - (10 * scale);
                by = canvasSize - bh - (16 * scale);
                br = 13 * scale;
                cGrad1 = Color.FromArgb(255, 255, 185, 45);  // Warm Honey Amber
                cGrad2 = Color.FromArgb(255, 235, 120, 15);  // Deep Amber
                cBorder = Color.FromArgb(255, 255, 240, 200);
                cText = Color.FromArgb(255, 55, 25, 0);       // Dark Mocha
                label = "COFFEE";
            }
            else if (type == "Cake")
            {
                // Sweet Pink / Rose
                bw = 145 * scale;
                bh = 46 * scale;
                bx = canvasSize - bw - (10 * scale);
                by = canvasSize - bh - (16 * scale);
                br = 13 * scale;
                cGrad1 = Color.FromArgb(255, 255, 120, 170); // Strawberry Pink
                cGrad2 = Color.FromArgb(255, 230, 40, 110);  // Deep Rose
                cBorder = Color.FromArgb(255, 255, 225, 240);
                cText = Color.White;
                label = "CAKE";
            }
            else if (type == "Lunch")
            {
                // Vivid Warm Orange / Coral
                bw = 155 * scale;
                bh = 46 * scale;
                bx = canvasSize - bw - (10 * scale);
                by = canvasSize - bh - (16 * scale);
                br = 13 * scale;
                cGrad1 = Color.FromArgb(255, 255, 140, 30);  // Vivid Orange
                cGrad2 = Color.FromArgb(255, 230, 75, 10);   // Dark Coral
                cBorder = Color.FromArgb(255, 255, 230, 210);
                cText = Color.White;
                label = "LUNCH";
            }
            else // Supporter
            {
                // Royal Violet / Purple
                bw = 188 * scale;
                bh = 46 * scale;
                bx = canvasSize - bw - (10 * scale);
                by = canvasSize - bh - (16 * scale);
                br = 13 * scale;
                cGrad1 = Color.FromArgb(255, 175, 75, 225);  // Bright Amethyst
                cGrad2 = Color.FromArgb(255, 115, 30, 175);  // Royal Purple
                cBorder = Color.FromArgb(255, 235, 210, 255);
                cText = Color.White;
                label = "SUPPORTER";
            }

            // Draw Badge Shadow
            using (var bShadowBrush = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
            using (var bShadowPath = CreateRoundedRectanglePath(bx + 2 * scale, by + 3 * scale, bw, bh, br))
            {
                g.FillPath(bShadowBrush, bShadowPath);
            }

            // Draw Badge Background
            using (var gradBrush = new LinearGradientBrush(new RectangleF(bx, by, bw, bh), cGrad1, cGrad2, 45.0f))
            using (var bPath = CreateRoundedRectanglePath(bx, by, bw, bh, br))
            using (var bPen = new Pen(cBorder, 2.0f * scale))
            {
                g.FillPath(gradBrush, bPath);
                g.DrawPath(bPen, bPath);
            }

            // Draw Icon inside Badge
            float iconCenterCX = bx + 22 * scale;
            float iconCenterCY = by + bh / 2;
            Color iconColor = (type == "Coffee") ? cText : Color.White;

            if (type == "Coffee")
            {
                DrawCoffee(g, iconCenterCX, iconCenterCY, 22 * scale, iconColor);
            }
            else if (type == "Cake")
            {
                DrawCake(g, iconCenterCX, iconCenterCY, 22 * scale, iconColor);
            }
            else if (type == "Lunch")
            {
                DrawLunch(g, iconCenterCX, iconCenterCY, 22 * scale, iconColor);
            }
            else // Supporter
            {
                DrawCrown(g, iconCenterCX, iconCenterCY, 22 * scale, iconColor);
            }

            // Draw Text
            float textOffsetLeft = 36 * scale;
            float fSize = (type == "Supporter" ? 17.5f : 19.0f) * scale;
            using (var font = new Font("Segoe UI", fSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var tBrush = new SolidBrush(cText))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                var textRect = new RectangleF(bx + textOffsetLeft, by, bw - textOffsetLeft - (6 * scale), bh);
                g.DrawString(label, font, tBrush, textRect, sf);
            }

            bmp.Save(outPath, ImageFormat.Png);
        }
    }
}
"@

# Execute via Windows PowerShell for reliable GDI+ compilation
$tempScript = [System.IO.Path]::GetTempFileName() + ".ps1"
$tempScriptContent = @"
Add-Type -TypeDefinition @'
$csharpCode
'@ -ReferencedAssemblies System.Drawing

[AddonAssetGenerator]::GenerateAll('$sourceIconResolved', '$outputDirResolved')
"@

Set-Content -Path $tempScript -Value $tempScriptContent -Encoding UTF8
try {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempScript
}
finally {
    if (Test-Path $tempScript) { Remove-Item $tempScript -Force }
}

Write-Host "`nAll Add-on assets successfully generated in: $outputDirResolved"
