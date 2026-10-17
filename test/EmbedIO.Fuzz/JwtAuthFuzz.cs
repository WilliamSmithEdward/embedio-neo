using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class JwtAuthFuzz
{
    private const int MaximumSize = 2048;
    private static readonly Lazy<Fixtures> Cached = new(() => new Fixtures());
    private sealed record Fixture(string Token, bool Accepted, int User, int Counter, string Kind);

    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        var fixtures = Cached.Value;
        var pending = new List<Task>();
        var gates = new List<TaskCompletionSource<bool>>();
        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                if (step.Command == 9)
                {
                    // Three distinct users concurrently validate through the same validator instance.
                    var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    gates.Add(start);
                    var waiting = 0;
                    var validations = new Task<ClaimsPrincipal?>[3];
                    for (var user = 0; user < 3; user++)
                    {
                        var input = fixtures.Get(step.Actor, user, step.Value, 0);
                        validations[user] = Task.Run(async () =>
                        {
                            if (Interlocked.Increment(ref waiting) == 3) ready.TrySetResult(true);
                            await start.Task.WaitAsync(token);
                            return await Validate(fixtures.Validators[step.Actor], input, token);
                        }, token);
                        pending.Add(validations[user]);
                    }
                    await ready.Task.WaitAsync(token);
                    start.TrySetResult(true);
                    var results = await Task.WhenAll(validations).WaitAsync(token);
                    var first = results[0]?.Identity as ClaimsIdentity ?? throw new StateMachineFailure("jwt.concurrent-identity", "Missing first identity.");
                    first.AddClaim(new Claim("request-local", "changed"));
                    Check(results.Skip(1).All(principal => principal?.HasClaim("request-local", "changed") == false),
                        "jwt.principal-isolation", "A request mutation leaked to a concurrent principal.");
                    trace.Cover("jwt.three-user-shared-validator-overlap");
                }
                else if (step.Command == 10)
                {
                    var input = fixtures.Get(step.Actor, step.Actor, step.Value, step.Value % 9);
                    using var canceled = new CancellationTokenSource();
                    await canceled.CancelAsync();
                    var observed = false;
                    try { await fixtures.Validators[step.Actor].ValidateAsync(input.Token, canceled.Token); }
                    catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { observed = true; }
                    Check(observed, "jwt.cancellation-result", "Cancellation became credentials or invalid-token result.");
                    trace.Cover("jwt.precanceled-validation");
                }
                else if (step.Command == 11)
                {
                    // This is a valid credential for one trust domain, presented to a different one.
                    var input = fixtures.Get(step.Actor, step.Actor, step.Value, 0) with { Accepted = false, Kind = "other-trust-domain" };
                    await Validate(fixtures.Validators[(step.Actor + 1) % 3], input, token);
                    trace.Cover("jwt.cross-issuer-isolation");
                }
                else
                {
                    var input = fixtures.Get(step.Actor, step.Actor, step.Value, step.Command);
                    await Validate(fixtures.Validators[step.Actor], input, token);
                    trace.Cover("jwt." + input.Kind);
                }
            }
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult(true);
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private static async Task<ClaimsPrincipal?> Validate(JwtBearerTokenValidator validator, Fixture input, CancellationToken token)
    {
        var principal = await validator.ValidateAsync(input.Token, token);
        Check((principal != null) == input.Accepted, "jwt.trust-ledger", "Unexpected acceptance for fixture kind " + input.Kind + ".");
        if (principal == null) return null;
        Check(principal.Identity?.IsAuthenticated == true && principal.Identity.AuthenticationType == "Bearer",
            "jwt.authentication", "A validated credential lacks authenticated Bearer identity.");
        Check(principal.Identity?.Name == UserName(input.User) && principal.FindFirst("sub")?.Value == UserName(input.User),
            "jwt.subject-isolation", "The principal belongs to a different synthetic user.");
        Check(principal.FindFirst("counter")?.Value == input.Counter.ToString(CultureInfo.InvariantCulture),
            "jwt.claim-ledger", "The validated counter differs from the independently recorded input.");
        Check(principal.IsInRole("role" + input.User.ToString(CultureInfo.InvariantCulture)), "jwt.role-ledger", "The principal lost its configured role.");
        Check(!principal.HasClaim("request-local", "changed"), "jwt.cached-principal-isolation", "Validation returned a previously mutated identity.");
        return principal;
    }

    private static string UserName(int user) => "synthetic-user-" + user.ToString(CultureInfo.InvariantCulture);
    private static void Check(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }

    private sealed class Fixtures
    {
        private readonly SymmetricSecurityKey[] _keys;
        private readonly ConcurrentDictionary<(int Issuer, int User, int Counter, int Kind), Fixture> _tokens = new();
        // Fixed broad offsets avoid boundary races; this command-line process is bounded well below a day.
        private readonly DateTime _origin = DateTime.UtcNow;
        internal JwtBearerTokenValidator[] Validators { get; }

        internal Fixtures()
        {
            // Public deterministic fixture bytes, never deployment credentials.
            _keys = Enumerable.Range(0, 3).Select(actor => new SymmetricSecurityKey(Enumerable.Repeat((byte)(actor + 1), 64).ToArray())
            { KeyId = "fixture-key-" + actor.ToString(CultureInfo.InvariantCulture) }).ToArray();
            Validators = Enumerable.Range(0, 3).Select(actor =>
            {
                var options = new JwtBearerOptions { Issuer = Issuer(actor), Audience = Audience(actor), MaxTokenSizeInBytes = MaximumSize };
                options.SigningKeys.Add(_keys[actor]);
                options.AllowedAlgorithms.Add(SecurityAlgorithms.HmacSha256);
                options.AllowedTokenTypes.Add("at+jwt");
                return new JwtBearerTokenValidator(options);
            }).ToArray();
        }

        internal Fixture Get(int issuer, int user, int counter, int kind)
            => _tokens.GetOrAdd((issuer, user, counter, kind), key => Create(key.Issuer, key.User, key.Counter, key.Kind));

        private Fixture Create(int issuer, int user, int counter, int kind)
        {
            if (kind == 7 && counter % 4 >= 2) return new Fixture("invalid." + new string((char)('a' + counter % 26), counter + 1) + ".!", false, user, counter, "malformed");
            if (kind == 8) return new Fixture(new string('a', MaximumSize + counter + 1), false, user, counter, "oversize");
            var descriptor = new SecurityTokenDescriptor
            {
                TokenType = kind == 7 ? "JWT" : "at+jwt",
                Issuer = Issuer(kind == 1 ? (issuer + 1) % 3 : issuer),
                Audience = Audience(kind == 2 ? (issuer + 1) % 3 : issuer),
                NotBefore = _origin.AddDays(kind == 4 ? 1 : -2),
                Expires = _origin.AddDays(kind == 3 ? -1 : 2),
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("sub", UserName(user)),
                    new Claim("counter", counter.ToString(CultureInfo.InvariantCulture)),
                    new Claim("role", "role" + user.ToString(CultureInfo.InvariantCulture)),
                }),
                SigningCredentials = kind == 6 ? null : new SigningCredentials(_keys[kind == 5 ? (issuer + 1) % 3 : issuer], SecurityAlgorithms.HmacSha256),
            };
            var encoded = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
            if (kind == 7 && counter % 4 == 1)
            {
                var input = Base64UrlEncoder.Encode("{\"alg\":\"HS256\"}") + "." + encoded.Split('.')[1];
                encoded = input + "." + Base64UrlEncoder.Encode(HMACSHA256.HashData(_keys[issuer].Key, Encoding.ASCII.GetBytes(input)));
            }
            var label = kind switch
            {
                0 => "valid",
                1 => "wrong-issuer",
                2 => "wrong-audience",
                3 => "expired",
                4 => "not-yet-valid",
                5 => "wrong-key",
                6 => "unsigned",
                7 => counter % 4 == 1 ? "missing-token-type" : "wrong-token-type",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            return new Fixture(encoded, kind == 0, user, counter, label);
        }

        private static string Issuer(int actor) => "https://issuer" + actor.ToString(CultureInfo.InvariantCulture) + ".example.test";
        private static string Audience(int actor) => "urn:synthetic-api:" + actor.ToString(CultureInfo.InvariantCulture);
    }
}
