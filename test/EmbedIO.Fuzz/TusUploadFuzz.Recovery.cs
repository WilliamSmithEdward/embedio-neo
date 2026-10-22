using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class TusUploadFuzz
{
    internal static async Task<bool> RecoverySelfTestAsync(string[] args)
    {
        if (args.Length == 0) return false;

        if (args[0] == "--tus-recovery-child")
        {
            if (args.Length != 3) throw new ArgumentException("Usage: --tus-recovery-child directory marker");

            await using var child = new World(args[1], CancellationToken.None);
            var upload = await child.Create(0, 6);
            Require(upload.Status == 201, "tus.crash-create");
            var patch = await child.Send(
                0,
                "PATCH",
                (upload.Location ?? throw new InvalidOperationException("Missing upload location.")),
                0,
                "abc"
            );
            Require(patch.Status == 204, "tus.crash-commit");

            using (var tail = new FileStream(
                Path.Combine(
                    args[1],
                    (upload.Location ?? throw new InvalidOperationException("Missing upload location."))
                        .Split('/')
                        .Last()
                        + ".data"
                ),
                FileMode.Append
            ))
            {
                tail.Write(Encoding.ASCII.GetBytes("uncommitted"));
                tail.Flush(true);
            }

            // Publish the marker by renaming a finished file. The parent reads it as soon as
            // the name exists, and Windows refuses that read while this process still holds
            // the file open for writing.
            var pending = args[2] + ".tmp";
            await File.WriteAllTextAsync(pending, upload.Location);
            File.Move(pending, args[2]);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return true;
        }

        if (args[0] != "--tus-recovery-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --tus-recovery-selftest");

        var parent = Path.Combine(
            Path.GetTempPath(),
            "embedio-tus-crash-"
                + Guid
                    .NewGuid()
                    .ToString("N")
        );
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "store");
        var marker = Path.Combine(parent, "ready.txt");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };

        if (string.Equals(
            Path.GetFileNameWithoutExtension(start.FileName),
            "dotnet",
            StringComparison.OrdinalIgnoreCase
        )) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);

        start.ArgumentList.Add("--tus-recovery-child");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(marker);
        using var childProcess = new Process { StartInfo = start };
        var started = false;

        try
        {
            started = childProcess.Start();

            if (!started) throw new InvalidOperationException("Could not start tus recovery child.");

            while (!File.Exists(marker))
            {
                if (childProcess.HasExited)
                    throw new InvalidOperationException("Recovery child exited before writing its marker.");

                await Task.Delay(20, stop.Token);
            }

            var path = await File.ReadAllTextAsync(marker, stop.Token);
            childProcess.Kill(true);
            await childProcess.WaitForExitAsync(stop.Token);
            await using var recovered = new World(root, stop.Token);
            var progress = await recovered.Send(0, "HEAD", path);
            Require(progress.Status == 200 && progress.Offset == 3, "tus.killed-process-offset");
            var foreign = await recovered.Send(1, "HEAD", path);
            Require(foreign.Status == 404, "tus.killed-process-owner");
            var finish = await recovered.Send(2, "PATCH", path, 3, "def", owner: 0);
            Require(finish.Status == 204 && finish.Offset == 6, "tus.killed-process-resume");
            using var bytes = new MemoryStream();
            await recovered.Store.CopyCompletedToAsync(
                path
                    .Split('/')
                    .Last(),
                Owner(0),
                bytes,
                stop.Token
            );
            Require(Encoding.ASCII.GetString(bytes.ToArray()) == "abcdef", "tus.killed-process-bytes");
            Console.WriteLine(
                "Passed killed-process tus recovery at offset "
                    + progress.Offset
                    + ", with owner isolation and exact resumed bytes."
            );
            return true;
        }

        finally
        {
            if (started && !childProcess.HasExited)
            {
                childProcess.Kill(true);
                await childProcess
                    .WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            Directory.Delete(parent, true);
        }
    }
}
