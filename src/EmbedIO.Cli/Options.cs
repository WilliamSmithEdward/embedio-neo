using System;
using System.Globalization;
using System.IO;

namespace EmbedIO.Cli
{
    internal sealed record Options(string? RootPath = null, int Port = 9696, string? ApiPath = null,
        bool NoWatch = false, bool NoBrowser = false, bool Help = false, bool Version = false)
    {
        internal static Options Parse(string[] args)
        {
            var result = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var parts = args[i].Split('=', 2);
                var name = parts[0];
                string Value()
                {
                    if (parts.Length == 2 && parts[1].Length > 0) return parts[1];
                    if (parts.Length == 1 && i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[++i];
                    throw new ArgumentException($"Missing value for {name}.");
                }

                if (parts.Length > 1 && name is not ("-p" or "--path" or "-o" or "--port" or "-a" or "--api"))
                    throw new ArgumentException($"Option {name} does not accept a value.");
                result = name switch
                {
                    "-p" or "--path" => result with { RootPath = Value() },
                    "-a" or "--api" => result with { ApiPath = Value() },
                    "-o" or "--port" => result with { Port = ParsePort(Value()) },
                    "--no-watch" => result with { NoWatch = true },
                    "--no-browser" => result with { NoBrowser = true },
                    "-h" or "--help" => result with { Help = true },
                    "--version" => result with { Version = true },
                    _ => throw new ArgumentException($"Unknown option: {name}. Use --help for usage."),
                };
            }
            if (!result.NoWatch && result.Port == 65535)
                throw new ArgumentException("Watch mode needs port + 1; use a port below 65535 or --no-watch.");
            return result;
        }

        internal string ResolveRoot(string currentDirectory)
        {
            var root = RootPath == null
                ? (Directory.Exists(Path.Combine(currentDirectory, "wwwroot")) ? Path.Combine(currentDirectory, "wwwroot") : currentDirectory)
                : Path.GetFullPath(RootPath, currentDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Web root does not exist: {root}");
            return Path.GetFullPath(root);
        }

        private static int ParsePort(string text)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new ArgumentException("Port must be an integer between 1 and 65535.");
            return port;
        }
    }
}
