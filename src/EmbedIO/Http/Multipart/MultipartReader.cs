using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Reads multipart/form-data sequentially with bounded buffering and explicit resource limits.</summary>
    /// <remarks>Only one operation may run at a time. Read until a null section to validate the complete upload.
    /// A parse, limit, I/O, or cancellation failure makes the reader unusable. No files are created automatically.</remarks>
    public sealed partial class MultipartReader : IDisposable
    {
        private static readonly Encoding HeaderEncoding = new UTF8Encoding(false, true);
        private readonly Stream _input;
        private readonly bool _ownsInput;
        private readonly MultipartOptions _options;
        private readonly byte[] _buffer = new byte[8192];
        private readonly byte[] _marker;
        private readonly string _boundary;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private int _start;
        private int _end;
        private long _total;
        private int _parts;
        private int _files;
        private int _fields;
        private int _busy;
        private int _disposed;
        private bool _eof;
        private bool _started;
        private bool _closing;
        private bool _complete;
        private bool _faulted;
        private PartStream? _current;

        /// <summary>Initializes a reader over a borrowed stream, which remains open when this reader is disposed.</summary>
        /// <param name="input">Readable decoded request body.</param>
        /// <param name="boundary">Unquoted MIME boundary, from 1 to 70 ASCII characters.</param>
        /// <param name="options">Limits, or null for defaults. All limits must be nonnegative.</param>
        /// <param name="cancellationToken">Cancellation for the entire reader lifetime.</param>
        public MultipartReader(Stream input, string boundary, MultipartOptions? options = null, CancellationToken cancellationToken = default)
            : this(input, boundary, options, cancellationToken, false)
        {
        }

        internal MultipartReader(Stream input, string boundary, MultipartOptions? options, CancellationToken cancellationToken, bool ownsInput)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            if (!input.CanRead) throw new ArgumentException("A readable stream is required.", nameof(input));
            ValidateBoundary(boundary);
            _boundary = "--" + boundary;
            _marker = Encoding.ASCII.GetBytes("\r\n" + _boundary);
            _options = (options ?? new MultipartOptions()).Snapshot();
            _ownsInput = ownsInput;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _token = _lifetime.Token;
        }

        /// <summary>Drains any unread previous body and reads the next part's headers. Null means the closing delimiter and epilogue have been validated.</summary>
        /// <param name="cancellationToken">Cancellation for this operation, in addition to the reader's lifetime token.</param>
        /// <returns>The next part, or null after the entire input has been consumed.</returns>
        public async Task<MultipartSection?> ReadNextSectionAsync(CancellationToken cancellationToken = default)
        {
            Enter();
            using var linked = Link(cancellationToken);
            var token = linked?.Token ?? _token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (_complete) return null;
                if (_current != null)
                {
                    while (await ReadBodyCoreAsync(_current, null, 0, _buffer.Length, token).ConfigureAwait(false) != 0) { }
                    _current.Dispose();
                    _current = null;
                }
                if (!_started)
                {
                    await ReadInitialBoundaryAsync(token).ConfigureAwait(false);
                    _started = true;
                }
                if (_closing)
                {
                    _start = _end;
                    while (await FillAsync(token).ConfigureAwait(false)) _start = _end;
                    _complete = true;
                    return null;
                }
                if (_parts == _options.MaxParts) throw Limit();
                _parts++;
                return await ReadHeadersAsync(token).ConfigureAwait(false);
            }
            catch
            {
                _faulted = true;
                throw;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>Stops parsing and releases owned resources. Does not drain or validate unread data.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _lifetime.Cancel(); }
            finally
            {
                _current?.Dispose();
                _lifetime.Dispose();
                if (_ownsInput) _input.Dispose();
            }
        }

        internal static void ValidateBoundary(string boundary)
        {
            if (boundary == null) throw new ArgumentNullException(nameof(boundary));
            if (boundary.Length == 0 || boundary.Length > 70 || boundary[boundary.Length - 1] == ' ')
                throw new ArgumentException("Invalid multipart boundary.", nameof(boundary));
            foreach (var c in boundary)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || "'()+_,-./:=? ".IndexOf(c.ToString(), StringComparison.Ordinal) >= 0) continue;
                throw new ArgumentException("Invalid multipart boundary.", nameof(boundary));
            }
        }

        private void Enter()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MultipartReader));
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Multipart operations must be sequential.");
            if (_faulted)
            {
                Volatile.Write(ref _busy, 0);
                throw new InvalidOperationException("The multipart reader has failed.");
            }
        }

        private CancellationTokenSource? Link(CancellationToken token)
            => token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_token, token) : null;

        private static HttpException Malformed() => HttpException.BadRequest("Malformed multipart form data.");
        private static HttpException Limit() => new HttpException(413, "Multipart form data exceeds a configured limit.");

        private async Task<bool> FillAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_eof) return false;
            if (_start != 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var remaining = _options.MaxTotalBytes - _total;
            var count = _buffer.Length - _end;
            if (remaining < count) count = (int)remaining + 1;
            var read = await _input.ReadAsync(_buffer, _end, count, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (read > remaining) throw Limit();
            _total += read;
            _end += read;
            _eof = read == 0;
            return !_eof;
        }

        private async Task<string> ReadLineAsync(int limit, bool allowEof, CancellationToken token, bool header = true)
        {
            using var bytes = new MemoryStream();
            var cr = false;
            while (true)
            {
                if (_start == _end && !await FillAsync(token).ConfigureAwait(false))
                {
                    if (allowEof && !cr) break;
                    throw Malformed();
                }
                if (bytes.Length + (cr ? 1 : 0) >= limit) throw Limit();
                var value = _buffer[_start++];
                if (cr)
                {
                    if (value != '\n') throw Malformed();
                    break;
                }
                if (value == '\r') cr = true;
                else if (value == '\n') throw Malformed();
                else bytes.WriteByte(value);
            }
            // Replacement decoding is safe for ignored preamble, but ASCII's '?' fallback
            // is not: '?' is a legal boundary character and could forge a delimiter.
            try { return (header ? HeaderEncoding : Encoding.UTF8).GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
            catch (DecoderFallbackException) { throw Malformed(); }
        }

        private async Task ReadInitialBoundaryAsync(CancellationToken token)
        {
            while (true)
            {
                var line = await ReadLineAsync(_options.MaxHeaderBytes, true, token, false).ConfigureAwait(false);
                var trimmed = line.TrimEnd(' ', '\t');
                if (trimmed == _boundary) return;
                if (trimmed == _boundary + "--") { _closing = true; return; }
                if (_eof) throw Malformed();
            }
        }

        private async Task<MultipartSection> ReadHeadersAsync(CancellationToken token)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var remaining = _options.MaxHeaderBytes;
            while (true)
            {
                var line = await ReadLineAsync(remaining, false, token).ConfigureAwait(false);
                remaining -= HeaderEncoding.GetByteCount(line) + 2;
                if (line.Length == 0) break;
                if (headers.Count == _options.MaxHeaders) throw Limit();
                var colon = line.IndexOf(":", StringComparison.Ordinal);
                if (colon <= 0) throw Malformed();
                var name = line.Substring(0, colon);
                foreach (var c in name)
                {
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                        || "!#$%&'*+-.^_`|~".IndexOf(c.ToString(), StringComparison.Ordinal) >= 0) continue;
                    throw Malformed();
                }
                var value = line.Substring(colon + 1).Trim(' ', '\t');
                foreach (var c in value) if ((c < 32 && c != '\t') || c == 127) throw Malformed();
                if (headers.ContainsKey(name)) throw Malformed();
                headers.Add(name, value);
            }
            if (!headers.TryGetValue("Content-Disposition", out var disposition)
                || !ContentDispositionHeaderValue.TryParse(disposition, out var parsed)
                || parsed == null || !string.Equals(parsed.DispositionType, "form-data", StringComparison.OrdinalIgnoreCase)) throw Malformed();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? field = null;
            string? file = null;
            string? extendedFile = null;
            foreach (var parameter in parsed.Parameters)
            {
                if (!seen.Add(parameter.Name)) throw Malformed();
                if (string.Equals(parameter.Name, "name", StringComparison.OrdinalIgnoreCase)) field = Unquote(parameter.Value);
                if (string.Equals(parameter.Name, "filename", StringComparison.OrdinalIgnoreCase)) file = Unquote(parameter.Value) ?? throw Malformed();
                if (string.Equals(parameter.Name, "filename*", StringComparison.OrdinalIgnoreCase)) extendedFile = DecodeExtendedFileName(parameter.Value);
            }
            file = extendedFile ?? file;
            if (field == null || headers.ContainsKey("Content-Transfer-Encoding")) throw Malformed();
            if (file == null)
            {
                if (_fields == _options.MaxFields) throw Limit();
                _fields++;
            }
            else
            {
                if (_files == _options.MaxFiles) throw Limit();
                _files++;
            }
            headers.TryGetValue("Content-Type", out var contentType);
            if (contentType != null && !MediaTypeHeaderValue.TryParse(contentType, out _)) throw Malformed();
            _current = new PartStream(this, file == null ? _options.MaxFieldBytes : _options.MaxFileBytes);
            return new MultipartSection(field, file, contentType, new ReadOnlyDictionary<string, string>(headers), _current);
        }

        internal static string? Unquote(string? value)
        {
            if (value == null) return null;
            if (value.Length < 2 || value[0] != '"') return value;
            var builder = new StringBuilder(value.Length - 2);
            for (var i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '\\') i++;
                builder.Append(value[i]);
            }
            return builder.ToString();
        }

        // HttpClient emits RFC 5987 filenames even though RFC 7578 discourages them.
        // Decode only this explicit form; ordinary filename values remain untouched.
        private static string DecodeExtendedFileName(string? value)
        {
            if (value == null || !value.StartsWith("UTF-8'", StringComparison.OrdinalIgnoreCase)) throw Malformed();
            var languageEnd = value.IndexOf("'", 6, StringComparison.Ordinal);
            if (languageEnd < 0) throw Malformed();
            using var bytes = new MemoryStream();
            for (var i = languageEnd + 1; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '%')
                {
                    if (i + 2 >= value.Length || Hex(value[i + 1]) < 0 || Hex(value[i + 2]) < 0) throw Malformed();
                    bytes.WriteByte((byte)((Hex(value[i + 1]) << 4) | Hex(value[i + 2])));
                    i += 2;
                }
                else
                {
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                        || "!#$&+-.^_`|~".IndexOf(c.ToString(), StringComparison.Ordinal) >= 0)) throw Malformed();
                    bytes.WriteByte((byte)c);
                }
            }
            string decoded;
            try { decoded = HeaderEncoding.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
            catch (DecoderFallbackException) { throw Malformed(); }
            foreach (var c in decoded) if (c < 32 || c == 127) throw Malformed();
            return decoded;
        }

        private static int Hex(char c)
            => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
    }
}
