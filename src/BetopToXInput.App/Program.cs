using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetopToXInput.Core;
using BetopToXInput.ViGEm;

namespace BetopToXInput.App;

internal static class Program
{
    public sealed class Settings
    {
        public string Mode { get; set; } = "vigem";   // "vigem" | "proxy" | "both"
        public int VigemUserIndex { get; set; } = 0;
        public double StickDeadzone { get; set; } = 0.08;
        public bool DumpHidReports { get; set; } = false;
        public string? LogFile { get; set; } = "betop-to-xinput.log";
    }

    private static int Main(string[] args)
    {
        Console.Title = "BetopToXInput";
        Console.WriteLine("==================================================");
        Console.WriteLine(" Betop C031 -> Xbox 360 (XInput) bridge");
        Console.WriteLine(" VID=0x0E8F  PID=0x0003  (KingChuang / Betop)");
        Console.WriteLine("==================================================");

        // Parse flags
        if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
        {
            PrintUsage();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--dump")
        {
            return RunDump();
        }
        if (args.Length > 0 && args[0] == "--calibrate")
        {
            return RunCalibrate();
        }
        if (args.Length > 0 && args[0] == "--list")
        {
            return RunList();
        }
        if (args.Length > 0 && args[0] == "--probe")
        {
            return RunProbe();
        }
        if (args.Length > 0 && args[0] == "--dump-all")
        {
            return RunDumpAll();
        }

        var settings = LoadSettings();
        Console.WriteLine($"Mode: {settings.Mode}");

        if (!BetopHidReader.IsDevicePresent())
        {
            Console.Error.WriteLine("ERROR: Betop C031 not detected.");
            Console.Error.WriteLine("  - Make sure the device is plugged in");
            Console.Error.WriteLine("  - If the unsigned vendor driver is failing to load,");
            Console.Error.WriteLine("    this tool can still work using the OS built-in HID");
            Console.Error.WriteLine("    class driver. Just plug the device in.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            Console.WriteLine("\nShutting down...");
            cts.Cancel();
            e.Cancel = true;
        };

        IDisposable? vigem = null;
        XInputProxyBridge? proxy = null;
        try
        {
            if (settings.Mode == "vigem" || settings.Mode == "both")
            {
                Console.WriteLine("Starting ViGEmBus bridge (virtual Xbox 360 controller)...");
                try
                {
                    var vigemBridge = new ViGEmBridge();
                    vigemBridge.PlugIn();
                    var reader = new BetopHidReader();
                    var rumble = new BetopRumbleWriter();
                    var service = new ViGEmService(reader, vigemBridge, rumble);
                    service.Start();
                    vigem = service;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ViGEm bridge failed: {ex.Message}");
                    Console.Error.WriteLine("  Make sure ViGEmBus driver is installed:");
                    Console.Error.WriteLine("  https://github.com/ViGEm/ViGEmBus/releases");
                    if (settings.Mode == "vigem")
                        return 4;
                }
            }
            if (settings.Mode == "proxy" || settings.Mode == "both")
            {
                Console.WriteLine("Starting XInput proxy bridge (drops into game directory)...");
                proxy = new XInputProxyBridge();
                proxy.Start();
            }
            if (settings.Mode != "vigem" && settings.Mode != "proxy" && settings.Mode != "both")
            {
                Console.Error.WriteLine($"Unknown mode: {settings.Mode}");
                return 1;
            }

            Console.WriteLine("Bridge is running. Press Ctrl+C to exit.");
            cts.Token.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex}");
            return 3;
        }
        finally
        {
            try { vigem?.Dispose(); } catch { /* ignore */ }
            try { proxy?.Dispose(); } catch { /* ignore */ }
        }
        return 0;
    }

    private static Settings LoadSettings()
    {
        try
        {
            if (File.Exists("appsettings.json"))
            {
                var json = File.ReadAllText("appsettings.json");
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to read appsettings.json: {ex.Message}");
        }
        return new Settings();
    }

    /// <summary>
    /// Connect to the Betop, print raw HID input reports as hex bytes, then exit.
    /// Useful for verifying the report layout when calibrating the parser.
    /// </summary>
    private static int RunDump()
    {
        Console.WriteLine("Dumping HID input reports. Press Ctrl+C to stop.");
        Console.WriteLine("(If you see no output below, the device is in exclusive mode or");
        Console.WriteLine(" the chosen HID path is the force-feedback interface, not input.)");
        Console.WriteLine();

        // Diagnostic: list matching paths
        var paths = BetopToXInput.Core.BetopHidPInvoke.EnumerateHidPaths(
            BetopToXInput.Core.BetopHidReader.BetopVendorId,
            BetopToXInput.Core.BetopHidReader.BetopProductId);
        Console.WriteLine($"Found {paths.Count} HID path(s) for Betop C031:");
        for (int i = 0; i < paths.Count; i++)
        {
            Console.WriteLine($"  [{i}] {paths[i]}");
        }
        Console.WriteLine();
        Console.WriteLine("Press a button / move a stick on the controller now.");
        Console.WriteLine();

        using var reader = new BetopToXInput.Core.BetopHidReader();
        reader.DumpAllHids = true;

        reader.RawInputReceived += bytes =>
        {
            var hex = BitConverter.ToString(bytes).Replace("-", " ");
            Console.WriteLine($"[raw {bytes.Length}B]  {hex}");
        };

        reader.InputReceived += state =>
        {
            Console.WriteLine($"  DPad:↑{state.DPadUp}↓{state.DPadDown}←{state.DPadLeft}→{state.DPadRight} " +
                              $"A:{state.ButtonA} B:{state.ButtonB} X:{state.ButtonX} Y:{state.ButtonY} " +
                              $"LB:{state.LeftShoulder} RB:{state.RightShoulder} " +
                              $"Start:{state.Start} Back:{state.Back} Guide:{state.Guide} " +
                              $"L3:{state.LeftStick} R3:{state.RightStick} " +
                              $"LT:{state.LeftTrigger} RT:{state.RightTrigger} " +
                              $"LX:{state.LeftStickX} LY:{state.LeftStickY} " +
                              $"RX:{state.RightStickX} RY:{state.RightStickY}");
        };

        reader.Start();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>
    /// Dump every RawInput HID report from any device. Use when --dump shows
    /// nothing but the device is plugged in, to check whether the device is
    /// even being routed through the RawInput API.
    /// </summary>
    private static int RunDumpAll()
    {
        Console.WriteLine("Dumping ALL RawInput HID reports (no VID/PID filter).");
        Console.WriteLine("Press any key on any HID device (or move the controller).");
        Console.WriteLine();
        Console.WriteLine("Duplicate consecutive reports are folded. New report is preceded");
        Console.WriteLine("by '<<CHANGE>>'. A diff against the prior Betop report is shown");
        Console.WriteLine("on the line above the change.");
        Console.WriteLine();

        using var reader = new BetopToXInput.Core.BetopHidReader();
        reader.DumpAllHids = true;

        byte[]? lastBetop = null;
        long lastBetopChangeMs = 0;
        long lastAnyChangeMs = 0;

        reader.AnyHIDInputReceived += (bytes, path) =>
        {
            bool isBetop = path.IndexOf("VID_0E8F", StringComparison.OrdinalIgnoreCase) >= 0
                        && path.IndexOf("PID_0003", StringComparison.OrdinalIgnoreCase) >= 0;
            var hex = BitConverter.ToString(bytes).Replace("-", " ");

            long now = Environment.TickCount64;
            if (isBetop)
            {
                if (lastBetop != null && lastBetop.Length == bytes.Length
                    && System.Linq.Enumerable.SequenceEqual(lastBetop, bytes))
                {
                    return; // fold duplicates
                }
                if (lastBetop != null && lastBetopChangeMs != 0
                    && now - lastBetopChangeMs < 5000)
                {
                    var diff = MakeDiff(lastBetop, bytes);
                    if (diff.Length > 0) Console.Error.WriteLine($"  diff: {diff}");
                }
                Console.Error.WriteLine($"<<CHANGE>> [{bytes.Length}B BETOP] {hex}");
                lastBetop = (byte[])bytes.Clone();
                lastBetopChangeMs = now;
            }
            else
            {
                if (now - lastAnyChangeMs < 250) return;
                lastAnyChangeMs = now;
                var shortPath = path.Length > 80 ? path.Substring(0, 80) + "..." : path;
                Console.Error.WriteLine($"[other {bytes.Length}B] {hex}  path={shortPath}");
            }
        };

        reader.InputReceived += state =>
        {
            Console.Error.WriteLine($"  parse: Up:{state.DPadUp} Down:{state.DPadDown} " +
                              $"A:{state.ButtonA} B:{state.ButtonB} " +
                              $"X:{state.ButtonX} Y:{state.ButtonY} " +
                              $"LT:{state.LeftTrigger} RT:{state.RightTrigger} " +
                              $"LX:{state.LeftStickX} LY:{state.LeftStickY} " +
                              $"RX:{state.RightStickX} RY:{state.RightStickY}");
        };

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static string MakeDiff(byte[] a, byte[] b)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                sb.Append($"b{i}:{a[i]:X2}->{b[i]:X2} ");
            }
        }
        return sb.ToString();
    }

    private static string BytesToHex(byte[] b) =>
        BitConverter.ToString(b).Replace("-", " ");

    /// <summary>
    /// Interactive step-by-step calibration. Walks the user through ~15 actions,
    /// captures the report at the moment they press Enter, and then prints a
    /// per-step diff table so we can see which byte/bit changed for each
    /// control. The resulting table is what we use to derive the correct
    /// ParseReport bit masks.
    /// </summary>
    private static int RunCalibrate()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  Betop C031 校准模式");
        Console.WriteLine("==================================================");
        Console.WriteLine();
        Console.WriteLine("我将引导你完成 15 步测试。每步:");
        Console.WriteLine("  1. 程序告诉你该按什么 / 推到哪");
        Console.WriteLine("  2. 你做这个动作并保持 1 秒");
        Console.WriteLine("  3. 按 [Enter] 采样当前报文");
        Console.WriteLine();
        Console.WriteLine("所有 15 步完成后, 我会打出每步的字节变化总览。");
        Console.WriteLine();

        using var reader = new BetopHidReader();

        // Last received Betop report (continuously updated by the pump).
        byte[]? lastReport = null;
        object reportLock = new();
        reader.AnyHIDInputReceived += (bytes, path) =>
        {
            bool isBetop = path.IndexOf("VID_0E8F", StringComparison.OrdinalIgnoreCase) >= 0
                        && path.IndexOf("PID_0003", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isBetop) return;
            lock (reportLock) lastReport = (byte[])bytes.Clone();
        };

        // Verify the device is talking.
        Console.Write("[等待手柄报文...]");
        SpinWait sw = new();
        for (int i = 0; i < 200; i++)
        {
            lock (reportLock) if (lastReport != null) break;
            sw.SpinOnce();
            Thread.Sleep(10);
        }
        lock (reportLock)
        {
            if (lastReport == null)
            {
                Console.WriteLine(" 失败: 5 秒内没有收到 Betop 报文。请确认手柄已插入。");
                return 2;
            }
            Console.WriteLine($" 收到! 报文长度 = {lastReport.Length} 字节。");
        }
        Console.WriteLine();

        var steps = new (string Name, string Instruction)[]
        {
            ("基准 (松开所有按键)",     "确认手放在腿上, 不要碰任何键, 两个摇杆居中。"),
            ("按住 A 键",              "按住 A 键不放。"),
            ("松开 A 键",              "把 A 键松开。"),
            ("按住 B 键",              "按住 B 键不放。"),
            ("按住 X 键",              "按住 X 键不放。"),
            ("按住 Y 键",              "按住 Y 键不放。"),
            ("按住 Start",             "按住 Start 键不放。"),
            ("按住 Back / Select",     "按住 Back 键不放。"),
            ("按住 LB (左肩键)",        "按住 LB 不放。"),
            ("按住 RB (右肩键)",        "按住 RB 不放。"),
            ("左摇杆推到底 (任意方向)",  "把左摇杆推到任一极限位置并保持。"),
            ("左摇杆居中",              "把左摇杆松回中间。"),
            ("左扳机扣到底",            "扣住 LT 到最大并保持。"),
            ("右扳机扣到底",            "扣住 RT 到最大并保持。"),
            ("D-Pad 上",                "按住方向键 ↑。"),
            ("D-Pad 下",                "按住方向键 ↓。"),
        };

        var samples = new List<(string Name, byte[]? Bytes)>();
        for (int i = 0; i < steps.Length; i++)
        {
            var (name, instr) = steps[i];
            Console.WriteLine($"=== 第 {i + 1}/{steps.Length} 步: {name} ===");
            Console.WriteLine($"动作: {instr}");
            Console.WriteLine();
            Console.WriteLine("请做这个动作并保持 ~1 秒, 然后按 [Enter] 采样...");
            Console.ReadLine();
            lock (reportLock)
            {
                samples.Add((name, lastReport != null ? (byte[])lastReport.Clone() : null));
            }
            Console.WriteLine();
        }

        Console.WriteLine("==================================================");
        Console.WriteLine("  校准完成。字节变化总览:");
        Console.WriteLine("==================================================");
        Console.WriteLine();

        byte[]? baseline = samples.Count > 0 ? samples[0].Bytes : null;
        for (int i = 0; i < samples.Count; i++)
        {
            var (name, bytes) = samples[i];
            if (bytes == null)
            {
                Console.WriteLine($"  [{i + 1:D2}] {name,-30} (no sample)");
                continue;
            }
            string diff;
            if (i == 0 || baseline == null)
            {
                diff = "(基准)";
            }
            else
            {
                diff = MakeDiff(baseline, bytes);
                if (string.IsNullOrEmpty(diff)) diff = "(无变化)";
            }
            Console.WriteLine($"  [{i + 1:D2}] {name,-30}  {BytesToHex(bytes)}");
            Console.WriteLine($"        diff vs 基准: {diff}");
        }

        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine("  请把上面输出截图发给我, 我就能写出正确的 ParseReport。");
        Console.WriteLine("==================================================");
        return 0;
    }

