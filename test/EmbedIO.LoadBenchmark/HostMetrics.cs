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

        return TimeSpan.Zero;
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
    }
}
