# Prints a receipt PNG to the thermal printer as a raw ESC/POS raster.
#
# Raw bytes rather than a document application, for the reason print-test.ps1 gives: the
# receipt has to print unattended for a week, and anything routed through mspaint or a shell
# print verb can raise a modal dialog - an update prompt, a recovery prompt, a licence check -
# which stops the queue with nobody watching. Bytes straight to the spooler in RAW mode cannot.
#
# It also prints dot-for-dot. The driver reports 203 dpi and 48.0 mm printable, so the 384 dot
# receipt lands at exactly 1:1 with no scaling to soften the one-bit edges.
#
# Called by RDReceipt with the PNG it just wrote:
#
#   Print Command   : powershell.exe
#   Print Arguments : -NoProfile -ExecutionPolicy Bypass -File "C:\Users\admin\Exhibition_xinyun\RDSystem\Tools\print-receipt.ps1" -File "{file}"
#
# By hand:
#
#   .\Tools\print-receipt.ps1 -File "path\to\receipt.png"
#   .\Tools\print-receipt.ps1 -File "..." -WhatIf      # pack it, print nothing

param(
    [Parameter(Mandatory = $true)]
    [string] $File,

    [string] $Printer = "CX-58D-POS",
    [switch] $NoCut,
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RawPrinterReceipt
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

    public static void Send(string printer, byte[] bytes)
    {
        IntPtr h;
        if (!OpenPrinter(printer, out h, IntPtr.Zero))
            throw new Exception("OpenPrinter failed: " + Marshal.GetLastWin32Error());

        try
        {
            DOCINFOA di = new DOCINFOA();
            di.pDocName = "RD Receipt";
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

                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}
'@

if (-not (Test-Path -LiteralPath $File)) { throw "No such file: $File" }

$path = (Resolve-Path -LiteralPath $File).Path
$bmp  = New-Object System.Drawing.Bitmap($path)

try {
    $MAX_DOTS = 384                       # 48.0 mm at 203 dpi, the printable width

    if ($bmp.Width -gt $MAX_DOTS) {
        throw "Image is $($bmp.Width) dots wide; the head is $MAX_DOTS. Printing it would crop, not scale."
    }

    $width  = $bmp.Width
    $height = $bmp.Height
    $bytesPerRow = [Math]::Ceiling($width / 8)

    # Pack to 1 bit per dot, MSB first, which is the order GS v 0 expects. The PNG is already
    # one-bit black and white, so this is a threshold at mid grey and not a conversion.
    $packed = New-Object byte[] ($bytesPerRow * $height)

    for ($y = 0; $y -lt $height; $y++) {
        $rowBase = $y * $bytesPerRow

        for ($x = 0; $x -lt $width; $x++) {
            $c = $bmp.GetPixel($x, $y)

            if ((($c.R + $c.G + $c.B) / 3) -lt 128) {
                $i = $rowBase + [Math]::Floor($x / 8)
                $packed[$i] = $packed[$i] -bor (0x80 -shr ($x % 8))
            }
        }
    }
}
finally { $bmp.Dispose() }

$BAND = 64        # rows per GS v 0 - cheap printers choke on one tall raster

$out = New-Object System.Collections.Generic.List[byte]
function Raw([byte[]] $b) { foreach ($x in $b) { $out.Add($x) } }

Raw @(0x1B, 0x40)                     # initialise

# Banded, each band its own GS v 0. Consecutive bands abut with no feed between them, so the
# seams do not show.
for ($top = 0; $top -lt $height; $top += $BAND) {
    $rows = [Math]::Min($BAND, $height - $top)

    Raw @(0x1D, 0x76, 0x30, 0x00)
    Raw @([byte]($bytesPerRow -band 0xFF), [byte](($bytesPerRow -shr 8) -band 0xFF))
    Raw @([byte]($rows -band 0xFF), [byte](($rows -shr 8) -band 0xFF))

    $start = $top * $bytesPerRow
    Raw $packed[$start..($start + ($rows * $bytesPerRow) - 1)]
}

Raw @(0x1B, 0x64, 0x04)               # feed clear of the cutter

if (-not $NoCut) { Raw @(0x1D, 0x56, 0x00) }

$payload = $out.ToArray()

Write-Output ("File      : " + $path)
Write-Output ("Printer   : " + $Printer)
Write-Output ("Raster    : " + $width + " x " + $height + " dots (" + [Math]::Round($width / 203 * 25.4, 1) + " x " + [Math]::Round($height / 203 * 25.4, 1) + " mm)")
Write-Output ("Bands     : " + [Math]::Ceiling($height / $BAND))
Write-Output ("Total sent: " + $payload.Length + " bytes")

if ($WhatIf) {
    Write-Output "WhatIf - nothing sent."
    return
}

[RawPrinterReceipt]::Send($Printer, $payload)
Write-Output "Sent. Check the paper."
