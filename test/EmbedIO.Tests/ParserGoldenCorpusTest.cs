using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Golden corpus for the parsers whose control flow was restructured in the
    // complexity pass. Each corpus file holds inputs from a fixed seeded generator
    // and the outputs recorded from the parsers before the restructuring, so the
    // files pin what each parser accepts, rejects and produces.
    //
    // Set EMBEDIO_PARSER_CORPUS_RECORD to a directory to write the corpus files
    // instead of verifying them, and EMBEDIO_PARSER_CORPUS_CASES to change the
    // number of cases (the default matches the committed files).
    public class ParserGoldenCorpusTest
    {
        private const string NullMarker = "<null>";
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void ByteRangeSelection() => Run("byte_ranges", 6, GenerateRangeCase, EvaluateRange, true, 500);

        [Test]
        public void SetCookieHeaders() => Run("set_cookie", 1, GenerateCookieCase, EvaluateCookies, true, 500);

        [Test]
        public void PriorityDictionaries() => Run("priority", 1, GeneratePriorityCase, EvaluatePriority, true, 1000);

        [Test]
        public void JsonNormalization() => Run("json_normalize", 2, GenerateJsonCase, EvaluateJson, true, 1000);

        [Test]
        public void ChunkedRequestBodies() => Run("chunked", 4, GenerateChunkedCase, EvaluateChunked, true, 400);

        [Test]
        public void DeflateFraming() => Run("deflate", 2, GenerateDeflateCase, EvaluateDeflate, false, 250);

        [Test]
        public void Http3ControlFrames() => Run("http3_control", 2, GenerateControlCase, EvaluateControl, true, 500);

        [Test]
        public void UrlEncodedData() => Run("url_encoded", 3, GenerateUrlEncodedCase, EvaluateUrlEncoded, true, 800);

        [Test]
        public void RequestCodingChains() => Run("request_coding", 3, GenerateCodingCase, EvaluateCoding, false, 300);

        // Inputs come from the generator in record mode and, when regenerate is set, are
        // also regenerated in verify mode and compared with the file to catch generator drift.
        // Inputs that depend on the recording machine (compressed data) are only read back.
        // The committed file holds the first committed cases of the generator's sequence.
        private static void Run(string name, int columns, Func<Rng, string[]> generate, Func<string[], string> evaluate, bool regenerate, int committed)
        {
            var record = Environment.GetEnvironmentVariable("EMBEDIO_PARSER_CORPUS_RECORD");
            var count = int.TryParse(Environment.GetEnvironmentVariable("EMBEDIO_PARSER_CORPUS_CASES"), NumberStyles.None, CultureInfo.InvariantCulture, out var configured)
                ? configured : committed;
            if (!string.IsNullOrEmpty(record))
            {
                Record(name, columns, count, generate, evaluate, record);
                return;
            }

            var cases = Load(name, columns);
            Assert.That(cases.Count, Is.EqualTo(committed), name + " corpus size");
            var rng = new Rng(Seed(name));
            var mismatches = new List<string>();
            for (var index = 0; index < cases.Count; index++)
            {
                var (inputs, expected) = cases[index];
                if (regenerate)
                {
                    var generated = generate(rng);
                    if (!generated.SequenceEqual(inputs))
                        throw new AssertionException($"{name} case {index}: the generator no longer produces the recorded input; regenerate the corpus.");
                }

                var actual = evaluate(inputs);
                if (actual != expected && mismatches.Count < 10)
                    mismatches.Add($"case {index}: input {string.Join(" | ", inputs.Select(Escape))}\n  expected {Escape(expected)}\n  actual   {Escape(actual)}");
                else if (actual != expected)
                    mismatches.Add($"case {index}");
            }

            Assert.That(mismatches, Is.Empty, $"{name}: {mismatches.Count} of {cases.Count} cases differ from the recorded corpus:\n{string.Join("\n", mismatches.Take(10))}");
        }

        private static void Record(string name, int columns, int count, Func<Rng, string[]> generate, Func<string[], string> evaluate, string directory)
        {
            Directory.CreateDirectory(directory);
            var rng = new Rng(Seed(name));
            var lines = new List<string> { $"# {name}: {count} cases, {columns} input columns, seed {Seed(name)}" };
            for (var index = 0; index < count; index++)
            {
                var inputs = generate(rng);
                if (inputs.Length != columns) throw new InvalidOperationException($"{name} generated {inputs.Length} columns.");
                lines.Add(string.Join("\t", inputs.Select(Escape).Append(Escape(evaluate(inputs)))));
            }

            File.WriteAllText(Path.Combine(directory, name + ".tsv"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        private static List<(string[] Inputs, string Expected)> Load(string name, int columns)
        {
            using var source = typeof(ParserGoldenCorpusTest).Assembly.GetManifestResourceStream("EmbedIO.Tests.Data.ParserCorpus." + name + ".tsv")
                ?? throw new AssertionException("Missing parser corpus " + name);
            using var reader = new StreamReader(source, Encoding.UTF8);
            var cases = new List<(string[], string)>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var fields = line.Split('\t');
                if (fields.Length != columns + 1) throw new AssertionException($"{name}: a corpus line has {fields.Length} fields.");
                cases.Add((fields.Take(columns).Select(Unescape).ToArray(), Unescape(fields[columns])));
            }

            return cases;
        }

        private static ulong Seed(string name)
        {
            // FNV-1a over the corpus name: stable across runtimes and processes.
            var hash = 14695981039346656037UL;
            foreach (var c in name) hash = (hash ^ c) * 1099511628211UL;
            return hash;
        }

        // Byte ranges: method, total length, maximum ranges, entity tag, If-Range, Range.
        private static string[] GenerateRangeCase(Rng rng)
        {
            var method = rng.Pick("GET", "GET", "GET", "GET", "GET", "QUERY", "HEAD", "POST");
            var total = rng.Pick(0L, 1L, 9L, 10L, 100L, 1000L, 1000L, 65536L, 65536L, 10L * 1024 * 1024 * 1024, long.MaxValue - 1);
            var maximum = rng.Pick(1, 2, 8, 8, 8, 8);
            var tag = rng.Pick(NullMarker, NullMarker, NullMarker, NullMarker, NullMarker, NullMarker, "\"abc\"", "\"abc\"", "W/\"abc\"", "\"\"", "abc", "\"open");
            var ifRange = rng.Pick(NullMarker, NullMarker, NullMarker, NullMarker, NullMarker, NullMarker, "\"abc\"", "W/\"abc\"", "\"other\"", "Sat, 29 Oct 1994 19:43:31 GMT", "garbage");
            return new[] { method, Invariant(total), Invariant(maximum), tag, ifRange, RangeHeader(rng) };
        }

        private static string RangeHeader(Rng rng)
        {
            var text = new StringBuilder();
            if (rng.Chance(15)) text.Append(rng.Pick(" ", "\t", "  "));
            text.Append(rng.Pick("bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=", "bytes=",
                "BYTES=", "Bytes=", "bytes =", "bytes", "octets=", "", "byte=", "bytes==", " bytes="));
            var specifications = rng.Chance(10) ? 0 : 1 + rng.Next(4);
            for (var i = 0; i < specifications; i++)
            {
                if (i > 0 || rng.Chance(10)) text.Append(rng.Pick(",", ", ", " ,", ",,", ", , ", "\t,"));
                if (rng.Chance(10)) text.Append(rng.Pick(" ", "\t"));
                text.Append(RangeSpecification(rng));
                if (rng.Chance(10)) text.Append(rng.Pick(" ", "\t"));
            }

            if (rng.Chance(10)) text.Append(rng.Pick(",", " ", "x", "-", "=", ";"));
            return text.ToString();
        }

        private static string RangeSpecification(Rng rng)
        {
            string Number() => rng.Pick("0", "1", "5", "9", "10", "99", "100", "500", "999", "1000", "4095", "65535", "007", "00", "4294967296",
                "9223372036854775807", "9223372036854775808", "99999999999999999999", "1a", "a", "");
            switch (rng.Next(12))
            {
                case 0:
                case 1: return "-" + Number();
                case 2: return Number() + "-";
                case 3:
                case 4:
                case 5:
                case 6: return Number() + "-" + Number();
                case 7: return Number();
                case 8: return Number() + "-" + Number() + "-" + Number();
                case 9: return "-";
                case 10: return Number() + " - " + Number();
                default: return rng.Pick("*", "x-y", "1-2-", "--5", "1--5", "+1-5", "1-+5", "1.5-2", "");
            }
        }

        private static string EvaluateRange(string[] inputs)
        {
            var request = new RangeRequest(inputs[0], inputs[5], inputs[4] == NullMarker ? null : inputs[4]);
            var total = long.Parse(inputs[1], CultureInfo.InvariantCulture);
            var maximum = int.Parse(inputs[2], CultureInfo.InvariantCulture);
            var tag = inputs[3] == NullMarker ? null : inputs[3];
            string selection;
            try
            {
                var method = typeof(HttpRequestExtensions).GetMethod("SelectByteRanges", Hidden)
                    ?? throw new AssertionException("Missing range selector.");
                var ranges = (Array?)method.Invoke(null, new object?[] { request, total, tag, null, false, maximum });
                selection = ranges == null ? "null" : "ranges:" + string.Join(",", ranges.Cast<object>().Select(r =>
                    Invariant((long)(r.GetType().GetProperty("Start", Hidden)?.GetValue(r) ?? throw new AssertionException("Missing start.")))
                    + "+" + Invariant((long)(r.GetType().GetProperty("Length", Hidden)?.GetValue(r) ?? throw new AssertionException("Missing length.")))));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                selection = Describe(error.InnerException);
            }

            string single;
            try
            {
                single = request.TryGetByteRange(total, tag, null, out var start, out var length) ? "true:" + Invariant(start) + "+" + Invariant(length) : "false";
            }
            catch (ArgumentException error)
            {
                single = Describe(error);
            }
            catch (HttpException error)
            {
                single = Describe(error);
            }

            return "select=" + selection + " single=" + single;
        }

        // Set-Cookie: one header value with one or more cookies. Time-dependent attributes
        // (Expires, Max-Age) are left to the dedicated cookie tests.
        private static string[] GenerateCookieCase(Rng rng)
        {
            var text = new StringBuilder();
            var cookies = 1 + rng.Next(3);
            for (var c = 0; c < cookies; c++)
            {
                if (c > 0) text.Append(rng.Pick(", ", ",", " , ", "; "));
                text.Append(CookiePair(rng));
                var attributes = rng.Next(5);
                for (var a = 0; a < attributes; a++)
                {
                    text.Append(rng.Pick("; ", ";", " ; ", ";  "));
                    text.Append(CookieAttribute(rng));
                }

                if (rng.Chance(10)) text.Append(rng.Pick(";", "; ", ",", " "));
            }

            return new[] { text.ToString() };
        }

        private static string CookiePair(Rng rng)
        {
            var name = rng.Pick("session", "session", "session", "id", "id", "id", "a", "a", "Name", "Name", "x_y", "path", "secure", "Version", "n a m e", "");
            var value = rng.Pick("1", "abc", "\"quoted\"", "", "a=b", "x y", "\"open", "%20", "value;", "v,w", "\"\"");
            return rng.Next(8) switch
            {
                0 => name,
                1 => name + "=",
                2 => "=" + value,
                3 => name + " = " + value,
                _ => name + "=" + value,
            };
        }

        private static string CookieAttribute(Rng rng)
        {
            var value = rng.Pick("1", "/", "/a/b", "example.com", ".example.com", "\"80\"", "\"80,443\"", "80", "\"\"", "", "a b",
                "text%20with%20spaces", "http://x/y", "\"http://x/y\"", "2", "-1", "x");
            var name = rng.Pick("Path", "Path", "Path", "path", "path", "PATH", "Domain", "Domain", "Domain", "domain", "Secure", "Secure", "secure", "HttpOnly", "HttpOnly",
                "httponly", "Discard", "Version", "version", "Port", "port", "Comment", "Comment", "CommentURL", "commenturl", "SameSite", "Unknown", "Max-Age-ish", "expiresX");
            return rng.Next(8) switch
            {
                0 => name,
                1 => name,
                2 => name + "=",
                _ => name + "=" + value,
            };
        }

        private static string EvaluateCookies(string[] inputs)
        {
            try
            {
                var cookies = CookieList.Parse(inputs[0]);
                return string.Join(" | ", cookies.Cast<Cookie>().Select(c =>
                    $"{c.Name}={c.Value};path={c.Path};domain={c.Domain};port={c.Port};version={Invariant(c.Version)};secure={c.Secure};httponly={c.HttpOnly};discard={c.Discard};comment={c.Comment};commenturi={c.CommentUri?.OriginalString}"));
            }
            catch (CookieException error)
            {
                return Describe(error);
            }
            catch (FormatException error)
            {
                return Describe(error);
            }
            catch (OverflowException error)
            {
                return Describe(error);
            }
            catch (ArgumentException error)
            {
                return Describe(error);
            }
        }

        // Priority: a Structured Fields dictionary with u and i members among others.
        private static string[] GeneratePriorityCase(Rng rng)
        {
            var text = new StringBuilder();
            var members = rng.Next(4);
            for (var m = 0; m < members; m++)
            {
                if (m > 0) text.Append(rng.Pick(",", ", ", " , ", ",\t", ",,", " ,"));
                else if (rng.Chance(10)) text.Append(rng.Pick(" ", "\t"));
                text.Append(rng.Pick("u", "u", "u", "i", "i", "x", "ab-c", "U", "*k", "9", "k1_2.3*", ""));
                switch (rng.Next(6))
                {
                    case 0: break;
                    case 1:
                        text.Append('=').Append(rng.Pick("0", "1", "3", "7", "8", "-1", "-0", "?1", "?0", "?2", "3.0", "1.", ".5", "\"s\"", "\"a\\\"b\"", "tok", "*t",
                            ":YQ==:", "::", "@123", "@-1", "@1.5", "%\"%41\"", "%\"%zz\"", "", "999999999999999", "9999999999999999", "123456789012.999", "1234567890123.1"));
                        break;
                    case 2:
                        text.Append("=(").Append(rng.Pick("", "1 2", "1  2", "?1 tok", "1;a=2 3", "1,2", "(1)", "1 ")).Append(rng.Chance(80) ? ")" : "");
                        break;
                    case 3:
                        text.Append('=').Append(rng.Pick("1", "?1", "tok")).Append(rng.Pick(";a", ";a=1", "; a", ";a=?0", ";", ";A", ";a=\"x\"", ";a;b"));
                        break;
                    case 4:
                        text.Append(rng.Pick(";p", ";p=1", "; p", ";=1"));
                        break;
                    default:
                        text.Append('=').Append(Invariant(rng.Next(10)));
                        break;
                }

                if (rng.Chance(10)) text.Append(rng.Pick(" ", "\t", "  "));
            }

            if (rng.Chance(10)) text.Append(rng.Pick(",", " ,", "x", ";"));
            return new[] { text.ToString() };
        }

        private static string EvaluatePriority(string[] inputs)
        {
            var type = Internal("EmbedIO.Net.Internal.HttpStructuredFields");
            var method = type.GetMethod("TryParsePriority", Hidden) ?? throw new AssertionException("Missing priority parser.");
            var arguments = new object?[] { inputs[0], null };
            var success = (bool)(method.Invoke(null, arguments) ?? throw new AssertionException("Missing parse result."));
            if (!success) return "false";
            var priority = arguments[1] ?? throw new AssertionException("Missing priority.");
            return "true:u=" + priority.GetType().GetProperty("Urgency", Hidden)?.GetValue(priority) + ",i=" + priority.GetType().GetProperty("Incremental", Hidden)?.GetValue(priority);
        }

        // JSON: comment handling mode and a document mixing strict and legacy syntax.
        private static string[] GenerateJsonCase(Rng rng)
        {
            var text = new StringBuilder();
            JsonValue(rng, text, 0);
            if (rng.Chance(10)) text.Append(rng.Pick(" ", "// tail", "/* tail */", "/* open", "//", ",", "x"));
            if (rng.Chance(8) && text.Length > 2) text.Length = 1 + rng.Next(text.Length - 1);
            return new[] { rng.Pick("Disallow", "Skip", "Allow"), text.ToString() };
        }

        private static void JsonValue(Rng rng, StringBuilder text, int depth)
        {
            void Space()
            {
                if (rng.Chance(30)) text.Append(rng.Pick(" ", "\t", "\n", "\r\n", "  "));
            }

            Space();
            switch (depth > 3 ? rng.Next(4) : rng.Next(7))
            {
                case 0:
                    text.Append(rng.Pick("1", "0", "-1", "+1", "+0", "007", "-007", "0.5", ".5", "-.5", "5.", "-5.", "1e5", "1E+5", "1.e5", "1.0e-5", "00", "0e0",
                        "+.5e1", "1.5.5", "--1", "1e", "0x1", "123456789012345678901234567890", "-0.0", "+", "-", "."));
                    break;
                case 1:
                    text.Append(rng.Pick("\"a\"", "\"\"", "\"a\\\"b\"", "\"tab\there\"", "\"nl\nhere\"", "\"ctl\u0001x\"", "\"\\u0041\"", "\"\\n\\t\"", "\"open",
                        "\"esc\\", "\"\u007f\"", "\"caf\u00e9\""));
                    break;
                case 2:
                    text.Append(rng.Pick("true", "false", "null", "\"true\"", "True", "nul"));
                    break;
                case 3:
                    text.Append(rng.Pick("// c\n", "/* c */", "/**/", "/* a\nb */", "/ x", "/* open"));
                    JsonValue(rng, text, depth + 1);
                    break;
                case 4:
                    text.Append('[');
                    var items = rng.Next(4);
                    for (var i = 0; i < items; i++)
                    {
                        if (i > 0) text.Append(rng.Pick(",", ", ", " ,"));
                        JsonValue(rng, text, depth + 1);
                    }

                    if (rng.Chance(20)) text.Append(',');
                    Space();
                    text.Append(rng.Chance(90) ? "]" : "");
                    break;
                case 5:
                    text.Append('{');
                    var properties = rng.Next(4);
                    for (var i = 0; i < properties; i++)
                    {
                        if (i > 0) text.Append(',');
                        Space();
                        text.Append(rng.Pick("\"k\"", "\"key\"", "\"n\"", "k", "\"\"", "\"1\""));
                        Space();
                        text.Append(rng.Pick(":", " : ", ""));
                        JsonValue(rng, text, depth + 1);
                    }

                    if (rng.Chance(20)) text.Append(',');
                    text.Append(rng.Chance(90) ? "}" : "");
                    break;
                default:
                    text.Append(rng.Pick("1", "\"s\"", "[]", "{}"));
                    break;
            }

            Space();
        }

        private static string EvaluateJson(string[] inputs)
        {
            var type = Internal("EmbedIO.Serialization.CompatibleJsonInput");
            var method = type.GetMethod("Normalize", Hidden) ?? throw new AssertionException("Missing JSON normalizer.");
            var handling = Enum.Parse<JsonCommentHandling>(inputs[0]);
            try
            {
                return "ok:" + (string)(method.Invoke(null, new object[] { inputs[1], handling }) ?? throw new AssertionException("Missing normalized text."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                return Describe(error.InnerException);
            }
        }

        // Chunked bodies: wire bytes, the split between the read-ahead buffer and the
        // source stream, the destination size per read and whether reads are asynchronous.
        private static string[] GenerateChunkedCase(Rng rng)
        {
            var wire = new List<byte>();
            var chunks = rng.Next(5);
            for (var c = 0; c < chunks; c++)
            {
                var size = rng.Pick(0, 1, 2, 5, 16, 100, 300);
                Ascii(wire, rng.Chance(50) ? size.ToString("x", CultureInfo.InvariantCulture) : size.ToString("X", CultureInfo.InvariantCulture));
                if (rng.Chance(25)) Ascii(wire, rng.Pick(";ext", ";ext=val", "; ext = val", ";ext=\"quoted val\"", ";ext=\"esc\\\"q\"", ";a;b=c", ";", ";=x", ";ext=\"open", ";ext=\"esc\\"));
                if (rng.Chance(5)) Ascii(wire, rng.Pick(" ", "\t", "x"));
                Ascii(wire, "\r\n");
                for (var i = 0; i < size; i++) wire.Add((byte)('a' + rng.Next(26)));
                Ascii(wire, rng.Chance(95) ? "\r\n" : rng.Pick("\n", "\r", "", "\r\r\n"));
            }

            if (rng.Chance(90))
            {
                Ascii(wire, "0");
                if (rng.Chance(15)) Ascii(wire, ";last");
                Ascii(wire, "\r\n");
                var trailers = rng.Next(4);
                for (var t = 0; t < trailers; t++)
                {
                    Ascii(wire, rng.Pick("X-One: first", "X-Two:\t second \t", "X-Three:", "x-four:a:b", "Content-Length: 5", "Host: h", "Trailer: x",
                        "Bad Name: v", ":v", "X-Ctl: \u0001", "X-Long: " + new string('v', 300), "X-Tab:\tv", "X-Space : v"));
                    Ascii(wire, "\r\n");
                }

                Ascii(wire, rng.Chance(90) ? "\r\n" : rng.Pick("\n", "", "\r\nextra"));
            }

            if (rng.Chance(30) && wire.Count > 0) Mutate(rng, wire);
            if (rng.Chance(20)) Ascii(wire, "GET / HTTP/1.1\r\n");
            var split = rng.Next(wire.Count + 1);
            var read = rng.Pick(1, 2, 3, 7, 16, 64, 4096);
            return new[] { Hex(wire.ToArray()), Invariant(split), Invariant(read), rng.Chance(50) ? "1" : "0" };
        }

        private static string EvaluateChunked(string[] inputs)
        {
            var wire = FromHex(inputs[0]);
            var split = int.Parse(inputs[1], CultureInfo.InvariantCulture);
            var readSize = int.Parse(inputs[2], CultureInfo.InvariantCulture);
            var asynchronous = inputs[3] == "1";
            var type = Internal("EmbedIO.Net.Internal.ChunkedRequestStream");
            var constructor = type.GetConstructor(Hidden, null, new[] { typeof(Stream), typeof(byte[]), typeof(int), typeof(int) }, null)
                ?? throw new AssertionException("Missing chunked constructor.");
            var initial = new byte[split];
            Array.Copy(wire, 0, initial, 0, split);
            using var source = new MemoryStream(wire, split, wire.Length - split, false);
            using var stream = (Stream)constructor.Invoke(new object[] { source, initial, 0, split });
            using var body = new MemoryStream();
            var buffer = new byte[readSize];
            try
            {
                while (true)
                {
                    var read = asynchronous ? stream.ReadAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult() : stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    body.Write(buffer, 0, read);
                }

                var arguments = new object?[] { null };
                var available = (bool)(type.GetMethod("TryGetTrailers", Hidden)?.Invoke(stream, arguments) ?? throw new AssertionException("Missing trailers."));
                var trailers = (IReadOnlyList<KeyValuePair<string, string>>)(arguments[0] ?? throw new AssertionException("Missing trailer list."));
                var remainder = (ArraySegment<byte>)(type.GetProperty("BufferedRemainder", Hidden)?.GetValue(stream) ?? throw new AssertionException("Missing remainder."));
                return "ok:" + Hex(body.ToArray()) + "|trailers:" + (available ? string.Join(";", trailers.Select(t => t.Key + "=" + t.Value)) : NullMarker)
                    + "|remainder:" + Hex(remainder.ToArray());
            }
            catch (IOException error)
            {
                return Describe(error) + "|read:" + Hex(body.ToArray());
            }
            catch (InvalidDataException error)
            {
                return Describe(error) + "|read:" + Hex(body.ToArray());
            }
        }

        // DEFLATE: compressed bytes (possibly corrupted) and the feed size.
        private static string[] GenerateDeflateCase(Rng rng)
        {
            var length = rng.Pick(0, 1, 10, 100, 500, 2000);
            var data = new byte[length];
            var alphabet = rng.Pick(2, 4, 26, 256);
            for (var i = 0; i < length; i++) data[i] = (byte)(i > 0 && rng.Chance(30) ? data[i - 1] : rng.Next(alphabet));
            var level = rng.Pick(CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression, CompressionLevel.SmallestSize);
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, level, true)) deflate.Write(data, 0, data.Length);
            var compressed = new List<byte>(output.ToArray());
            if (rng.Chance(35) && compressed.Count > 0) Mutate(rng, compressed);
            if (rng.Chance(10)) compressed.AddRange(new byte[] { 1, 2, 3 });
            var chunk = rng.Pick(1, 2, 3, 5, 17, 64, 1024, 65536);
            return new[] { Hex(compressed.ToArray()), Invariant(chunk) };
        }

        private static string EvaluateDeflate(string[] inputs)
        {
            var data = FromHex(inputs[0]);
            var chunk = int.Parse(inputs[1], CultureInfo.InvariantCulture);
            var type = Internal("EmbedIO.Internal.DeflateFramingValidator");
            var validator = Activator.CreateInstance(type, true) ?? throw new AssertionException("Missing validator.");
            var feed = type.GetMethod("Feed", Hidden) ?? throw new AssertionException("Missing Feed.");
            var complete = type.GetMethod("Complete", Hidden) ?? throw new AssertionException("Missing Complete.");
            var results = new List<string>();
            try
            {
                for (var offset = 0; offset < data.Length; offset += chunk)
                    results.Add(Invariant((int)(feed.Invoke(validator, new object[] { data, offset, Math.Min(chunk, data.Length - offset) }) ?? throw new AssertionException("Missing feed result."))));
                complete.Invoke(validator, null);
                return "feed:" + string.Join(",", results) + "|finished:" + type.GetProperty("Finished", Hidden)?.GetValue(validator)
                    + "|decoded:" + type.GetProperty("DecodedBytes", Hidden)?.GetValue(validator);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                return Describe(error.InnerException) + "|feed:" + string.Join(",", results);
            }
        }

        // HTTP/3 control stream: frames on the wire and whether the peer is a server.
        private static string[] GenerateControlCase(Rng rng)
        {
            var wire = new List<byte>();
            var peerIsServer = rng.Chance(15);
            if (rng.Chance(92)) Frame(wire, 4, Settings(rng));
            var frames = rng.Next(5);
            for (var f = 0; f < frames; f++)
            {
                var identifier = rng.Pick(0L, 4L, 8L, 12L, 13L, 100L, 1L << 30, (1L << 62) - 1);
                switch (rng.Next(9))
                {
                    case 0: Frame(wire, 7, Varint(identifier)); break;
                    case 1: Frame(wire, 13, Varint(identifier)); break;
                    case 2: Frame(wire, 3, Varint(identifier)); break;
                    case 3:
                        Frame(wire, rng.Chance(50) ? 0xf0700 : 0xf0701, Varint(identifier)
                            .Concat(Encoding.Latin1.GetBytes(rng.Pick("", "u=3", "u=3,i", "u=9", "u=?1", "bad=(", "u=3\u0080", "i"))).ToArray());
                        break;
                    case 4: Frame(wire, rng.Pick(0x21L, 0x2aL, 0x100L, 0x1f * 3 + 0x21L), Random(rng, rng.Next(41))); break;
                    case 5: Frame(wire, rng.Pick(0L, 1L, 2L, 5L, 6L, 8L, 9L), Random(rng, rng.Next(9))); break;
                    case 6: Frame(wire, 4, Settings(rng)); break;
                    case 7:
                        wire.AddRange(Varint(7));
                        var length = rng.Pick(0, 9, 16);
                        wire.AddRange(Varint(length));
                        wire.AddRange(Random(rng, length));
                        break;
                    default: Frame(wire, 7, new byte[] { 0xC0 }); break;
                }
            }

            if (rng.Chance(25) && wire.Count > 0)
            {
                var cut = rng.Next(wire.Count);
                wire.RemoveRange(cut, wire.Count - cut);
            }

            return new[] { Hex(wire.ToArray()), peerIsServer ? "1" : "0" };
        }

        private static byte[] Settings(Rng rng)
        {
            var payload = new List<byte>();
            var identifiers = new List<long> { 1L, 6L, 7L, 8L, 0x33L, 0x21L };
            var entries = rng.Next(4);
            for (var e = 0; e < entries; e++)
            {
                var identifier = identifiers[rng.Next(identifiers.Count)];
                if (rng.Chance(85)) identifiers.Remove(identifier);
                payload.AddRange(Varint(rng.Chance(5) ? 2L : identifier));
                payload.AddRange(Varint(rng.Pick(0L, 1L, 2L, 100L, 4096L, 65536L)));
            }

            if (rng.Chance(10)) payload.Add(0xC0);
            return payload.ToArray();
        }

        private static string EvaluateControl(string[] inputs)
        {
            var wire = FromHex(inputs[0]);
            var type = Internal("EmbedIO.Net.Internal.Http3.Http3ControlStream");
            var constructor = type.GetConstructor(Hidden, null, new[] { typeof(Stream), typeof(bool) }, null)
                ?? throw new AssertionException("Missing control stream constructor.");
            var read = type.GetMethod("ReadAsync", Hidden) ?? throw new AssertionException("Missing ReadAsync.");
            using var source = new MemoryStream(wire);
            var stream = constructor.Invoke(new object[] { source, inputs[1] == "1" });
            var events = new List<string>();
            try
            {
                for (var i = 0; i < 64; i++)
                {
                    var task = (Task)(read.Invoke(stream, new object[] { CancellationToken.None }) ?? throw new AssertionException("Missing task."));
                    task.GetAwaiter().GetResult();
                    var result = task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new AssertionException("Missing event.");
                    var resultType = result.GetType();
                    var priority = resultType.GetProperty("Priority", Hidden)?.GetValue(result);
                    events.Add(Invariant((long)(resultType.GetProperty("Type", Hidden)?.GetValue(result) ?? 0L)) + ":"
                        + Invariant((long)(resultType.GetProperty("Identifier", Hidden)?.GetValue(result) ?? 0L)) + ":"
                        + (resultType.GetProperty("PriorityFieldValue", Hidden)?.GetValue(result) ?? NullMarker) + ":"
                        + (priority == null ? NullMarker : "u=" + priority.GetType().GetProperty("Urgency", Hidden)?.GetValue(priority) + ",i=" + priority.GetType().GetProperty("Incremental", Hidden)?.GetValue(priority)));
                }

                return "events:" + string.Join(";", events) + "|unbounded";
            }
            catch (IOException error)
            {
                return "events:" + string.Join(";", events) + "|" + Describe(error);
            }
            catch (InvalidDataException error)
            {
                return "events:" + string.Join(";", events) + "|" + Describe(error);
            }
            catch (InvalidOperationException error)
            {
                return "events:" + string.Join(";", events) + "|" + Describe(error);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                return "events:" + string.Join(";", events) + "|" + Describe(error.InnerException);
            }
        }

        // URL-encoded data: a query or form body, whether flags group under the null
        // key and whether the result is mutable.
        private static string[] GenerateUrlEncodedCase(Rng rng)
        {
            var text = new StringBuilder();
            if (rng.Chance(30)) text.Append(rng.Pick("?", "?", "?", "??", "?&"));
            var pairs = rng.Next(6);
            for (var p = 0; p < pairs; p++)
            {
                if (p > 0) text.Append(rng.Pick("&", "&", "&", "&", "&&", "&&&", ";"));
                text.Append(UrlEncodedPair(rng));
            }

            if (rng.Chance(15)) text.Append(rng.Pick("&", "&&", "=", "&="));
            return new[] { text.ToString(), rng.Chance(50) ? "1" : "0", rng.Chance(75) ? "1" : "0" };
        }

        private static string UrlEncodedPair(Rng rng)
        {
            var key = rng.Pick("a", "a", "a", "key", "key", "na%6De", "item[0]", "item[1]", "item[]", "[x]", "a[b][c]", "k%5B0%5D", "caf%C3%A9", "%E2%82%AC",
                "%zz", "%2", "%", "+plus+", "sp ace", "k=v", "é", "€", "", "A", "KEY", "x.y", "n-m", "a%26b");
            var value = rng.Pick("1", "2", "hello+world", "Z%C3%BCrich", "%2B%26%3D", "a=b", "", "x y", "%", "%4", "%C3", "%80", "%41%42", "true", "é", "\u0001",
                "v\u0000w", "long" + new string('v', 40), "a&", "=", "?", "%u0041", "%E2%82", "%F0%9F%98%80");
            return rng.Next(10) switch
            {
                0 => key,
                1 => key + "=",
                2 => "=" + value,
                3 => key + "==" + value,
                4 => key + "=" + value + "=" + value,
                _ => key + "=" + value,
            };
        }

        private static string EvaluateUrlEncoded(string[] inputs)
        {
            var data = UrlEncodedDataParser.Parse(inputs[0], inputs[1] == "1", inputs[2] == "1");
            var text = new StringBuilder("count:").Append(Invariant(data.Count));
            foreach (var key in data.AllKeys)
            {
                var values = data.GetValues(key);
                text.Append('|').Append(key ?? NullMarker).Append('=').Append(values == null ? NullMarker : string.Join(",", values.Select(v => "[" + v + "]")));
            }

            bool mutable;
            try
            {
                data.Set("probe", "x");
                mutable = true;
            }
            catch (NotSupportedException)
            {
                mutable = false;
            }

            return text.Append("|mutable:").Append(mutable ? "1" : "0").ToString();
        }

        // Request content codings: the Content-Encoding value, whether decoding is enabled
        // and a short body encoded with the known codings in the order listed.
        private static string[] GenerateCodingCase(Rng rng)
        {
            var codings = new List<string>();
            var count = rng.Pick(0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 8, 9);
            for (var c = 0; c < count; c++) codings.Add(rng.Pick("gzip", "gzip", "deflate", "deflate", "br", "identity"));
            if (rng.Chance(15)) codings.Insert(rng.Next(codings.Count + 1), rng.Pick("compress", "x-gzip", "gzip;q=1", "zstd", "gz ip", "GZIP2", "deflate\u0000", "identity2"));
            var text = new StringBuilder();
            for (var c = 0; c < codings.Count; c++)
            {
                if (c > 0) text.Append(rng.Pick(", ", ",", " , ", ",,", ",\t", " ,"));
                else if (rng.Chance(20)) text.Append(rng.Pick(" ", "\t", ","));
                text.Append(rng.Chance(20) ? codings[c].ToUpperInvariant() : codings[c]);
                if (rng.Chance(10)) text.Append(rng.Pick(" ", "\t", "  "));
            }

            if (rng.Chance(10)) text.Append(rng.Pick(",", " ,", ", ", ",,"));
            var payload = Random(rng, rng.Pick(0, 1, 5, 16, 24));
            return new[] { text.ToString(), rng.Chance(75) ? "1" : "0", Hex(EncodeCodings(payload, codings)) };
        }

        private static byte[] EncodeCodings(byte[] body, List<string> codings)
        {
            foreach (var coding in codings)
            {
                if (coding is not ("gzip" or "deflate" or "br")) continue;
                using var output = new MemoryStream();
                using (Stream encoder = coding == "gzip" ? new GZipStream(output, CompressionMode.Compress, true)
                    : coding == "deflate" ? new DeflateStream(output, CompressionMode.Compress, true)
                    : new BrotliStream(output, CompressionMode.Compress, true))
                {
                    encoder.Write(body, 0, body.Length);
                }

                body = output.ToArray();
            }

            return body;
        }

        private static string EvaluateCoding(string[] inputs)
        {
            var type = Internal("EmbedIO.Internal.RequestCodingChain");
            var method = type.GetMethod("TryOpen", Hidden) ?? throw new AssertionException("Missing TryOpen.");
            using var source = new MemoryStream(FromHex(inputs[2]), false);
            var arguments = new object?[] { source, inputs[0], inputs[1] == "1", null };
            bool opened;
            try
            {
                opened = (bool)(method.Invoke(null, arguments) ?? throw new AssertionException("Missing open result."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                return Describe(error.InnerException);
            }

            if (!opened) return "false:" + (arguments[3] == null ? NullMarker : "stream");
            if (ReferenceEquals(arguments[3], source)) return "source";
            using var decoded = (Stream)(arguments[3] ?? throw new AssertionException("Missing decoded stream."));
            using var body = new MemoryStream();
            var buffer = new byte[7];
            try
            {
                int read;
                while ((read = decoded.Read(buffer, 0, buffer.Length)) != 0) body.Write(buffer, 0, read);
                return "ok:" + Hex(body.ToArray());
            }
            catch (HttpException error)
            {
                return Describe(error) + "|read:" + Hex(body.ToArray());
            }
            catch (IOException error)
            {
                return Describe(error) + "|read:" + Hex(body.ToArray());
            }
            catch (InvalidDataException error)
            {
                return Describe(error) + "|read:" + Hex(body.ToArray());
            }
        }

        private static void Frame(List<byte> wire, long type, byte[] payload)
        {
            wire.AddRange(Varint(type));
            wire.AddRange(Varint(payload.Length));
            wire.AddRange(payload);
        }

        private static byte[] Varint(long value)
        {
            if (value < 64) return new[] { (byte)value };
            if (value < 16384) return new[] { (byte)(0x40 | (value >> 8)), (byte)value };
            if (value < 1073741824) return new[] { (byte)(0x80 | (value >> 24)), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
            return new[] { (byte)(0xC0 | (value >> 56)), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static byte[] Random(Rng rng, int count)
        {
            var bytes = new byte[count];
            for (var i = 0; i < count; i++) bytes[i] = (byte)rng.Next(256);
            return bytes;
        }

        private static void Mutate(Rng rng, List<byte> bytes)
        {
            var index = rng.Next(bytes.Count);
            switch (rng.Next(3))
            {
                case 0: bytes.RemoveAt(index); break;
                case 1: bytes[index] = rng.Pick((byte)0, (byte)9, (byte)10, (byte)13, (byte)32, (byte)'x', (byte)'{', (byte)127, (byte)255); break;
                default: bytes.RemoveRange(index, bytes.Count - index); break;
            }
        }

        private static void Ascii(List<byte> wire, string text) => wire.AddRange(Encoding.Latin1.GetBytes(text));

        private static Type Internal(string name)
            => typeof(WebServer).Assembly.GetType(name, true) ?? throw new AssertionException("Missing type " + name);

        // Our own parsers throw with fixed messages; BCL exceptions carry runtime-specific text.
        private static string Describe(Exception error)
        {
            var name = error.GetType().Name;
            if (error is HttpException http) return name + ":" + Invariant(http.StatusCode);
            if (error.GetType().Name == "Http3ProtocolException")
                return name + "(0x" + ((long)(error.GetType().GetProperty("ErrorCode")?.GetValue(error) ?? 0L)).ToString("x", CultureInfo.InvariantCulture) + "):" + error.Message;
            if (error is InvalidDataException || error is EndOfStreamException) return name + ":" + error.Message;
            if (error is ArgumentException argument) return name + ":" + argument.ParamName;
            return name;
        }

        private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        private static byte[] FromHex(string text) => Convert.FromHexString(text);

        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '\t': escaped.Append("\\t"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    default:
                        if (c < ' ' || c > '~') escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else escaped.Append(c);
                        break;
                }
            }

            return escaped.ToString();
        }

        private static string Unescape(string text)
        {
            var result = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\')
                {
                    result.Append(text[i]);
                    continue;
                }

                switch (text[++i])
                {
                    case '\\': result.Append('\\'); break;
                    case 't': result.Append('\t'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 'u':
                        result.Append((char)int.Parse(text.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new AssertionException("Invalid corpus escape.");
                }
            }

            return result.ToString();
        }

        // xorshift64*: identical sequences on every runtime and platform.
        private sealed class Rng
        {
            private ulong _state;

            internal Rng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

            internal int Next(int maximum)
            {
                _state ^= _state >> 12;
                _state ^= _state << 25;
                _state ^= _state >> 27;
                return (int)((_state * 0x2545F4914F6CDD1DUL >> 33) % (ulong)maximum);
            }

            internal bool Chance(int percent) => Next(100) < percent;

            internal T Pick<T>(params T[] items) => items[Next(items.Length)];
        }

        // Only the members the range selectors read are populated.
        private sealed class RangeRequest : IHttpRequest
        {
            internal RangeRequest(string method, string range, string? ifRange)
            {
                HttpMethod = method;
                Headers = new NameValueCollection { [HttpHeaderNames.Range] = range };
                if (ifRange != null) Headers[HttpHeaderNames.IfRange] = ifRange;
            }

            public NameValueCollection Headers { get; }
            public string HttpMethod { get; }
            public ICookieCollection Cookies => throw new NotSupportedException();
            public Version ProtocolVersion => HttpVersion.Version11;
            public bool KeepAlive => true;
            public string RawTarget => "/";
            public NameValueCollection QueryString => throw new NotSupportedException();
            public HttpVerbs HttpVerb => HttpVerbs.Any;
            public Uri Url => new("http://localhost/");
            public bool HasEntityBody => false;
            public Stream InputStream => Stream.Null;
            public Encoding ContentEncoding => Encoding.UTF8;
            public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 1);
            public bool IsLocal => true;
            public bool IsSecureConnection => false;
            public string? UserAgent => null;
            public bool IsWebSocketRequest => false;
            public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 2);
            public string? ContentType => null;
            public long ContentLength64 => 0;
            public bool IsAuthenticated => false;
            public Uri? UrlReferrer => null;
        }
    }
}
