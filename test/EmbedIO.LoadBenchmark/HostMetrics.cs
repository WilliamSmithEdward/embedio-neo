using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

// Whole-machine busy CPU time, used to estimate background load during a sample.
internal static class SystemCpu
{
    internal static TimeSpan BusyTime()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException("GetSystemTimes failed.");
            // Kernel time includes idle time on Windows.
            return TimeSpan.FromTicks(kernel + user - idle);
        }

        if (OperatingSystem.IsLinux())
        {
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var busy = fields.Sum() - fields[3] - fields[4];
            return TimeSpan.FromSeconds(busy / 100.0);
        }

        if (OperatingSystem.IsMacOS())
        {
            // HOST_CPU_LOAD_INFO: user, system, idle and nice ticks summed over all
            // CPUs, at 100 per second. The 32-bit counters wrap after about 27 days
            // on 18 CPUs; a wrap inside one sample shows as a negative background.
            var ticks = new uint[4];
            uint count = 4;
            if (NativeMethods.host_statistics(MacHost.Value, 3, ticks, ref count) != 0 || count != 4)
                throw new InvalidOperationException("host_statistics(HOST_CPU_LOAD_INFO) failed.");
            return TimeSpan.FromSeconds((ticks[0] + (double)ticks[1] + ticks[3]) / 100.0);
        }

        return TimeSpan.Zero;
    }

    private static readonly Lazy<uint> MacHost = new(NativeMethods.mach_host_self);

    // Core classes. Apple Silicon reports performance levels (for example "Super" and
    // "Performance" on M5 Pro) rather than one homogeneous set.
    internal static string? Topology()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        if (!int.TryParse(Command("sysctl", "-n hw.nperflevels"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var levels)) return "unavailable";
        var parts = new List<string>();
        for (var level = 0; level < levels; level++)
        {
            var prefix = "hw.perflevel" + level.ToString(CultureInfo.InvariantCulture);
            parts.Add($"{Command("sysctl", "-n " + prefix + ".name")}: {Command("sysctl", "-n " + prefix + ".physicalcpu")} physical / {Command("sysctl", "-n " + prefix + ".logicalcpu")} logical");
        }

        return string.Join("; ", parts);
    }

    internal static string ProcessorName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        }

        if (File.Exists("/proc/cpuinfo"))
        {
            return File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown";
        }

        if (OperatingSystem.IsMacOS()) return Command("sysctl", "-n machdep.cpu.brand_string");

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    internal static string Command(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException(file + " did not start.");
            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(10000) ? output : "timed out";
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return "unavailable: " + exception.Message;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("/usr/lib/libSystem.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint mach_host_self();

        [DllImport("/usr/lib/libSystem.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);
    }
}
