using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Authentication
{
    /// <summary>
    /// Simple HTTP basic authentication module that stores credentials
    /// in a <seealso cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    public class BasicAuthenticationModule : BasicAuthenticationModuleBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BasicAuthenticationModule"/> class.
        /// </summary>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="realm">The authentication realm.</param>
        /// <remarks>
        /// <para>If <paramref name="realm"/> is <see langword="null"/> or the empty string,
        /// the <see cref="BasicAuthenticationModuleBase.Realm">Realm</see> property will be set equal to
        /// <see cref="IWebModule.BaseRoute">BaseRoute</see>.</para>
        /// </remarks>
        public BasicAuthenticationModule(string baseRoute, string? realm = null)
            : base(baseRoute, realm)
        {
        }

        /// <summary>
        /// Gets a dictionary of valid user names and passwords.
        /// </summary>
        /// <value>
        /// The accounts.
        /// </value>
        public ConcurrentDictionary<string, string> Accounts { get; } = new ConcurrentDictionary<string, string>(StringComparer.InvariantCulture);

        /// <inheritdoc />
        protected override Task<bool> VerifyCredentialsAsync(string path, string userName, string password, CancellationToken cancellationToken)
            => Task.FromResult(VerifyCredentialsInternal(userName, password));

        // An unknown user is compared against a value of its own rather than returning early,
        // so the work does not depend on whether the account exists, and the comparison itself
        // runs in time that does not depend on how many characters matched.
        private bool VerifyCredentialsInternal(string userName, string password)
        {
            if (userName == null) return false;
            var known = Accounts.TryGetValue(userName, out var storedPassword);
            return FixedTimeEquals(password ?? string.Empty, storedPassword ?? AbsentAccountPassword) & known;
        }

        // Compared against when no account matches, so an unknown user costs the same work as a
        // known one. A supplied password that happened to equal it still fails, because the
        // result is combined with whether the account exists at all.
        private static readonly string AbsentAccountPassword = new string('\0', 32);

        // Reads every position of the longer string, so the time depends on the lengths and not
        // on how many characters matched. Lengths are folded in rather than compared first,
        // which would return early and leak the stored length.
        private static bool FixedTimeEquals(string left, string right)
        {
            var difference = left.Length ^ right.Length;
            var length = Math.Max(left.Length, right.Length);
            for (var index = 0; index < length; index++)
                difference |= CharacterAt(left, index) ^ CharacterAt(right, index);

            return difference == 0;
        }

        private static int CharacterAt(string value, int index) => index < value.Length ? value[index] : 0;
    }
}
