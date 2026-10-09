using System.Collections.Generic;
using System.Threading;

namespace EmbedIO.Tests.TestObjects
{
    public static class Resources
    {
        public static readonly string SubIndex = @"<!DOCTYPE html>

<html lang=""en"" xmlns=""http://www.w3.org/1999/xhtml"">
<head>
    <meta charset=""utf-8"" />
    <title></title>
</head>
<body>
    <h1>Sub</h1>
</body>
</html>";

        public static readonly string Index = @"<!DOCTYPE html>

<html lang=""en"" xmlns=""http://www.w3.org/1999/xhtml"">
<head>
    <meta charset=""utf-8"" />
    <title></title>
</head>
<body>
    This is a placeholder
</body>
</html>";

        // Start above the well-known Windows media/management HTTP.sys service ports.
        private static int _counter = 10999;

        // Ports that HTTP.sys would never deliver to a localhost registration: a wildcard
        // URL reservation on the port captures the request first (see HttpSysReservedPorts).
        private static readonly HashSet<int> ExcludedPorts = new(HttpSysReservedPorts.Current);

        public static string GetServerAddress()
        {
            const string serverAddress = "http://localhost:{0}/";

            int port;
            do port = Interlocked.Increment(ref _counter);
            while (IsExcluded(port));
            return string.Format(serverAddress, port);
        }

        internal static bool IsExcluded(int port)
        {
            lock (ExcludedPorts) return ExcludedPorts.Contains(port);
        }

        internal static void Exclude(int port)
        {
            lock (ExcludedPorts) ExcludedPorts.Add(port);
        }
    }
}