    private static int RunList()
    {
        var paths = BetopToXInput.Core.BetopHidPInvoke.EnumerateHidPaths(
            BetopToXInput.Core.BetopHidReader.BetopVendorId,
            BetopToXInput.Core.BetopHidReader.BetopProductId);
        Console.WriteLine($"Found {paths.Count} HID path(s) for Betop C031 (VID=0x0E8F, PID=0x0003):");
        for (int i = 0; i < paths.Count; i++)
        {
            Console.WriteLine($"  [{i}] {paths[i]}");
        }
        return 0;
    }

    /// <summary>
    /// Enumerate every HID device on the system, not just Betop. Use this when
    /// --list returns 0 to check whether HID enumeration itself is working.
    /// </summary>
    private static int RunProbe()
    {
        var paths = BetopToXInput.Core.BetopHidPInvoke.EnumerateAllHidPaths();
        Console.WriteLine($"Found {paths.Count} HID path(s) on this system:");
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            bool isBetop = p.IndexOf("VID_0E8F", StringComparison.OrdinalIgnoreCase) >= 0;
            var tag = isBetop ? " <-- Betop C031" : "";
            Console.WriteLine($"  [{i}] {p}{tag}");
        }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  BetopToXInput.App [--dump | --dump-all | --calibrate | --list | --probe]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --dump        Print raw HID reports from the Betop (collapses duplicates).");
        Console.WriteLine("  --calibrate   Step-by-step interactive calibration. Walks you through");
        Console.WriteLine("                ~15 actions, captures the report at each step, then");
        Console.WriteLine("                prints a per-step byte-diff table.");
        Console.WriteLine("  --dump-all    Print every RawInput HID report from every device.");
        Console.WriteLine("  --list        List HID paths matching the Betop VID/PID.");
        Console.WriteLine("  --probe       Enumerate every HID device on the system.");
        Console.WriteLine();
        Console.WriteLine("Configuration: appsettings.json");
        Console.WriteLine("  Mode: 'vigem' (default) | 'proxy' | 'both'");
    }
}
