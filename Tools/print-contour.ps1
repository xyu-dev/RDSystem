# Prints the contour map as an ESC/POS raster, to see what line art actually does at 203 dpi.
#
# Text is trivial for a thermal head. A raster is not: the head is 1-bit, the paper bleeds a
# little, and a hairline that looks right on a screen can come out either invisible or as a
# thick smear. This prints the same field at three line weights so the paper decides.
#
#   .\Tools\print-contour.ps1
#   .\Tools\print-contour.ps1 -WhatIf        # build the bitmap, print nothing

param(
    [string] $Printer = "CX-58D-POS",
    [int]    $Height = 240,
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

# The field and the rasteriser live in C# because this is ~92,000 samples with six trig calls
# each, and PowerShell loops would take half a minute to do what this does instantly.
Add-Type -TypeDefinition @'
using System;

public static class Contour
{
    // Must match the artboard's field() exactly, or the printed map is not the one on screen.
    static double Field(double nx, double ny, double cx, double cy)
    {
        double s = Math.Sin(nx * 15 + Math.Sin(ny * 9) * 2.2)
                 + Math.Sin(ny * 12 + Math.Sin(nx * 7) * 2.0)
                 + 0.8 * Math.Sin((nx + ny) * 11);

        double dx = nx - cx;
        double dy = ny - cy;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double ring = Math.Exp(-Math.Pow((r - 0.22) / 0.09, 2)) * 0.95;

        double v = 0.44 + 0.26 * s + ring;
        return v < 0 ? 0 : (v > 1 ? 1 : v);
    }

    /// <summary>
    /// 1-bit raster, 384 dots wide. Contours are found per PIXEL rather than as line segments:
    /// a pixel is set when the level falls between it and the neighbour to its right or below.
    /// That yields a one-dot line directly in the raster with no line-drawing step, and one dot
    /// is the finest thing the head can place.
    ///
    /// weight 2 sets the pixel below and right as well, for the heavier core contour.
    /// </summary>
    public static byte[] Build(int width, int height, double cx, double cy)
    {
        double[] v = new double[width * height];

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                v[y * width + x] = Field((double)x / (width - 1), (double)y / (height - 1), cx, cy);

        bool[] on = new bool[width * height];

        // Back to the first setting. The heavier version lost the drawing: at 2/3/4 the lines
        // crowd each other and the field reads as a blot rather than as contours. A single dot
        // prints faint, and faint is the right answer here.
        double[] levels = { 0.26, 0.16, 0.355 };
        int[] weights = { 1, 1, 2 };

        for (int L = 0; L < levels.Length; L++)
        {
            double level = levels[L];
            int w = weights[L];

            for (int y = 0; y < height - 1; y++)
            {
                for (int x = 0; x < width - 1; x++)
                {
                    double a = v[y * width + x];
                    double b = v[y * width + x + 1];
                    double c = v[(y + 1) * width + x];

                    bool crosses = ((a >= level) != (b >= level)) || ((a >= level) != (c >= level));
                    if (!crosses) continue;

                    // A square brush of side w, so weight is a real thickness rather than the
                    // one-pixel-right-and-down smear the first version used - that thickened
                    // the line on two sides only and read as a lighter, fuzzier hairline.
                    for (int by = 0; by < w; by++)
                    {
                        int yy = y + by;
                        if (yy >= height) break;

                        for (int bx = 0; bx < w; bx++)
                        {
                            int xx = x + bx;
                            if (xx >= width) break;

                            on[yy * width + xx] = true;
                        }
                    }
                }
            }
        }

        // Pack to ESC/POS raster order: row-major, 8 dots per byte, most significant bit left.
        int bytesPerRow = width / 8;
        byte[] packed = new byte[bytesPerRow * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!on[y * width + x]) continue;
                packed[y * bytesPerRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return packed;
    }
}
'@

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RawPrinter2
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOA di);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void Send(string printerName, byte[] bytes)
    {
        IntPtr h;
        if (!OpenPrinter(printerName, out h, IntPtr.Zero))
            throw new Exception("OpenPrinter failed for '" + printerName + "': " + Marshal.GetLastWin32Error());

        try
        {
            DOCINFOA di = new DOCINFOA();
            di.pDocName = "RD contour test";
            di.pDataType = "RAW";

            if (!StartDocPrinter(h, 1, ref di))
                throw new Exception("StartDocPrinter failed: " + Marshal.GetLastWin32Error());

            try
            {
                if (!StartPagePrinter(h))
                    throw new Exception("StartPagePrinter failed: " + Marshal.GetLastWin32Error());

                int written;
                if (!WritePrinter(h, bytes, bytes.Length, out written))
                    throw new Exception("WritePrinter failed: " + Marshal.GetLastWin32Error());

                if (written != bytes.Length)
                    throw new Exception("Short write: " + written + " of " + bytes.Length);

                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}
'@

$WIDTH = 384
$BYTES_PER_ROW = $WIDTH / 8
$BAND = 64        # rows per GS v 0 command - cheap printers choke on tall single rasters

$packed = [Contour]::Build($WIDTH, $Height, 0.42, 0.5)

$out = New-Object System.Collections.Generic.List[byte]
function Raw([byte[]] $b)  { foreach ($x in $b) { $out.Add($x) } }
function Line([string] $s) { Raw ([System.Text.Encoding]::ASCII.GetBytes($s)); Raw @(0x0A) }

Raw @(0x1B, 0x40)                     # initialise
Raw @(0x1B, 0x61, 0x01)               # centre
Line "CONTOUR RASTER TEST"
Line "384 dots / 203 dpi"
Raw @(0x0A)
Raw @(0x1B, 0x61, 0x00)               # left

# Send the image in horizontal bands. Each band is its own GS v 0, and consecutive bands
# abut with no feed between them, so the seams are invisible.
for ($top = 0; $top -lt $Height; $top += $BAND) {
    $rows = [Math]::Min($BAND, $Height - $top)

    Raw @(0x1D, 0x76, 0x30, 0x00)
    Raw @([byte]($BYTES_PER_ROW -band 0xFF), [byte](($BYTES_PER_ROW -shr 8) -band 0xFF))
    Raw @([byte]($rows -band 0xFF), [byte](($rows -shr 8) -band 0xFF))

    $start = $top * $BYTES_PER_ROW
    $count = $rows * $BYTES_PER_ROW
    Raw $packed[$start..($start + $count - 1)]
}

Raw @(0x0A)
Raw @(0x1B, 0x61, 0x01)
Line "hairline = mid level"
Line "medium   = halo"
Line "heavy    = core"
Raw @(0x0A)
Line "too faint? raise the weights."
Line "too thick? the head is bleeding."
Raw @(0x1B, 0x61, 0x00)

Raw @(0x1B, 0x64, 0x04)               # feed clear of the cutter
Raw @(0x1D, 0x56, 0x00)               # full cut

$payload = $out.ToArray()

Write-Output ("Printer   : " + $Printer)
Write-Output ("Raster    : " + $WIDTH + " x " + $Height + " dots, " + $packed.Length + " bytes")
Write-Output ("Bands     : " + [Math]::Ceiling($Height / $BAND) + " x " + $BAND + " rows")
Write-Output ("Total sent: " + $payload.Length + " bytes")

if ($WhatIf) {
    Write-Output "WhatIf - nothing sent."
    return
}

[RawPrinter2]::Send($Printer, $payload)
Write-Output "Sent. Check the paper."
