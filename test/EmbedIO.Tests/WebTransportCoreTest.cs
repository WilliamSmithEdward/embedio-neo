using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Exercises the internal WebTransport framing/session core against the rules
    // of draft-ietf-webtrans-http3-16 without any QUIC transport. The types are
    // internal, so the fixture drives them through reflection.
    [TestFixture]
    public class WebTransportCoreTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private const string Namespace = "EmbedIO.Net.Internal.WebTransport.";
        private const long CloseType = 0x2843, DrainType = 0x78ae, MaxData = 0x190B4D3D, MaxStreamsBidi = 0x190B4D3F, MaxStreamsUni = 0x190B4D40;
        private const long DataBlocked = 0x190B4D41, StreamsBlockedBidi = 0x190B4D43, StreamsBlockedUni = 0x190B4D44, MaxStreamData = 0x190B4D3E;
        private const long SessionGone = 0x170d7b68, BufferedRejected = 0x3994bd84, FlowControlError = 0x045d4487;
        private const long H3IdError = 0x108, H3SettingsError = 0x109, H3MessageError = 0x10e, H3DatagramError = 0x33;

        private static Type T(string name) => typeof(WebServer).Assembly.GetType(Namespace + name, true) ?? throw new AssertionException("Missing " + name);

        private static object New(string name, params object?[] arguments)
        {
            var constructor = T(name).GetConstructors(Hidden).Single(c => c.GetParameters().Length == arguments.Length);
            try { return constructor.Invoke(arguments); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }

        private static object? Invoke(Type type, object? target, string name, object?[] arguments)
        {
            var method = type.GetMethods(Hidden).Single(m => m.Name == name && m.GetParameters().Length == arguments.Length);
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }

        private static object? Call(object target, string name, params object?[] arguments) => Invoke(target.GetType(), target, name, arguments);
        private static object? Static(string type, string name, params object?[] arguments) => Invoke(T(type), null, name, arguments);
        private static object Must(string type, string name, params object?[] arguments) => Static(type, name, arguments) ?? throw new AssertionException("Missing result of " + name);
        private static object Get(object target, string name, params object?[] arguments) => Call(target, name, arguments) ?? throw new AssertionException("Missing result of " + name);
        private static TValue Prop<TValue>(object target, string name)
            => (TValue)(target.GetType().GetProperty(name, Hidden)?.GetValue(target) ?? throw new AssertionException("Missing property " + name));
        private static object? PropOrNull(object target, string name) => target.GetType().GetProperty(name, Hidden)?.GetValue(target);
        private static void Set(object target, string name, object value) => (target.GetType().GetProperty(name, Hidden) ?? throw new AssertionException("Missing " + name)).SetValue(target, value);

        // Asserts an internal exception by name and its ErrorCode property.
        private static void AssertError(string typeName, long code, Action action)
        {
            var error = Assert.Catch(() => action()) ?? throw new AssertionException("Missing error.");
            Assert.That(error.GetType().Name, Is.EqualTo(typeName), error.Message);
            Assert.That(Prop<long>(error, "ErrorCode"), Is.EqualTo(code), error.Message);
        }

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private sealed class Recorder
        {
            public readonly List<(object Session, object Stream)> Streams = new();
            public readonly List<(object Session, byte[] Data)> Datagrams = new();
            public void OnStream(object session, object stream) { lock (Streams) Streams.Add((session, stream)); }
            public void OnDatagram(object session, byte[] bytes, int offset, int count)
            {
                var copy = new byte[count];
                Array.Copy(bytes, offset, copy, 0, count);
                lock (Datagrams) Datagrams.Add((session, copy));
            }
            public Delegate StreamHandler => Delegate.CreateDelegate(T("WebTransportStreamHandler"), this, typeof(Recorder).GetMethod(nameof(OnStream)) ?? throw new AssertionException("Missing handler."));
            public Delegate DatagramHandler => Delegate.CreateDelegate(T("WebTransportDatagramHandler"), this, typeof(Recorder).GetMethod(nameof(OnDatagram)) ?? throw new AssertionException("Missing handler."));
        }

        private static object Handle(long id, bool bidirectional, List<(long Id, long Code)>? aborts = null)
            => New("WebTransportStreamHandle", id, bidirectional, new Action<long>(code => aborts?.Add((id, code))));
        private static object Settings(bool enabled, long data = 0, long uni = 0, long bidi = 0) => Must("WebTransportSettings", "Local", enabled, data, uni, bidi);
        private static object Capsule(long type, byte[] payload) => New("WebTransportCapsule", type, payload);
        private static object Limit(long type, long value) => Capsule(type, ((byte[])(Static("WebTransportCapsuleCodec", "EncodeLimit", type, value) ?? throw new AssertionException("Missing capsule."))).Skip(Skip(type)).ToArray());
        private static int Skip(long type) => type < 64 ? 2 : type < 16384 ? 3 : type < 1073741824 ? 5 : 9;

        private static object Session(long id, bool flowControl, object? local = null, object? peer = null, Recorder? recorder = null, object? limits = null)
            => New("WebTransportSession", id, flowControl, local ?? Settings(true, 1000, 2, 3), peer ?? Settings(true, 500, 1, 2), limits ?? New("WebTransportSessionLimits"),
                (recorder ?? new Recorder()).StreamHandler, (recorder ?? new Recorder()).DatagramHandler);

        private static object Registry(object local, object peer, Recorder recorder, bool transportParameters = true, object? limits = null)
            => New("WebTransportSessionRegistry", local, peer, transportParameters, limits ?? New("WebTransportRegistryLimits"), recorder.StreamHandler, recorder.DatagramHandler);

        // --- Protocol identifiers, headers and error code mapping (sections 4, 4.4, 4.5, 9) ---

        [Test]
        public void ConstantsMatchTheDraftRegistrations()
        {
            var protocol = T("WebTransportProtocol");
            object Constant(string name) => protocol.GetField(name, Hidden)?.GetRawConstantValue() ?? throw new AssertionException(name);
            Assert.That(Constant("ProtocolToken"), Is.EqualTo("webtransport-h3"));
            Assert.That(Constant("SettingEnabled"), Is.EqualTo(0x2c7cf000L));
            Assert.That(Constant("SettingInitialMaxData"), Is.EqualTo(0x2b61L));
            Assert.That(Constant("SettingInitialMaxStreamsUnidirectional"), Is.EqualTo(0x2b64L));
            Assert.That(Constant("SettingInitialMaxStreamsBidirectional"), Is.EqualTo(0x2b65L));
            Assert.That(Constant("UnidirectionalStreamType"), Is.EqualTo(0x54L));
            Assert.That(Constant("BidirectionalStreamSignal"), Is.EqualTo(0x41L));
            Assert.That(Constant("CloseSessionCapsule"), Is.EqualTo(CloseType));
            Assert.That(Constant("DrainSessionCapsule"), Is.EqualTo(DrainType));
            Assert.That(Constant("SessionGone"), Is.EqualTo(SessionGone));
            Assert.That(Constant("BufferedStreamRejected"), Is.EqualTo(BufferedRejected));
            Assert.That(Constant("FlowControlError"), Is.EqualTo(FlowControlError));
            Assert.That(Constant("AlpnError"), Is.EqualTo(0x0817b3ddL));
            Assert.That(Constant("RequirementsNotMet"), Is.EqualTo(0x212c0d48L));
            Assert.That(Constant("MaximumCloseMessageBytes"), Is.EqualTo(1024));
        }

        [TestCase(0u, 0x52e4a40fa8dbL)]
        [TestCase(1u, 0x52e4a40fa8dcL)]
        [TestCase(0x1du, 0x52e4a40fa8f8L)]
        [TestCase(0x1eu, 0x52e4a40fa8faL)]
        [TestCase(0xffffffffu, 0x52e5ac983162L)]
        public void ApplicationErrorCodesMapIntoTheReservedHttp3Range(uint application, long expected)
        {
            Assert.That(Static("WebTransportProtocol", "ToHttp3ErrorCode", application), Is.EqualTo(expected));
            var arguments = new object?[] { expected, 0u };
            Assert.That(Static("WebTransportProtocol", "TryToApplicationErrorCode", arguments), Is.True);
            Assert.That(arguments[1], Is.EqualTo(application));
        }

        [Test]
        public void MappedCodesSkipReservedGreaseCodepointsAndStayMonotonic()
        {
            long previous = -1;
            for (uint n = 0; n < 5000; n++)
            {
                var h = (long)Must("WebTransportProtocol", "ToHttp3ErrorCode", n);
                Assert.That((h - 0x21) % 0x1f, Is.Not.Zero, "n=" + n);
                Assert.That(h, Is.GreaterThan(previous));
                previous = h;
            }
        }

        [TestCase(0x52e4a40fa8daL)]
        [TestCase(0x52e5ac983163L)]
        [TestCase(0x52e4a40fa8f9L)]
        [TestCase(0x10eL)]
        public void CodesOutsideOrOnReservedPointsDoNotMap(long http3)
        {
            var arguments = new object?[] { http3, 0u };
            Assert.That(Static("WebTransportProtocol", "TryToApplicationErrorCode", arguments), Is.False);
        }

        [TestCase(0L, true)]
        [TestCase(4L, true)]
        [TestCase(1L, false)]
        [TestCase(2L, false)]
        [TestCase(3L, false)]
        [TestCase(-4L, false)]
        [TestCase((1L << 62) - 4, true)]
        [TestCase(1L << 62, false)]
        public void SessionIdentifiersAreClientInitiatedBidirectionalStreamIds(long id, bool valid)
            => Assert.That(Static("WebTransportProtocol", "IsSessionId", id), Is.EqualTo(valid));

        [TestCase("08", 8L, 1)]
        [TestCase("4008", 8L, 2)]
        [TestCase("80000008", 8L, 4)]
        [TestCase("c000000000000008", 8L, 8)]
        public void SessionIdFollowsTheStreamTypeInAnyIntegerWidth(string hex, long id, int consumed)
        {
            var arguments = new object?[] { Hex(hex + "ff"), 0, consumed + 1, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadSessionId", arguments)?.ToString(), Is.EqualTo("Complete"));
            Assert.That((arguments[3], arguments[4]), Is.EqualTo((id, consumed)));
        }

        [TestCase("")]
        [TestCase("40")]
        [TestCase("c0000000")]
        public void SplitSessionIdAsksForMoreBytesWithoutFailing(string hex)
        {
            var bytes = Hex(hex);
            var arguments = new object?[] { bytes, 0, bytes.Length, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadSessionId", arguments)?.ToString(), Is.EqualTo("NeedMoreData"));
        }

        [TestCase("01")]
        [TestCase("02")]
        [TestCase("03")]
        public void SessionIdNamingAnotherStreamKindIsAConnectionIdError(string hex)
            => AssertError("Http3ProtocolException", H3IdError, () => Static("WebTransportProtocol", "TryReadSessionId", new object?[] { Hex(hex), 0, 1, 0L, 0 }));

        [Test]
        public void BidirectionalSignalSelectsWebTransportAndOtherSignalsLeaveTheStreamAlone()
        {
            var arguments = new object?[] { Hex("40410c"), 0, 3, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadBidirectionalHeader", arguments)?.ToString(), Is.EqualTo("Complete"));
            Assert.That((arguments[3], arguments[4]), Is.EqualTo((12L, 3)));
            // A HEADERS frame type (0x01) is an ordinary request stream.
            arguments = new object?[] { Hex("0100"), 0, 2, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadBidirectionalHeader", arguments)?.ToString(), Is.EqualTo("NotWebTransport"));
            arguments = new object?[] { Hex("41"), 0, 1, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadBidirectionalHeader", arguments)?.ToString(), Is.EqualTo("NeedMoreData"));
            // A non-minimal signal encoding is still the signal.
            arguments = new object?[] { Hex("8000004104"), 0, 5, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadBidirectionalHeader", arguments)?.ToString(), Is.EqualTo("Complete"));
            Assert.That((arguments[3], arguments[4]), Is.EqualTo((4L, 5)));
        }

        [Test]
        public void ServerStreamHeadersRoundTripThroughTheReaders()
        {
            var bytes = new byte[16];
            var count = (int)Must("WebTransportProtocol", "WriteUnidirectionalHeader", bytes, 0, 16384L);
            Assert.That(bytes.Take(count).ToArray(), Is.EqualTo(Hex("405480004000")));
            count = (int)Must("WebTransportProtocol", "WriteBidirectionalHeader", bytes, 0, 8L);
            Assert.That(bytes.Take(count).ToArray(), Is.EqualTo(Hex("404108")));
            var arguments = new object?[] { bytes, 0, count, 0L, 0 };
            Assert.That(Static("WebTransportProtocol", "TryReadBidirectionalHeader", arguments)?.ToString(), Is.EqualTo("Complete"));
            Assert.That(arguments[3], Is.EqualTo(8L));
            Assert.Throws<ArgumentOutOfRangeException>(() => Static("WebTransportProtocol", "WriteUnidirectionalHeader", bytes, 0, 2L));
        }

        [Test]
        public void DatagramQuarterStreamIdNamesTheConnectStreamAndBorrowsThePayload()
        {
            var packet = Hex("ff03616263");
            var arguments = new object?[] { packet, 1, 4, 0 };
            Assert.That(Static("WebTransportProtocol", "ReadDatagramSessionId", arguments), Is.EqualTo(12L));
            Assert.That(arguments[3], Is.EqualTo(2));
            var header = new byte[8];
            var count = (int)Must("WebTransportProtocol", "WriteDatagramHeader", header, 0, 12L);
            Assert.That(header.Take(count).ToArray(), Is.EqualTo(Hex("03")));
            Assert.Throws<ArgumentOutOfRangeException>(() => Static("WebTransportProtocol", "WriteDatagramHeader", header, 0, 2L));
        }

        [TestCase("")]
        [TestCase("c0")]
        [TestCase("ffffffffffffffff")]
        public void MalformedDatagramHeadersAreDatagramErrors(string hex)
        {
            var packet = Hex(hex);
            AssertError("Http3ProtocolException", H3DatagramError, () => Static("WebTransportProtocol", "ReadDatagramSessionId", new object?[] { packet, 0, packet.Length, 0 }));
        }

        // --- Settings (sections 3.1, 5.1, 5.5, 9.2) ---

        [Test]
        public void PeerSettingsRequireExtendedConnectDatagramsAndEnabled()
        {
            var settings = Must("WebTransportSettings", "Parse", Hex("08013301"), 1024);
            Assert.That(Prop<bool>(settings, "SettingsRequirementsMet"), Is.False);
            settings = Must("WebTransportSettings", "Parse", Hex("08013301ac7cf00001"), 1024);
            Assert.That(Prop<bool>(settings, "SettingsRequirementsMet"), Is.True);
            Assert.That(Prop<bool>(settings, "DeclaresFlowControl"), Is.False);
            settings = Must("WebTransportSettings", "Parse", Hex("08013301ac7cf000016b614000"), 1024);
            Assert.That(Prop<long>(settings, "InitialMaxData"), Is.EqualTo(0));
            settings = Must("WebTransportSettings", "Parse", Hex("6b61400a6b64026b6503"), 1024);
            Assert.That((Prop<long>(settings, "InitialMaxData"), Prop<long>(settings, "InitialMaxStreamsUnidirectional"), Prop<long>(settings, "InitialMaxStreamsBidirectional")),
                Is.EqualTo((10L, 2L, 3L)));
            Assert.That(Prop<bool>(settings, "DeclaresFlowControl"), Is.True);
        }

        [TestCase("ac7cf00002", H3SettingsError)]
        [TestCase("ac7cf00001ac7cf00001", H3SettingsError)]
        [TestCase("6b64d000000000000001", H3SettingsError)]
        [TestCase("6b", 0x106)]
        [TestCase("ac7cf000", 0x106)]
        public void InvalidSettingsAreConnectionErrors(string hex, long code)
            => AssertError("Http3ProtocolException", code, () => Static("WebTransportSettings", "Parse", Hex(hex), 1024));

        [Test]
        public void TooManySettingsAreExcessiveLoad()
            => AssertError("Http3ProtocolException", 0x107, () => Static("WebTransportSettings", "Parse", Hex("080133016b6101"), 2));

        [Test]
        public void FlowControlIsEnabledOnlyWhenBothEndpointsDeclareALimit()
        {
            Assert.That(Static("WebTransportSettings", "FlowControlEnabled", Settings(true, 1), Settings(true, 0, 0, 1)), Is.True);
            Assert.That(Static("WebTransportSettings", "FlowControlEnabled", Settings(true, 1), Settings(true)), Is.False);
            Assert.That(Static("WebTransportSettings", "FlowControlEnabled", Settings(true), Settings(true, 0, 1)), Is.False);
        }

        [Test]
        public void LocalSettingsEncodeOnlyNonDefaultValues()
        {
            Assert.That(Call(Settings(false), "EncodeLocal"), Is.EqualTo(Array.Empty<byte>()));
            Assert.That(Call(Settings(true), "EncodeLocal"), Is.EqualTo(Hex("ac7cf00001")));
            var encoded = (byte[])Get(Settings(true, 65536, 0, 3), "EncodeLocal");
            var parsed = Must("WebTransportSettings", "Parse", encoded, 1024);
            Assert.That((Prop<bool>(parsed, "Enabled"), Prop<long>(parsed, "InitialMaxData"), Prop<long>(parsed, "InitialMaxStreamsBidirectional")), Is.EqualTo((true, 65536L, 3L)));
        }

        // --- Capsule payload codecs (sections 4.7, 5.6, 6) ---

        [Test]
        public void CloseCapsuleCarriesA32BitCodeAndUtf8Message()
        {
            var capsule = (byte[])Must("WebTransportCapsuleCodec", "EncodeClose", 0xdeadbeefu, "bye");
            Assert.That(capsule, Is.EqualTo(Hex("684307deadbeef627965")));
            var arguments = new object?[] { capsule.Skip(3).ToArray(), "" };
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeClose", arguments), Is.EqualTo(0xdeadbeefu));
            Assert.That(arguments[1], Is.EqualTo("bye"));
            arguments = new object?[] { Hex("00000000"), "" };
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeClose", arguments), Is.EqualTo(0u));
            Assert.That(arguments[1], Is.EqualTo(""));
        }

        [Test]
        public void CloseMessagesTruncateAtACharacterBoundaryWhenSent()
        {
            var message = new string('a', 1022) + "€";
            Assert.That(Static("WebTransportCapsuleCodec", "TruncateCloseMessage", message), Is.EqualTo(new string('a', 1022)));
            message = new string('a', 1021) + "€";
            Assert.That(Static("WebTransportCapsuleCodec", "TruncateCloseMessage", message), Is.EqualTo(message));
            var capsule = (byte[])Must("WebTransportCapsuleCodec", "EncodeClose", 1u, new string('b', 2000));
            Assert.That(capsule.Length, Is.EqualTo(2 + 2 + 4 + 1024));
        }

        [Test]
        public void ReceivedCloseMessagesOver1024BytesOrInvalidUtf8AreMessageErrors()
        {
            var tooLong = new byte[4 + 1025];
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeClose", new object?[] { tooLong, "" }));
            var exact = new byte[4 + 1024];
            for (var i = 4; i < exact.Length; i++) exact[i] = (byte)'x';
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeClose", new object?[] { exact, "" }), Is.EqualTo(0u));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeClose", new object?[] { Hex("00000000ff"), "" }));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeClose", new object?[] { Hex("00000000e282"), "" }));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeClose", new object?[] { Hex("000000"), "" }));
        }

        [Test]
        public void LimitCapsulesEncodeMinimalIntegersAndDecodeAnyWidth()
        {
            Assert.That(Static("WebTransportCapsuleCodec", "EncodeLimit", MaxData, 70000L), Is.EqualTo(Hex("990b4d3d0480011170")));
            Assert.That(Static("WebTransportCapsuleCodec", "EncodeLimit", MaxStreamsUni, 3L), Is.EqualTo(Hex("990b4d400103")));
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeLimit", MaxStreamsBidi, Hex("c000000000000003")), Is.EqualTo(3L));
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeLimit", MaxData, Hex("ffffffffffffffff")), Is.EqualTo((1L << 62) - 1));
            Assert.That(Static("WebTransportCapsuleCodec", "DecodeLimit", MaxStreamsUni, Hex("d000000000000000")), Is.EqualTo(1L << 60));
            Assert.That(Static("WebTransportCapsuleCodec", "EncodeDrain"), Is.EqualTo(Hex("800078ae00")));
        }

        [Test]
        public void StreamLimitsAbove2To60AreFlowControlErrorsAndMalformedLimitsAreMessageErrors()
        {
            AssertError("WebTransportException", FlowControlError, () => Static("WebTransportCapsuleCodec", "DecodeLimit", MaxStreamsUni, Hex("d000000000000001")));
            AssertError("WebTransportException", FlowControlError, () => Static("WebTransportCapsuleCodec", "DecodeLimit", StreamsBlockedBidi, Hex("d000000000000001")));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeLimit", MaxData, Hex("")));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeLimit", MaxData, Hex("40")));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "DecodeLimit", MaxData, Hex("0100")));
            AssertError("WebTransportException", H3MessageError, () => Static("WebTransportCapsuleCodec", "ValidateDrain", Hex("00")));
            Assert.Throws<ArgumentOutOfRangeException>(() => Static("WebTransportCapsuleCodec", "EncodeLimit", MaxStreamsUni, (1L << 60) + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Static("WebTransportCapsuleCodec", "EncodeLimit", CloseType, 1L));
        }

        // --- Capsule reader over a borrowed CONNECT stream ---

        private static async Task<object?> ReadCapsule(object reader)
        {
            var task = (Task)Get(reader, "ReadAsync", CancellationToken.None);
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        [Test]
        public async Task ReaderDeliversSessionCapsulesSkipsUnknownTypesAndReportsCleanEnd()
        {
            // Unknown type 0x2b with a 300000-byte value, then DRAIN, then CLOSE, then FIN.
            var bytes = new List<byte>(Hex("2b800493e0"));
            bytes.AddRange(new byte[300000]);
            bytes.AddRange(Hex("800078ae00"));
            bytes.AddRange(Hex("684306000000076869"));
            using var stream = new FragmentedInput(bytes.ToArray(), 7);
            var reader = New("WebTransportCapsuleReader", stream);
            var drain = await ReadCapsule(reader) ?? throw new AssertionException("Missing drain.");
            Assert.That((Prop<long>(drain, "Type"), Prop<byte[]>(drain, "Payload").Length), Is.EqualTo((DrainType, 0)));
            var close = await ReadCapsule(reader) ?? throw new AssertionException("Missing close.");
            Assert.That(Prop<long>(close, "Type"), Is.EqualTo(CloseType));
            Assert.That(Prop<byte[]>(close, "Payload"), Is.EqualTo(Hex("000000076869")));
            Assert.That(await ReadCapsule(reader), Is.Null);
            Assert.That(stream.CanRead, Is.True, "The CONNECT stream stays caller-owned.");
        }

        [Test]
        public async Task ReaderRejectsOversizedSessionPayloadsBeforeAllocatingThem()
        {
            using var stream = new MemoryStream(Hex("6843ffffffffffffffff00000000"));
            var reader = New("WebTransportCapsuleReader", stream);
            AssertError("WebTransportException", H3MessageError, () => ReadCapsule(reader).GetAwaiter().GetResult());
            await Task.CompletedTask;
        }

        [TestCase("6843")]
        [TestCase("684306000000")]
        [TestCase("78")]
        public async Task TruncatedCapsulesAreMessageErrors(string hex)
        {
            using var stream = new FragmentedInput(Hex(hex), 1);
            var reader = New("WebTransportCapsuleReader", stream);
            AssertError("WebTransportException", H3MessageError, () => ReadCapsule(reader).GetAwaiter().GetResult());
            await Task.CompletedTask;
        }

        // --- Session lifecycle (sections 3.2, 4.6, 4.7, 6) ---

        [Test]
        public void SessionDeliversBufferedStreamsAndDatagramsOnEstablishment()
        {
            var recorder = new Recorder();
            var aborts = new List<(long, long)>();
            var session = Session(4, false, recorder: recorder);
            Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Pending"));
            Assert.That(Call(session, "AttachIncomingStream", Handle(2, false, aborts)), Is.True);
            Assert.That(Call(session, "AcceptDatagram", new byte[] { 9, 8, 7 }, 1, 2), Is.True);
            Assert.That(recorder.Streams, Is.Empty);
            Assert.That((Prop<int>(session, "BufferedStreamCount"), Prop<int>(session, "BufferedDatagramCount")), Is.EqualTo((1, 1)));
            Call(session, "Establish");
            Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Established"));
            Assert.That(recorder.Streams.Select(s => Prop<long>(s.Stream, "Id")), Is.EqualTo(new[] { 2L }));
            Assert.That(recorder.Datagrams.Single().Data, Is.EqualTo(new byte[] { 8, 7 }));
            Assert.That(aborts, Is.Empty);
            Assert.That(Prop<int>(session, "ActiveStreamCount"), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => Call(session, "Establish"));
        }

        [Test]
        public void PendingBuffersAreBoundedAndExcessStreamsAreRejectedWithTheDraftCode()
        {
            var aborts = new List<(long Id, long Code)>();
            var limits = New("WebTransportSessionLimits");
            Set(limits, "MaximumBufferedStreams", 2);
            Set(limits, "MaximumBufferedDatagrams", 1);
            Set(limits, "MaximumBufferedDatagramBytes", 4);
            var session = Session(0, false, limits: limits);
            Assert.That(Call(session, "AttachIncomingStream", Handle(2, false, aborts)), Is.True);
            Assert.That(Call(session, "AttachIncomingStream", Handle(6, false, aborts)), Is.True);
            Assert.That(Call(session, "AttachIncomingStream", Handle(10, false, aborts)), Is.False);
            Assert.That(aborts, Is.EqualTo(new[] { (10L, BufferedRejected) }));
            Assert.That(Call(session, "AcceptDatagram", new byte[5], 0, 5), Is.False, "over the byte bound");
            Assert.That(Call(session, "AcceptDatagram", new byte[4], 0, 4), Is.True);
            Assert.That(Call(session, "AcceptDatagram", new byte[1], 0, 1), Is.False, "over the count bound");
        }

        [Test]
        public void LocalCloseQueuesTheCapsuleRequiresFinAndResetsEveryStream()
        {
            var recorder = new Recorder();
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false, recorder: recorder);
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            Assert.That(Call(session, "TryOpenOutgoingStream", true), Is.True);
            Call(session, "RegisterOutgoingStream", Handle(1, true, aborts));
            Assert.That(Call(session, "Close", 7u, "done"), Is.True);
            Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Closed"));
            Assert.That(Prop<bool>(session, "OutputMustFinish"), Is.True);
            Assert.That(Prop<bool>(session, "ClosedByPeer"), Is.False);
            Assert.That(aborts.OrderBy(a => a.Id), Is.EqualTo(new[] { (1L, SessionGone), (2L, SessionGone) }));
            var arguments = new object?[] { Array.Empty<byte>() };
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("68430800000007646f6e65")));
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.False);
            Assert.That(Call(session, "Close", 1u, ""), Is.False, "closing twice sends nothing");
            Assert.That(Call(session, "AttachIncomingStream", Handle(6, true, aborts)), Is.False);
            Assert.That(aborts.Last(), Is.EqualTo((6L, SessionGone)));
            Assert.That(Call(session, "AcceptDatagram", new byte[1], 0, 1), Is.False);
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.False);
            Assert.That(recorder.Streams.Count, Is.EqualTo(1));
        }

        [Test]
        public void PeerCloseCapsuleEndsTheSessionAndLaterCapsulesAreMessageErrors()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false);
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            var effect = Call(session, "ProcessCapsule", Capsule(CloseType, Hex("0000002a6f6b")));
            Assert.That(effect?.ToString(), Is.EqualTo("SessionClosed"));
            Assert.That((Prop<uint>(session, "CloseErrorCode"), Prop<string>(session, "CloseMessage"), Prop<bool>(session, "ClosedByPeer")), Is.EqualTo((42u, "ok", true)));
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone) }));
            Assert.That(PropOrNull(session, "FaultErrorCode"), Is.Null);
            AssertError("WebTransportException", H3MessageError, () => Call(session, "ProcessCapsule", Capsule(DrainType, Array.Empty<byte>())));
        }

        [Test]
        public void MalformedPeerCloseIsAMessageErrorThatTerminatesTheSession()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false);
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            AssertError("WebTransportException", H3MessageError, () => Call(session, "ProcessCapsule", Capsule(CloseType, Hex("00000000ff"))));
            Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Closed"));
            Assert.That(Prop<long?>(session, "FaultErrorCode"), Is.EqualTo(H3MessageError));
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone) }));
        }

        [Test]
        public void CleanPeerFinIsAZeroCodeCloseAndAbortIsAPeerClose()
        {
            var session = Session(4, false);
            Call(session, "Establish");
            Call(session, "PeerFinished");
            Assert.That((Prop<object>(session, "State").ToString(), Prop<uint>(session, "CloseErrorCode"), Prop<string>(session, "CloseMessage"), Prop<bool>(session, "ClosedByPeer")),
                Is.EqualTo(("Closed", 0u, "", true)));
            Assert.That(Prop<bool>(session, "OutputMustFinish"), Is.False);
            var other = Session(8, false);
            Call(other, "PeerAborted");
            Assert.That((Prop<object>(other, "State").ToString(), Prop<bool>(other, "ClosedByPeer")), Is.EqualTo(("Closed", true)));
            Call(other, "PeerFinished");
            Call(other, "Terminate", (long?)5);
            Assert.That(PropOrNull(other, "FaultErrorCode"), Is.Null, "a closed session records no later fault");
        }

        [Test]
        public void DrainIsASignalThatLeavesTheSessionUsable()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false);
            Call(session, "Establish");
            Assert.That(Call(session, "ProcessCapsule", Capsule(DrainType, Array.Empty<byte>()))?.ToString(), Is.EqualTo("DrainRequested"));
            Assert.That(Prop<bool>(session, "DrainRequested"), Is.True);
            Assert.That(Call(session, "AttachIncomingStream", Handle(2, true, aborts)), Is.True);
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.True);
            Assert.That(Call(session, "Drain"), Is.True);
            Assert.That(Call(session, "Drain"), Is.False);
            var arguments = new object?[] { Array.Empty<byte>() };
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("800078ae00")));
            Assert.That(aborts, Is.Empty);
        }

        [Test]
        public void TerminateWithACodeRecordsTheFaultAndResetsBufferedStreamsToo()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false);
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            Call(session, "Terminate", (long?)FlowControlError);
            Assert.That(Prop<long?>(session, "FaultErrorCode"), Is.EqualTo(FlowControlError));
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone) }));
            Assert.Throws<InvalidOperationException>(() => Call(session, "Establish"));
        }

        // --- Flow control (section 5) ---

        [Test]
        public void WithoutNegotiatedFlowControlLimitsDoNotApplyAndCapsulesAreIgnored()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, false, Settings(true, 1, 1, 1), Settings(true));
            Call(session, "Establish");
            for (var i = 0; i < 20; i++) Assert.That(Call(session, "AttachIncomingStream", Handle(2 + (i * 4), false, aborts)), Is.True);
            for (var i = 0; i < 20; i++) Assert.That(Call(session, "TryOpenOutgoingStream", true), Is.True);
            Call(session, "RecordIncomingData", 1L << 40);
            Assert.That(Call(session, "TryReserveOutgoingData", 1L << 40), Is.True);
            Assert.That(Call(session, "ProcessCapsule", Limit(MaxData, 0))?.ToString(), Is.EqualTo("None"));
            Assert.That(Call(session, "ProcessCapsule", Capsule(MaxStreamData, Hex("05")))?.ToString(), Is.EqualTo("None"));
            Assert.That(Prop<int>(session, "PendingCapsuleCount"), Is.Zero);
            Assert.That(aborts, Is.Empty);
        }

        [Test]
        public void IncomingStreamsBeyondTheAdvertisedLimitCloseTheSessionWithFlowControlError()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, true, Settings(true, 100, 1, 2), Settings(true, 100, 1, 1));
            Call(session, "Establish");
            Assert.That(Call(session, "AttachIncomingStream", Handle(2, false, aborts)), Is.True);
            Assert.That(Call(session, "AttachIncomingStream", Handle(8, true, aborts)), Is.True);
            Assert.That(Call(session, "AttachIncomingStream", Handle(12, true, aborts)), Is.True);
            AssertError("WebTransportException", FlowControlError, () => Call(session, "AttachIncomingStream", Handle(6, false, aborts)));
            Assert.That(Prop<long?>(session, "FaultErrorCode"), Is.EqualTo(FlowControlError));
            Assert.That(aborts.Select(a => a.Code).Distinct(), Is.EqualTo(new[] { SessionGone }));
            Assert.That(aborts.Select(a => a.Id).OrderBy(id => id), Is.EqualTo(new long[] { 2, 6, 8, 12 }));
        }

        [Test]
        public void OutgoingStreamsRespectThePeerLimitAndQueueOneBlockedCapsulePerLimit()
        {
            var session = Session(4, true, Settings(true, 100, 1, 1), Settings(true, 100, 3, 0));
            Call(session, "Establish");
            // Section 5.6.2 example: a limit of 3 permits three streams, not a fourth.
            for (var i = 0; i < 3; i++) Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.True);
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.False);
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.False);
            Assert.That(Call(session, "TryOpenOutgoingStream", true), Is.False);
            var arguments = new object?[] { Array.Empty<byte>() };
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d440103")));
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d430100")));
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.False);
            Assert.That(Call(session, "ProcessCapsule", Limit(MaxStreamsUni, 4))?.ToString(), Is.EqualTo("CreditIncreased"));
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.True);
            Assert.That(Call(session, "TryOpenOutgoingStream", false), Is.False);
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True, "a new limit produces a new blocked signal");
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d440104")));
        }

        [TestCase(MaxStreamsUni, 3L)]
        [TestCase(MaxStreamsUni, 2L)]
        [TestCase(MaxStreamsBidi, 0L)]
        [TestCase(MaxData, 100L)]
        [TestCase(MaxData, 1L)]
        public void LimitsThatDoNotIncreaseAreFlowControlErrors(long type, long value)
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, true, Settings(true, 100, 1, 1), Settings(true, 100, 3, 0));
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            AssertError("WebTransportException", FlowControlError, () => Call(session, "ProcessCapsule", Limit(type, value)));
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone) }));
            Assert.That(Prop<long?>(session, "FaultErrorCode"), Is.EqualTo(FlowControlError));
        }

        [Test]
        public void ProhibitedPerStreamCapsulesAndOversizedStreamLimitsAreSessionErrors()
        {
            var session = Session(4, true, Settings(true, 100, 1, 1), Settings(true, 100, 3, 0));
            Call(session, "Establish");
            AssertError("WebTransportException", FlowControlError, () => Call(session, "ProcessCapsule", Capsule(MaxStreamData, Hex("05"))));
            session = Session(8, true, Settings(true, 100, 1, 1), Settings(true, 100, 3, 0));
            Call(session, "Establish");
            AssertError("WebTransportException", FlowControlError, () => Call(session, "ProcessCapsule", Capsule(MaxStreamsUni, Hex("d000000000000001"))));
            session = Session(12, true, Settings(true, 100, 1, 1), Settings(true, 100, 3, 0));
            Call(session, "Establish");
            AssertError("WebTransportException", H3MessageError, () => Call(session, "ProcessCapsule", Capsule(MaxData, Hex("0100"))));
            Assert.That(Prop<long?>(session, "FaultErrorCode"), Is.EqualTo(H3MessageError));
        }

        [Test]
        public void SessionDataLimitsCountStreamBodyBytesInBothDirections()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, true, Settings(true, 100, 1, 1), Settings(true, 50, 1, 1));
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            Call(session, "RecordIncomingData", 60L);
            Call(session, "RecordIncomingData", 40L);
            AssertError("WebTransportException", FlowControlError, () => Call(session, "RecordIncomingData", 1L));
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone) }));
            session = Session(8, true, Settings(true, 100, 1, 1), Settings(true, 50, 1, 1));
            Call(session, "Establish");
            Assert.That(Call(session, "TryReserveOutgoingData", 30L), Is.True);
            Assert.That(Call(session, "TryReserveOutgoingData", 21L), Is.False);
            Assert.That(Call(session, "TryReserveOutgoingData", 20L), Is.True);
            Assert.That(Call(session, "TryReserveOutgoingData", 1L), Is.False);
            var arguments = new object?[] { Array.Empty<byte>() };
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d410132")), "one WT_DATA_BLOCKED at limit 50");
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.False);
            Assert.That(Call(session, "ProcessCapsule", Limit(MaxData, 80))?.ToString(), Is.EqualTo("CreditIncreased"));
            Assert.That(Call(session, "TryReserveOutgoingData", 30L), Is.True);
            Assert.That(Prop<long>(session, "PeerMaxData"), Is.EqualTo(80));
        }

        [Test]
        public void ConsumedDataAndFinishedStreamsAdvertiseNewCreditBeforeThePeerBlocks()
        {
            var aborts = new List<(long Id, long Code)>();
            var session = Session(4, true, Settings(true, 100, 2, 0), Settings(true, 50, 1, 1));
            Call(session, "Establish");
            Call(session, "ReleaseIncomingData", 49L);
            Assert.That(Prop<int>(session, "PendingCapsuleCount"), Is.Zero, "below half the window nothing is advertised");
            Call(session, "ReleaseIncomingData", 1L);
            var arguments = new object?[] { Array.Empty<byte>() };
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d3d024096")), "WT_MAX_DATA 150");
            Assert.That(Prop<long>(session, "LocalMaxData"), Is.EqualTo(150));
            var first = Handle(2, false, aborts);
            var second = Handle(6, false, aborts);
            Call(session, "AttachIncomingStream", first);
            Call(session, "AttachIncomingStream", second);
            Call(session, "StreamFinished", first);
            Assert.That(Prop<int>(session, "PendingCapsuleCount"), Is.EqualTo(1), "one finished stream of a two-stream window advertises credit");
            Assert.That(Call(session, "TryDequeueOutgoingCapsule", arguments), Is.True);
            Assert.That(arguments[0], Is.EqualTo(Hex("990b4d400103")), "WT_MAX_STREAMS 3 for unidirectional streams");
            Assert.That(Call(session, "AttachIncomingStream", Handle(10, false, aborts)), Is.True);
            AssertError("WebTransportException", FlowControlError, () => Call(session, "AttachIncomingStream", Handle(14, false, aborts)));
        }

        // --- Connection-scoped registry (sections 4, 4.6, 5.1, 5.2) ---

        [Test]
        public void NegotiationRequiresLocalEnabledPeerSettingsAndTransportParameters()
        {
            var recorder = new Recorder();
            Assert.That(Prop<bool>(Registry(Settings(true), Settings(true), recorder), "Negotiated"), Is.True);
            Assert.That(Prop<bool>(Registry(Settings(false), Settings(true), recorder), "Negotiated"), Is.False);
            Assert.That(Prop<bool>(Registry(Settings(true), Static("WebTransportSettings", "Parse", Hex("08013301"), 1024) ?? throw new AssertionException("Missing settings."), recorder), "Negotiated"), Is.False);
            var registry = Registry(Settings(true), Settings(true), recorder, transportParameters: false);
            Assert.That(Prop<bool>(registry, "Negotiated"), Is.False);
            var arguments = new object?[] { 0L, null };
            Assert.That(Call(registry, "TryCreateSession", arguments)?.ToString(), Is.EqualTo("NotNegotiated"));
            Assert.That(arguments[1], Is.Null);
        }

        [Test]
        public void WithoutFlowControlOnlyOneSessionIsAcceptedAtATime()
        {
            var registry = Registry(Settings(true), Settings(true), new Recorder());
            var arguments = new object?[] { 0L, null };
            Assert.That(Call(registry, "TryCreateSession", arguments)?.ToString(), Is.EqualTo("Accepted"));
            var first = arguments[1] ?? throw new AssertionException("Missing session.");
            Assert.That(Prop<bool>(first, "FlowControlEnabled"), Is.False);
            arguments = new object?[] { 4L, null };
            Assert.That(Call(registry, "TryCreateSession", arguments)?.ToString(), Is.EqualTo("TooManySessions"));
            Call(first, "Close", 0u, "");
            Call(registry, "SessionEnded", first);
            Assert.That(Call(registry, "TryCreateSession", arguments)?.ToString(), Is.EqualTo("Accepted"));
            Assert.That(Prop<int>(registry, "ActiveSessionCount"), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => Call(registry, "TryCreateSession", new object?[] { 4L, null }));
        }

        [Test]
        public void WithFlowControlSessionsPoolUpToTheConfiguredBound()
        {
            var limits = New("WebTransportRegistryLimits");
            Set(limits, "MaximumSessions", 2);
            var registry = Registry(Settings(true, 10), Settings(true, 10), new Recorder(), limits: limits);
            Assert.That(Prop<bool>(registry, "FlowControlEnabled"), Is.True);
            Assert.That(Call(registry, "TryCreateSession", new object?[] { 0L, null })?.ToString(), Is.EqualTo("Accepted"));
            Assert.That(Call(registry, "TryCreateSession", new object?[] { 4L, null })?.ToString(), Is.EqualTo("Accepted"));
            Assert.That(Call(registry, "TryCreateSession", new object?[] { 8L, null })?.ToString(), Is.EqualTo("TooManySessions"));
        }

        [Test]
        public void StreamsForFutureSessionsAreBufferedThenDeliveredAfterEstablishment()
        {
            var recorder = new Recorder();
            var aborts = new List<(long Id, long Code)>();
            var registry = Registry(Settings(true), Settings(true), recorder);
            Assert.That(Call(registry, "RouteIncomingStream", 8L, Handle(2, false, aborts)), Is.True);
            Assert.That(Call(registry, "RouteDatagram", 8L, new byte[] { 1, 2, 3 }, 0, 3), Is.True);
            Assert.That((Prop<int>(registry, "BufferedStreamCount"), Prop<int>(registry, "BufferedDatagramCount")), Is.EqualTo((1, 1)));
            var arguments = new object?[] { 8L, null };
            Assert.That(Call(registry, "TryCreateSession", arguments)?.ToString(), Is.EqualTo("Accepted"));
            var session = arguments[1] ?? throw new AssertionException("Missing session.");
            Assert.That((Prop<int>(registry, "BufferedStreamCount"), Prop<int>(session, "BufferedStreamCount")), Is.EqualTo((0, 1)));
            Assert.That(recorder.Streams, Is.Empty);
            Call(session, "Establish");
            Assert.That(recorder.Streams.Select(s => Prop<long>(s.Stream, "Id")), Is.EqualTo(new[] { 2L }));
            Assert.That(recorder.Datagrams.Single().Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Call(registry, "RouteIncomingStream", 8L, Handle(6, false, aborts)), Is.True);
            Assert.That(recorder.Streams.Count, Is.EqualTo(2));
            Assert.That(aborts, Is.Empty);
        }

        [Test]
        public void StreamsForPastOrClosedSessionsAreResetWithSessionGone()
        {
            var aborts = new List<(long Id, long Code)>();
            var registry = Registry(Settings(true), Settings(true), new Recorder());
            Call(registry, "NoteRequestStream", 12L);
            Assert.That(Call(registry, "RouteIncomingStream", 8L, Handle(2, false, aborts)), Is.False, "an ordinary request stream below the high-water mark");
            Assert.That(Call(registry, "RouteDatagram", 12L, new byte[1], 0, 1), Is.False);
            var arguments = new object?[] { 16L, null };
            Call(registry, "TryCreateSession", arguments);
            var session = arguments[1] ?? throw new AssertionException("Missing session.");
            Call(session, "Establish");
            Call(session, "Close", 0u, "");
            Call(registry, "SessionEnded", session);
            Assert.That(Call(registry, "RouteIncomingStream", 16L, Handle(6, true, aborts)), Is.False);
            Assert.That(aborts, Is.EqualTo(new[] { (2L, SessionGone), (6L, SessionGone) }));
            Assert.That(Call(registry, "RouteIncomingStream", 20L, Handle(10, true, aborts)), Is.True, "a later identifier is still early");
        }

        [Test]
        public void ConnectionBuffersAreBoundedAndTheNewestArrivalsAreDiscarded()
        {
            var aborts = new List<(long Id, long Code)>();
            var limits = New("WebTransportRegistryLimits");
            Set(limits, "MaximumBufferedStreams", 2);
            Set(limits, "MaximumBufferedDatagrams", 2);
            Set(limits, "MaximumBufferedDatagramBytes", 3);
            var registry = Registry(Settings(true), Settings(true), new Recorder(), limits: limits);
            Assert.That(Call(registry, "RouteIncomingStream", 8L, Handle(2, false, aborts)), Is.True);
            Assert.That(Call(registry, "RouteIncomingStream", 12L, Handle(6, false, aborts)), Is.True);
            Assert.That(Call(registry, "RouteIncomingStream", 8L, Handle(10, false, aborts)), Is.False);
            Assert.That(aborts, Is.EqualTo(new[] { (10L, BufferedRejected) }));
            Assert.That(Call(registry, "RouteDatagram", 8L, new byte[2], 0, 2), Is.True);
            Assert.That(Call(registry, "RouteDatagram", 8L, new byte[2], 0, 2), Is.False, "byte bound");
            Assert.That(Call(registry, "RouteDatagram", 8L, new byte[1], 0, 1), Is.True);
            Assert.That(Call(registry, "RouteDatagram", 8L, new byte[0], 0, 0), Is.False, "count bound");
            Assert.That((Prop<int>(registry, "BufferedStreamCount"), Prop<int>(registry, "BufferedDatagramCount")), Is.EqualTo((2, 2)));
        }

        [Test]
        public void InvalidSessionIdentifiersOnStreamsOrDatagramsAreConnectionIdErrors()
        {
            var registry = Registry(Settings(true), Settings(true), new Recorder());
            AssertError("Http3ProtocolException", H3IdError, () => Call(registry, "RouteIncomingStream", 6L, Handle(2, false)));
            AssertError("Http3ProtocolException", H3IdError, () => Call(registry, "RouteDatagram", 1L, new byte[1], 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Call(registry, "TryCreateSession", new object?[] { 2L, null }));
        }

        [Test]
        public void ShutdownTerminatesSessionsResetsBufferedStreamsAndRefusesNewWork()
        {
            var aborts = new List<(long Id, long Code)>();
            var registry = Registry(Settings(true), Settings(true), new Recorder());
            var arguments = new object?[] { 0L, null };
            Call(registry, "TryCreateSession", arguments);
            var session = arguments[1] ?? throw new AssertionException("Missing session.");
            Call(session, "Establish");
            Call(session, "AttachIncomingStream", Handle(2, false, aborts));
            Call(registry, "RouteIncomingStream", 8L, Handle(6, true, aborts));
            Call(registry, "Shutdown");
            Call(registry, "Shutdown");
            Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Closed"));
            Assert.That(PropOrNull(session, "FaultErrorCode"), Is.Null);
            Assert.That(aborts.OrderBy(a => a.Id), Is.EqualTo(new[] { (2L, SessionGone), (6L, SessionGone) }));
            Assert.That((Prop<int>(registry, "ActiveSessionCount"), Prop<int>(registry, "BufferedStreamCount")), Is.EqualTo((0, 0)));
            Assert.That(Call(registry, "RouteIncomingStream", 12L, Handle(10, true, aborts)), Is.False);
            Assert.That(aborts.Last(), Is.EqualTo((10L, SessionGone)));
            Assert.That(Call(registry, "TryCreateSession", new object?[] { 12L, null })?.ToString(), Is.EqualTo("NotNegotiated"));
        }

        [Test]
        public void StreamHandlesAbortOnceWithTheFirstCode()
        {
            var codes = new List<long>();
            var handle = New("WebTransportStreamHandle", 3L, true, new Action<long>(codes.Add));
            Assert.That(Prop<long>(handle, "AbortErrorCode"), Is.EqualTo(-1));
            Call(handle, "Abort", SessionGone);
            Call(handle, "Abort", 0L);
            Assert.That(codes, Is.EqualTo(new[] { SessionGone }));
            Assert.That(Prop<long>(handle, "AbortErrorCode"), Is.EqualTo(SessionGone));
        }

        [Test]
        public async Task ConcurrentAttachOpenAndCloseKeepCountsAndResetsConsistent()
        {
            for (var round = 0; round < 20; round++)
            {
                var aborts = new System.Collections.Concurrent.ConcurrentBag<(long Id, long Code)>();
                var recorder = new Recorder();
                var session = New("WebTransportSession", 0L, false, Settings(true), Settings(true), New("WebTransportSessionLimits"),
                    recorder.StreamHandler,
                    recorder.DatagramHandler);
                Call(session, "Establish");
                using var start = new ManualResetEventSlim();
                var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 50; i++)
                    {
                        var id = 2 + (4 * ((worker * 50) + i));
                        Call(session, "AttachIncomingStream", New("WebTransportStreamHandle", (long)id, false, new Action<long>(code => aborts.Add((id, code)))));
                        if (i == 25 && worker == 3) Call(session, "Close", 1u, "race");
                    }
                })).ToArray();
                start.Set();
                await Task.WhenAll(tasks);
                Assert.That(Prop<object>(session, "State").ToString(), Is.EqualTo("Closed"));
                Assert.That(Prop<int>(session, "ActiveStreamCount"), Is.Zero);
                Assert.That(aborts.Select(a => a.Code).Distinct(), Is.EqualTo(new[] { SessionGone }));
                Assert.That(aborts.Count, Is.EqualTo(400), "every stream was reset exactly once, after delivery or instead of it");
                Assert.That(recorder.Streams.Count, Is.InRange(1, 400));
                Assert.That(recorder.Streams.Select(s => Prop<long>(s.Stream, "Id")).All(id => aborts.Any(a => a.Id == id)), Is.True);
                Assert.That(aborts.Select(a => a.Id).Distinct().Count(), Is.EqualTo(aborts.Count));
            }
        }

        private sealed class FragmentedInput : Stream
        {
            private readonly byte[] _bytes;
            private readonly int _fragment;
            private int _position;
            public FragmentedInput(byte[] bytes, int fragment) { _bytes = bytes; _fragment = fragment; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _bytes.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var available = Math.Min(Math.Min(count, _fragment), _bytes.Length - _position);
                Array.Copy(_bytes, _position, buffer, offset, available);
                _position += available;
                return available;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await Task.Yield();
                return Read(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
