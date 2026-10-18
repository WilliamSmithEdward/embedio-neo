using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Testing;
using EmbedIO.Tus;

internal static partial class TusUploadFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) =>
        RunCoreAsync(trace, false, token);

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--tus-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --tus-selftest");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await RunCoreAsync(new StateMachineTrace("tus-uploads", 1729, []), true, stop.Token);
        }

        catch (StateMachineFailure failure) when (failure.Invariant == "tus.committed-offset")
        {
            Console.WriteLine("Rejected injected tus offset corruption: " + failure.Invariant);
            return true;
        }

        throw new StateMachineFailure("tus.negative-oracle", "Injected foreign offset was accepted.");
    }

    private sealed class Model(string path, int owner, int length)
    {
        internal readonly string Path = path;
        internal readonly int Owner = owner;
        internal readonly int Length = length;
        internal string Bytes = string.Empty;
    }

    private static async Task RunCoreAsync(StateMachineTrace trace, bool corrupt, CancellationToken token)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "embedio-tus-model-"
                + Guid
                    .NewGuid()
                    .ToString("N")
        );
        var models = new List<Model>();
        World? world = null;

        try
        {
            world = new World(root, token);
            await VerifyBrowserOverrideAsync(world.Store, trace, token);
            var start = await world.Create(0, 8);
            Require(start.Status == 201, "tus.creation");
            var first = new Model(
                (start.Location ?? throw new InvalidOperationException("Missing upload location.")),
                0,
                8
            );
            models.Add(first);
            var writes = await Task.WhenAll(
                world.Send(0, "PATCH", first.Path, 0, "ab"),
                world.Send(1, "PATCH", first.Path, 0, "ab", owner: 0)
            );
            Require(
                writes.Count(r => r.Status == 204) == 1 && writes.Count(r => r.Status == 409) == 1,
                "tus.concurrent-append-once"
            );
            first.Bytes = "ab";
            trace.Cover("tus.shared-store-overlap");
            await Verify(first, corrupt);

            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var model = models.Count == 0 ? null : models[step.Value % models.Count];

                switch (step.Command % 8)
                {
                    case 0:
                        {
                            var length = step.Value % 9;
                            var result = await world.Create(step.Actor, length);
                            var expected = models.Count >= 8 || models.Sum(m => m.Length) + length > 64 ? 507 : 201;
                            Require(result.Status == expected, "tus.reserved-capacity");

                            if (expected == 201)
                                models.Add(
                                    new Model(
                                        (result.Location
                                            ?? throw new InvalidOperationException("Missing upload location.")),
                                        step.Actor,
                                        length
                                    )
                                );

                            trace.Cover("tus.create-and-capacity");
                            break;
                        }

                    case 1 when model != null:
                        {
                            var bytes = new string(
                                (char)('a' + step.Actor),
                                Math.Min(1 + step.Value % 4, model.Length - model.Bytes.Length)
                            );
                            var response = await world.Send(
                                step.Actor,
                                "PATCH",
                                model.Path,
                                model.Bytes.Length,
                                bytes,
                                model.Owner
                            );
                            Require(response.Status == 204, "tus.append");
                            model.Bytes += bytes;
                            trace.Cover("tus.append");
                            break;
                        }

                    case 2 when model != null:
                        {
                            var response = await world.Send(
                                step.Actor,
                                "PATCH",
                                model.Path,
                                model.Bytes.Length + 1,
                                "x",
                                model.Owner
                            );
                            Require(response.Status == 409, "tus.stale-offset");
                            trace.Cover("tus.stale-offset");
                            break;
                        }

                    case 3 when model != null:
                        {
                            foreach (var method in new[] { "HEAD", "PATCH", "DELETE" })
                            {
                                var response = await world.Send(
                                    step.Actor,
                                    method,
                                    model.Path,
                                    0,
                                    method == "PATCH" ? "x" : null,
                                    (model.Owner + 1) % 3
                                );
                                Require(response.Status == 404 && response.Offset == null, "tus.owner-isolation");
                            }

                            trace.Cover("tus.owner-isolation");
                            break;
                        }

                    case 4 when model != null:
                        {
                            var response = await world.Send(step.Actor, "DELETE", model.Path, owner: model.Owner);
                            Require(response.Status == 204, "tus.termination");
                            models.Remove(model);
                            var missing = await world.Send(step.Actor, "HEAD", model.Path, owner: model.Owner);
                            Require(missing.Status == 404, "tus.terminated-not-found");
                            trace.Cover("tus.termination");
                            break;
                        }

                    case 5:
                        {
                            await world.DisposeAsync();
                            world = null;

                            if (model != null)
                            {
                                using var data = new FileStream(
                                    Path.Combine(
                                        root,
                                        model.Path
                                            .Split('/')
                                            .Last()
                                            + ".data"
                                    ),
                                    FileMode.Append
                                );
                                data.Write(Encoding.ASCII.GetBytes("uncommitted-tail"));
                                data.Flush(true);
                            }

                            world = new World(root, token);
                            trace.Cover("tus.restart-uncommitted-tail");
                            break;
                        }

                    case 6 when model != null:
                        {
                            var response = await world.Send(
                                step.Actor,
                                "PATCH",
                                model.Path,
                                model.Bytes.Length,
                                "",
                                model.Owner,
                                "sha256 " + Convert.ToBase64String(new byte[32])
                            );
                            Require(response.Status == 460, "tus.checksum-rollback");
                            trace.Cover("tus.checksum-rollback");
                            break;
                        }

                    default:
                        trace.Cover("tus.progress");
                        break;
                }

                foreach (var item in models) await Verify(item, false);

                Require(
                    world.Store.Count == models.Count && world.Store.ReservedBytes == models.Sum(m => m.Length),
                    "tus.accounting"
                );
            }

            // Finish every independent ledger entry and compare the actual persisted file bytes.
            foreach (var model in models)
            {
                while (model.Bytes.Length < model.Length)
                {
                    var bytes = new string('z', Math.Min(4, model.Length - model.Bytes.Length));
                    var result = await world.Send(0, "PATCH", model.Path, model.Bytes.Length, bytes, model.Owner);
                    Require(result.Status == 204, "tus.finish");
                    model.Bytes += bytes;
                }

                using var output = new MemoryStream();
                await world.Store.CopyCompletedToAsync(
                    model.Path
                        .Split('/')
                        .Last(),
                    Owner(model.Owner),
                    output,
                    token
                );
                Require(Encoding.ASCII.GetString(output.ToArray()) == model.Bytes, "tus.persisted-byte-order");
                trace.Cover("tus.completed-bytes");
            }

            async Task Verify(Model model, bool inject)
            {
                var response = await (world ?? throw new InvalidOperationException("Missing active host.")).Send(
                    0,
                    "HEAD",
                    model.Path,
                    owner: model.Owner
                );
                Require(
                    response.Status == 200 && response.Offset + (inject ? 1 : 0) == model.Bytes.Length,
                    "tus.committed-offset"
                );
            }
        }

        finally
        {
            if (world != null) await world.DisposeAsync();

            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new StateMachineFailure(invariant, "Independent tus byte/owner ledger diverged.");
    }

    private static string Owner(int value) => "tenant.test.subject." + value.ToString(CultureInfo.InvariantCulture);

    private static ClaimsPrincipal FixturePrincipal(string credential)
    {
        var claims = new[] { new Claim(ClaimTypes.Name, credential) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture"));
    }

    private sealed record Reply(int Status, long? Offset, string? Location);

    private sealed class World : IAsyncDisposable
    {
        internal readonly DiskTusUploadStore Store;
        private readonly TestWebServer[] _hosts;
        private readonly Task[] _running;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;

        internal World(string root, CancellationToken token)
        {
            _token = token;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Store = new DiskTusUploadStore(new TusUploadOptions(root, 8, 64, 8, 4));
            _hosts =
            [
                .. Enumerable
                    .Range(0, 3)
                    .Select(_ => new TestWebServer())
            ];

            foreach (var host in _hosts)
            {
                host
                    .WithModule(
                        new BearerAuthenticationModule(
                            "/",
                            (credential, _) => Task.FromResult<ClaimsPrincipal?>(FixturePrincipal(credential))
                        )
                    )
                    .WithModule(new TusUploadModule("/uploads/", Store, (c, _) => c.User.Identity?.Name));
            }

            _running = Array.ConvertAll(_hosts, h => h.RunAsync(_stop.Token));
        }

        internal async Task<Reply> Create(int owner, int length)
        {
            using var request = Message("POST", "/uploads/", owner);
            request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
            return await SendCore(owner, request);
        }

        internal async Task<Reply> Send(
            int actor,
            string method,
            string path,
            long offset = 0,
            string? body = null,
            int? owner = null,
            string? checksum = null
        )
        {
            using var request = Message(method, path, owner ?? actor);

            if (body != null)
            {
                request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            }

            if (method == "PATCH") request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));

            if (checksum != null) request.Headers.Add("Upload-Checksum", checksum);

            return await SendCore(actor, request);
        }

        private static HttpRequestMessage Message(string method, string path, int owner)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Tus-Resumable", "1.0.0");
            request.Headers.Add("Authorization", "Bearer " + Owner(owner));
            return request;
        }

        private async Task<Reply> SendCore(int actor, HttpRequestMessage request)
        {
            using var response = await _hosts[actor].Client.SendAsync(request, _token);
            return new Reply(
                (int)response.StatusCode,
                response.Headers.TryGetValues("Upload-Offset", out var offsets)
                    ? long.Parse(offsets.Single(), CultureInfo.InvariantCulture)
                    : null,
                response.Headers.Location?.ToString()
            );
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();

            try
            {
                await Task.WhenAll(_running);
            }

            finally
            {
                foreach (var host in _hosts) host.Dispose();

                _stop.Dispose();
                Store.Dispose();
            }
        }
    }
}
