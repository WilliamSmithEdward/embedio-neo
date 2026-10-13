using System;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class ListenerAdmissionOptionsTest
    {
        [TestCase("--seconds", "NaN")]
        [TestCase("--seconds", "Infinity")]
        [TestCase("--seconds", "-1")]
        [TestCase("--seconds", "0")]
        [TestCase("--seconds", "2147484")]
        [TestCase("--rounds", "NaN")]
        [TestCase("--rounds", "Infinity")]
        [TestCase("--rounds", "-1")]
        [TestCase("--rounds", "0")]
        [TestCase("--rounds", "1.5")]
        [TestCase("--rounds", "2147483647")]
        [TestCase("--depth", "NaN")]
        [TestCase("--depth", "Infinity")]
        [TestCase("--depth", "-1")]
        [TestCase("--depth", "0")]
        [TestCase("--depth", "1.5")]
        [TestCase("--depth", "2147483647")]
        public void InvalidOptionsFailBeforeStartingWorkers(string option, string value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ListenerAdmission.Run(new[] { "--listener-admission", option, value }));
        }

        [TestCase("--seconds")]
        [TestCase("--rounds")]
        [TestCase("--depth")]
        public void MissingValuesFailBeforeStartingWorkers(string option)
        {
            Assert.Throws<ArgumentException>(() => ListenerAdmission.Run(new[] { "--listener-admission", option }));
        }
    }
}
