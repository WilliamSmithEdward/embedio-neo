using System.Buffers.Binary;

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
            Mutate([0, 1, 2, 3]);
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, 4096)];
            var type = data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : (ushort)0x1234;

            if (type == 57) type = 58;

            var parameters = data.ToArray();
            var valid = Create(parameters, (type, []));
            Require(
                TlsClientHello
                    .ReadTransportParameters(valid)
                    .AsSpan()
                    .SequenceEqual(data),
                "fuzz-parameters"
            );
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
