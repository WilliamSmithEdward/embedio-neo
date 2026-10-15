using System;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class HttpCapsuleProtocolTest
    {
        private static readonly Type ProtocolType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpCapsuleProtocol", true)
            ?? throw new AssertionException("Missing capsule protocol validation.");
        private static object? Call(string name, params object?[] arguments)
        {
            try
            {
                return (ProtocolType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing protocol method.")).Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException is Exception cause)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw(); return null; }
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("?1", true)]
        [TestCase("?0", false)]
        [TestCase(" ?1 ", true)]
        [TestCase("?1;unknown", true)]
        [TestCase("?1;a=?0;b=123;c=1.25;d=\"x\";e=:AQI=:;f=token;g=@1;h=%\"hello\"", true)]
        [TestCase("?1,?1", false)]
        [TestCase("?1;foo=(1)", false)]
        [TestCase("?1;X=1", false)]
        [TestCase("?1;foo=\"\\q\"", false)]
        [TestCase("?1;foo=?2", false)]
        [TestCase("?1\t", false)]
        [TestCase("?1;foo=?0\t", false)]
        [TestCase("?10", false)]
        [TestCase("true", false)]
        [TestCase("1", false)]
        public void CapsuleProtocolRequiresOneValidBooleanItem(string? field, bool expected)
        {
            var headers = new NameValueCollection();
            if (field != null) headers.Add("Capsule-Protocol", field);
            Assert.That(Call("IsEnabled", headers), Is.EqualTo(expected));
        }

        [Test]
        public void RepeatedCapsuleProtocolFieldsBecomeAListAndAreIgnored()
        {
            var headers = new NameValueCollection { { "Capsule-Protocol", "?1" }, { "Capsule-Protocol", "?0" } };
            Assert.That(Call("IsEnabled", headers), Is.False);
        }

        [TestCase("Content-Length")]
        [TestCase("Content-Type")]
        [TestCase("Transfer-Encoding")]
        public void CarrierRejectsForbiddenFieldsEvenWithEmptyValues(string name)
        {
            var headers = new NameValueCollection { { name, "" }, { "Capsule-Protocol", "?1" } };
            Assert.Throws<InvalidDataException>(() => Call("ValidateCarrierHeaders", headers, 200));
        }

        [TestCase(101, true)]
        [TestCase(200, true)]
        [TestCase(201, true)]
        [TestCase(299, true)]
        [TestCase(199, false)]
        [TestCase(204, false)]
        [TestCase(205, false)]
        [TestCase(206, false)]
        [TestCase(300, false)]
        [TestCase(404, false)]
        public void CarrierResponseStatusMustPermitNegotiatedCapsules(int status, bool accepted)
        {
            var headers = new NameValueCollection { { "Capsule-Protocol", "?1" }, { "Server", "application" } };
            if (accepted) Assert.DoesNotThrow(() => Call("ValidateCarrierHeaders", headers, status));
            else Assert.Throws<InvalidDataException>(() => Call("ValidateCarrierHeaders", headers, status));
        }
    }
}
