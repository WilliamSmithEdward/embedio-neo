using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace EmbedIO.Tests.TestObjects
{
    /// <summary>
    /// Finds the TCP ports that carry a wildcard HTTP.sys URL reservation
    /// (<c>http://+:port/</c> or <c>http://*:port/</c>) on this Windows machine.
    /// </summary>
    /// <remarks>
    /// HTTP.sys examines the strong-wildcard category before explicit host names when
    /// it routes a request, and a request that matches a reservation with no
    /// registration under it is failed by HTTP.sys itself. A Microsoft-mode listener
    /// registered on <c>http://localhost:port/</c> therefore starts normally but never
    /// receives requests on such a port; on the hosted windows-2025 runner the clients
    /// get <c>503 Service Unavailable</c> from <c>Microsoft-HTTPAPI/2.0</c>. The test
    /// address allocator skips these ports so that neither listener mode is handed
    /// an address that HTTP.sys will not deliver.
    /// </remarks>
    internal static class HttpSysReservedPorts
    {
        private static readonly Regex WildcardReservation = new(
            @"https?://[+*]:(\d{1,5})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Gets the wildcard-reserved ports found when the test assembly loaded.</summary>
        public static IReadOnlyCollection<int> Current { get; } = Load();

        /// <summary>Extracts the wildcard-reserved ports from <c>netsh http show urlacl</c> output.</summary>
        /// <remarks>Only the URL itself is matched, so localized labels do not matter.</remarks>
        public static IReadOnlyCollection<int> Parse(string urlaclOutput)
        {
            ArgumentNullException.ThrowIfNull(urlaclOutput);
            var ports = new HashSet<int>();
            foreach (Match match in WildcardReservation.Matches(urlaclOutput))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && port is >= 1 and <= 65535)
                    ports.Add(port);
            }

            return ports;
        }

        private static IReadOnlyCollection<int> Load()
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<int>();
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("netsh", "http show urlacl")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    return Array.Empty<int>();
                }

                errors.GetAwaiter().GetResult();
                return process.ExitCode == 0 ? Parse(output.GetAwaiter().GetResult()) : Array.Empty<int>();
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                // Without the reservation list the allocator keeps its plain sequence.
                return Array.Empty<int>();
            }
        }
    }
}
