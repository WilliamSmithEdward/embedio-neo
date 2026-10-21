using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.Quic
{
    internal static class TlsHelloTests
    {
        internal static void Run()
        {
            var parameters = new byte[] { 1, 2, 3 };
            var hello = Create(parameters);
            Require(
                TlsClientHello
                    .ReadTransportParameters(hello)
                    .AsSpan()
                    .SequenceEqual(parameters),
                "parameters"
            );
            Require(TlsClientHello.ReadTransportParameters(Create([])).Length == 0, "empty-parameters");

            for (var length = 0; length < hello.Length; length++) Reject(hello.AsSpan(0, length), 50);

            Reject([.. hello, 0], 50);
            var altered = (byte[])hello.Clone();
            altered[0] = 2;
            Reject(altered, 50);
            altered = (byte[])hello.Clone();
            altered[5] = 2;
            Reject(altered, 70);
            altered = (byte[])hello.Clone();
            altered[38] = 33;
            Reject(altered, 50);
            altered = (byte[])hello.Clone();
            altered[40] = 3;
            Reject(altered, 50);
            altered = (byte[])hello.Clone();
            altered[44] = 1;
            Reject(altered, 47);
            altered = (byte[])hello.Clone();
            altered[48] = 58;
            Reject(altered, 109);
            Reject(Create(parameters, (57, parameters)), 47);
            Reject(Create(parameters, (0x1234, []), (0x1234, [1])), 47);
            Reject(Create(new byte[4097]), 47);
            Require(TlsClientHello.ReadTransportParameters(Create(new byte[4096])).Length == 4096, "parameter-limit");
            var trailing = Create(parameters, (0x1234, new byte[65482]));
            Reject(trailing, 50);
            CheckServerNames(parameters);
            Mutate([0, 1, 2, 3]);
        }

        private static void CheckServerNames(byte[] parameters)
        {
            Require(TlsClientHello.Read(Create(parameters)).ServerName == null, "missing-sni");

            foreach (var name in new[]
            {
                "localhost",
                "EXAMPLE.test",
                "xn--bcher-kva.example",
                "internal_name.local",
                "localhost.",
                string.Join(".", Enumerable.Repeat(new string('a', 63), 4))
            })
            {
                var info = TlsClientHello.Read(Create(parameters, (0, ServerName(Encoding.ASCII.GetBytes(name)))));
                Require(
                    info.ServerName == name
                        && info.TransportParameters
                            .AsSpan()
                            .SequenceEqual(parameters),
                    "sni-wire-spelling"
                );
            }

            foreach (var bytes in new byte[][]
            {
                [],
                [0],
                [10],
                [127],
                [128],
                new byte[256]
            })
                Reject(Create(parameters, (0, ServerName(bytes))), 47);

            foreach (var name in new[]
            {
                "../leaf",
                "a/b",
                "a\\b",
                "x:443",
                "a..b",
                ".a",
                new string('a', 64)
            })
                Reject(Create(parameters, (0, ServerName(Encoding.ASCII.GetBytes(name)))), 47);

            Reject(Create(parameters, (0, [])), 50);
            Reject(Create(parameters, (0, [0, 0])), 50);
            Reject(Create(parameters, (0, [0, 4, 0, 0, 2, 65])), 50);
            Reject(Create(parameters, (0, [0, 4, 1, 0, 1, 65])), 47);
            Reject(Create(parameters, (0, [0, 8, 0, 0, 1, 65, 0, 0, 1, 66])), 47);
        }

        internal static byte[] ServerName(byte[] name)
        {
            using var value = new MemoryStream();
            Length(value, checked((ushort)(name.Length + 3)));
            value.WriteByte(0);
            Length(value, checked((ushort)name.Length));
            value.Write(name);
            return value.ToArray();
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, 4096)];
            var type = data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : (ushort)0x1234;

            if (type is 0 or 57) type = 58;

            var parameters = data.ToArray();
            var valid = Create(parameters, (type, []));
            Require(
                TlsClientHello
                    .ReadTransportParameters(valid)
                    .AsSpan()
                    .SequenceEqual(data),
                "fuzz-parameters"
            );
            var name = "x" + Convert.ToHexString(data[..Math.Min(data.Length, 16)]) + ".example";
            var named = Create(parameters, (0, ServerName(Encoding.ASCII.GetBytes(name))));
            var info = TlsClientHello.Read(named);
            Require(
                info.ServerName == name
                    && info.TransportParameters
                        .AsSpan()
                        .SequenceEqual(data),
                "fuzz-sni"
            );

            if (data.Length != 0)
            {
                named[data[0] % named.Length] ^= data[^1];
                Check(named);
            }

            Reject(Create(parameters, (type, []), (type, [])), 47);
            Check(data);

            if (data.Length != 0)
            {
                valid[data[0] % valid.Length] ^= data[^1];
                Check(valid);
            }
        }

        private static void Check(ReadOnlySpan<byte> bytes)
        {
            try
            {
                var parameters = TlsClientHello.ReadTransportParameters(bytes);
                Require(parameters.Length <= 4096, "fuzz-parameter-bound");
            }

            catch (TransportException error)
            {
                Require(error.ErrorCode is 0x12f or 0x132 or 0x146 or 0x16d, "fuzz-alert-code");
            }
        }

        internal static byte[] NativeHello(
            byte[] parameters,
            string protocol,
            params (ushort Type, byte[] Value)[] extras
        ) => NativeHello(parameters, protocol, false, extras);

        internal static byte[] NativeHello(
            byte[] parameters,
            string protocol,
            bool prime256,
            params (ushort Type, byte[] Value)[] extras
        )
        {
            var group = prime256 ? (byte)23 : (byte)29;
            byte[] point;

            if (prime256)
            {
                using var agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var key = agreement.ExportParameters(false).Q;
                point =
                [
                    4,
                    ..key.X ?? throw new CryptographicException("Missing public X coordinate."),
                    ..key.Y ?? throw new CryptographicException("Missing public Y coordinate.")
                ];
            }

            else
            {
                point = new byte[32];
                point[0] = 9;
            }

            byte[] share =
            [
                0,
                checked((byte)(point.Length + 4)),
                0,
                group,
                0,
                checked((byte)point.Length),
                .. point
            ];
            var name = Encoding.ASCII.GetBytes(protocol);
            byte[] alpn =
            [
                0,
                checked((byte)(name.Length + 1)),
                checked((byte)name.Length),
                .. name
            ];
            (ushort Type, byte[] Value)[] extensions =
            [
                (43, [2, 3, 4]),
                (10, [0, 2, 0, group]),
                (13, [0, 4, 8, 4, 4, 3]),
                (51, share),
                (16, alpn),
                .. extras
            ];
            return Create(parameters, extensions);
        }

        internal static byte[] Create(byte[] parameters, params (ushort Type, byte[] Value)[] extras)
        {
            using var extensions = new MemoryStream();
            Extension(extensions, 57, parameters);

            foreach (var (type, value) in extras) Extension(extensions, type, value);

            using var body = new MemoryStream();
            body.Write([3, 3]);
            body.Write(new byte[32]);
            body.Write([0, 0, 2, 0x13, 1, 1, 0]);
            Length(body, checked((ushort)extensions.Length));
            extensions.Position = 0;
            extensions.CopyTo(body);
            var count = checked((int)body.Length);
            byte[] result =
            [
                1,
                (byte)(count >> 16),
                (byte)(count >> 8),
                (byte)count,
                .. body.ToArray()
            ];
            return result;
        }

        private static void Extension(Stream output, ushort type, byte[] bytes)
        {
            Length(output, type);
            Length(output, checked((ushort)bytes.Length));
            output.Write(bytes);
        }

        private static void Length(Stream output, ushort size)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, size);
            output.Write(bytes);
        }

        private static void Reject(ReadOnlySpan<byte> bytes, byte alert)
        {
            try
            {
                TlsClientHello.ReadTransportParameters(bytes);
            }

            catch (TransportException error)
            {
                Require(error.ErrorCode == 0x100UL + alert, "expected-alert");
                return;
            }

            throw new InvalidOperationException("TLS ClientHello was incorrectly accepted.");
        }

        private static void Require(bool condition, string invariant) =>
            QuicFuzzChecks.Require(condition, "tls-hello-" + invariant);
    }
}
