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

        public static string GetServerAddress()
        {
            const string serverAddress = "http://localhost:{0}/";

            var port = Interlocked.Increment(ref _counter);
            return string.Format(serverAddress, port);
        }
    }
}
