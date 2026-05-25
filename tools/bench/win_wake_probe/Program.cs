// SPDX-License-Identifier: Apache-2.0
//
// Tiny micro-benchmark measuring the floor RT of cross-process kernel wake
// on Windows using Named EventWaitHandle. Spawns one child process that
// flips two events back-and-forth N times; measures total elapsed and
// divides for per-RT cost. The bare floor here gives an unambiguous upper
// bound on what any no-spin SHM transport can achieve on this hardware.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinWakeProbe;

public static partial class Program
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SignalObjectAndWait(IntPtr hObjectToSignal, IntPtr hObjectToWaitOn, uint dwMilliseconds, [MarshalAs(UnmanagedType.Bool)] bool bAlertable);

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
        {
            return RunChild(args[1], args[2], args[3], int.Parse(args[4]), args.Length > 5 && args[5] == "saw");
        }

        var iters = args.Length > 0 ? int.Parse(args[0]) : 50000;
        var mode = args.Length > 1 ? args[1] : "split"; // split | saw
        return RunParent(iters, mode);
    }

    static int RunParent(int iters, string mode)
    {
        var id = Guid.NewGuid().ToString("N");
        var evtAName = $"Local\\winwake_probe_{id}_a";
        var evtBName = $"Local\\winwake_probe_{id}_b";
        var evtDoneName = $"Local\\winwake_probe_{id}_done";

        using var evtA = new EventWaitHandle(false, EventResetMode.AutoReset, evtAName);
        using var evtB = new EventWaitHandle(false, EventResetMode.AutoReset, evtBName);
        using var evtDone = new EventWaitHandle(false, EventResetMode.AutoReset, evtDoneName);

        var hA = evtA.SafeWaitHandle.DangerousGetHandle();
        var hB = evtB.SafeWaitHandle.DangerousGetHandle();
        var hDone = evtDone.SafeWaitHandle.DangerousGetHandle();

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no procpath");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"child {evtAName} {evtBName} {evtDoneName} {iters} {mode}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Console.WriteLine($"[parent] mode={mode} iters={iters}");

        using var p = Process.Start(psi)!;

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            SetEvent(hB);
            WaitForSingleObject(hA, 5000);
        }

        var sw = Stopwatch.StartNew();
        if (mode == "split")
        {
            // Classic: SetEvent then WaitForSingleObject.
            for (int i = 0; i < iters; i++)
            {
                SetEvent(hB);
                WaitForSingleObject(hA, uint.MaxValue);
            }
        }
        else // "saw" = SignalObjectAndWait — atomic
        {
            for (int i = 0; i < iters; i++)
            {
                SignalObjectAndWait(hB, hA, uint.MaxValue, false);
            }
        }
        sw.Stop();

        SetEvent(hDone);
        p.WaitForExit();

        double avgUs = sw.Elapsed.TotalMicroseconds / iters;
        Console.WriteLine($"[parent] mode={mode}  RT avg = {avgUs:F2} µs  ({iters} iters, total {sw.Elapsed.TotalMilliseconds:F1} ms)");
        return 0;
    }

    static int RunChild(string aName, string bName, string doneName, int iters, bool saw)
    {
        using var evtA = EventWaitHandle.OpenExisting(aName);
        using var evtB = EventWaitHandle.OpenExisting(bName);
        using var evtDone = EventWaitHandle.OpenExisting(doneName);
        var hA = evtA.SafeWaitHandle.DangerousGetHandle();
        var hB = evtB.SafeWaitHandle.DangerousGetHandle();
        var hDone = evtDone.SafeWaitHandle.DangerousGetHandle();

        // Warmup (use the same primitives the main loop will use).
        for (int i = 0; i < 1000; i++)
        {
            WaitForSingleObject(hB, 5000);
            SetEvent(hA);
        }
        if (saw)
        {
            // For SAW, the child must START with a wait so the parent's
            // first signal lands on a waiter (auto-reset events don't
            // accumulate across signal/wait in this combined call).
            WaitForSingleObject(hB, uint.MaxValue);
            for (int i = 0; i < iters - 1; i++)
            {
                SignalObjectAndWait(hA, hB, uint.MaxValue, false);
            }
            SetEvent(hA);
        }
        else
        {
            for (int i = 0; i < iters; i++)
            {
                WaitForSingleObject(hB, uint.MaxValue);
                SetEvent(hA);
            }
        }

        WaitForSingleObject(hDone, 1000);
        return 0;
    }
}
