# Raw ESC/POS test page for the receipt printer.
#
# Why raw bytes and not a document: the receipt has to print unattended for a week, and
# anything that goes through a document application can raise a modal dialog - an update
# prompt, a recovery prompt, a licence check - which stops the queue with nobody watching.
# Bytes straight to the spooler in RAW mode cannot do that.
#
# What this page is FOR: confirming the column count. A 57 mm printer is usually 384 dots
# wide, which is 32 characters at Font A and 42 at Font B - but "usually" is not good enough
# to lay a design to, so the page prints rulers and lets the paper answer.
#
#   .\Tools\print-test.ps1                      # prints the test page
#   .\Tools\print-test.ps1 -Printer "NAME"      # to a different printer
#   .\Tools\print-test.ps1 -WhatIf              # show the bytes, print nothing

param(
    [string] $Printer = "CX-58D-POS",
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RawPrinter
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
            di.pDocName = "RD receipt test";
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

# ---- ESC/POS ----------------------------------------------------------------

$bytes = New-Object System.Collections.Generic.List[byte]

function Raw([byte[]] $b)  { foreach ($x in $b) { $bytes.Add($x) } }
function Txt([string] $s)  { Raw ([System.Text.Encoding]::ASCII.GetBytes($s)) }
function Line([string] $s) { Txt $s; Raw @(0x0A) }

$ESC = 0x1B
$GS  = 0x1D

Raw @($ESC, 0x40)                 # initialise

Raw @($ESC, 0x61, 0x01)           # centre
Raw @($GS, 0x21, 0x11)            # double width + height
Line "PAY ATTENTION"
Raw @($GS, 0x21, 0x00)            # normal size
Line "printer test"
Raw @($ESC, 0x61, 0x00)           # left

Raw @(0x0A)

# ---- the diagnostic: where does a long line wrap? --------------------------

Line "FONT A ruler, count to the wrap:"
Raw @($ESC, 0x4D, 0x00)           # Font A (12 x 24 dots)
Line "....5...10...15...20...25...30...35...40...45..50"
Line "123456789012345678901234567890123456789012345678"
Raw @(0x0A)

Line "FONT B ruler:"
Raw @($ESC, 0x4D, 0x01)           # Font B (9 x 17 dots)
Line "....5...10...15...20...25...30...35...40...45..50...55...60...65"
Line "1234567890123456789012345678901234567890123456789012345678901234"
Raw @($ESC, 0x4D, 0x00)           # back to Font A
Raw @(0x0A)

# ---- the layout the receipt is built to: 32 columns ------------------------

Line "32-COLUMN LAYOUT (Font A):"
Line "================================"
Line "session                   #0247"
Line "date           26.10.2026 14:32"
Line "--------------------------------"
Line "seconds_held                 184"
Line "fixations                     37"
Line "surface_claimed             0.42"
Line "--------------------------------"
Line "generous response        01:52"
Line "  to confirm the habit"
Line "--------------------------------"
Line "total_attended         00:03:04"
Line "================================"

Raw @(0x0A)
Raw @($ESC, 0x61, 0x01)
Line "if the rules above are flush"
Line "with the paper edges, 32 is"
Line "right. if they wrap, read the"
Line "ruler and tell me the number."
Raw @($ESC, 0x61, 0x00)

Raw @($ESC, 0x64, 0x04)           # feed 4 lines so the cut clears the text
Raw @($GS, 0x56, 0x00)            # full cut

# ---- send -------------------------------------------------------------------

$payload = $bytes.ToArray()

Write-Output ("Printer : " + $Printer)
Write-Output ("Bytes   : " + $payload.Length)

if ($WhatIf) {
    Write-Output "WhatIf - nothing sent."
    return
}

[RawPrinter]::Send($Printer, $payload)
Write-Output "Sent. Check the paper."
