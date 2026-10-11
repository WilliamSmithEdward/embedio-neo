using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    internal static class QuicDependencyEvidence
    {
        private static readonly object Sync = new();
        private static bool _verified;
        internal static void VerifyIfRequested()
        {
            var expected = Environment.GetEnvironmentVariable("EMBEDIO_EXPECT_QUIC_LIBRARY_ROOT");
            if (string.IsNullOrEmpty(expected)) return;
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native image evidence requires macOS.");
            lock (Sync)
            {
                if (_verified) return;
                VerifyDarwin(expected);
                _verified = true;
            }
        }
        [SupportedOSPlatform("macos")]
        private static void VerifyDarwin(string expected)
        {
            var root = Path.GetFullPath(expected) + Path.DirectorySeparatorChar;
            var images = new List<string>();
            var count = NativeMethods.ImageCount();
            for (uint index = 0; index < count; index++)
            {
                var path = Marshal.PtrToStringUTF8(NativeMethods.ImageName(index));
                if (path != null && Path.GetFileName(path).StartsWith("libmsquic", StringComparison.Ordinal)) images.Add(path);
            }
            Assert.That(images, Has.Count.EqualTo(1), "The test process must identify its actual MsQuic image.");
            var loaded = Path.GetFullPath(images[0]);
            var canonical = new FileInfo(loaded).ResolveLinkTarget(true)?.FullName ?? loaded;
            Assert.That(canonical.StartsWith(root, StringComparison.Ordinal), Is.True, "The expected build must supply the loaded dependency.");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonical)));
            var expectedHash = Environment.GetEnvironmentVariable("EMBEDIO_EXPECT_QUIC_LIBRARY_SHA256")
                ?? throw new AssertionException("Missing expected native dependency hash.");
            Assert.That(hash, Is.EqualTo(expectedHash).IgnoreCase);
            var output = Environment.GetEnvironmentVariable("EMBEDIO_QUIC_LIBRARY_EVIDENCE")
                ?? throw new AssertionException("Missing native evidence output path.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? throw new AssertionException("Missing output directory."));
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                loaded,
                canonical,
                sha256 = hash,
                expectedRoot = root,
                runtime = Environment.Version.ToString(),
                processId = Environment.ProcessId,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                verified = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        [SupportedOSPlatform("macos")]
        private static class NativeMethods
        {
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
            internal static extern uint ImageCount();
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
            internal static extern IntPtr ImageName(uint index);
        }
    }
}
