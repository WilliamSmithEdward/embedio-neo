"""Binary inspection contracts for distributable private TLS assets."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("native_audit", Path(__file__).resolve().parents[1] / "audit.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

HEADER = "Class: ELF64\nData: 2's complement, little endian\nType: DYN (Shared object file)\nMachine: Advanced Micro Devices X86-64\n"
SEGMENTS = """  LOAD 0x0000 0x0000 0x0000 0x1000 0x1000 R E 0x1000
  LOAD 0x1000 0x1000 0x1000 0x1000 0x2000 RW 0x1000
  GNU_STACK 0x0000 0x0000 0x0000 0x0000 0x0000 RW 0x10
  GNU_RELRO 0x1000 0x1000 0x1000 0x0800 0x0800 R 0x1
"""
DYNAMIC = """0x1 (NEEDED) Shared library: [libc.so.6]
0xe (SONAME) Library soname: [libembedio_quic_tls.so]
0x18 (BIND_NOW)
"""
VERSIONS = "Name: GLIBC_2.2.5 Flags: none\nName: GLIBC_2.17 Flags: none\n"
EXPORTS = "0000000000000000 A EMBEDIO_QUIC_TLS_3\n" + "".join("0000000000000010 T " + symbol + "@@EMBEDIO_QUIC_TLS_3\n" for symbol in sorted(module.EXPORTS))


class LinuxAuditTests(unittest.TestCase):
    def inspect(self, **changes):
        arguments = dict(header=HEADER, segments=SEGMENTS, dynamic=DYNAMIC, versions=VERSIONS,
                         exports=EXPORTS, architecture="x64", requested="linux-x64")
        arguments.update(changes)
        return module.inspect_linux(**arguments)

    def test_baseline_and_architecture(self):
        result = self.inspect()
        self.assertEqual(result["runtimeIdentifier"], "linux-x64")
        self.assertEqual(result["minimumGlibc"], "2.17")
        self.assertEqual(self.inspect(header=HEADER.replace("Advanced Micro Devices X86-64", "AArch64"),
                                      architecture="arm64", requested="linux-arm64")["runtimeIdentifier"], "linux-arm64")

    def test_rejects_incompatible_binary_metadata(self):
        cases = [dict(header=HEADER.replace("ELF64", "ELF32")),
                 dict(header=HEADER.replace("little endian", "big endian")),
                 dict(header=HEADER.replace("DYN", "EXEC")),
                 dict(header=HEADER.replace("X86-64", "wrong")),
                 dict(requested="linux-arm64"),
                 dict(versions=VERSIONS + "Name: GLIBC_2.28 Flags: none\n"),
                 dict(versions="Name: GLIBC_PRIVATE Flags: none\n"),
                 dict(versions=""),
                 dict(dynamic=DYNAMIC + "0x1 (NEEDED) Shared library: [libssl.so.3]\n"),
                 dict(dynamic=DYNAMIC.replace("libc.so.6", "/untrusted/libc.so.6")),
                 dict(dynamic=DYNAMIC.replace("libembedio_quic_tls.so", "other.so")),
                 dict(dynamic=DYNAMIC.replace("0x18 (BIND_NOW)", "")),
                 dict(dynamic=DYNAMIC + "0xf (RPATH) Library rpath: [/tmp]\n"),
                 dict(dynamic=DYNAMIC + "0x1d (RUNPATH) Library runpath: [/tmp]\n"),
                 dict(dynamic=DYNAMIC + "0x16 (TEXTREL)\n"),
                 dict(segments=SEGMENTS.replace("R E", "RWE")),
                 dict(segments=SEGMENTS.replace("RW 0x10", "RWE 0x10")),
                 dict(segments=SEGMENTS.replace("GNU_RELRO", "OTHER")),
                 dict(segments=""), dict(exports=""),
                 dict(exports=EXPORTS.replace(" A EMBEDIO_QUIC_TLS_3", " A EMBEDIO_QUIC_TLS_2")),
                 dict(exports=EXPORTS.replace("@@EMBEDIO_QUIC_TLS_3", "@@EMBEDIO_QUIC_TLS_2")),
                 dict(exports=EXPORTS.replace("@@EMBEDIO_QUIC_TLS_3", "@@OTHER")),
                 dict(exports=EXPORTS + "0000000000000020 T SSL_new\n"),
                 dict(exports=EXPORTS.replace(" T ", " D ", 1))]
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.inspect(**changes)

    def test_development_receipt_reports_newer_glibc_without_distribution_acceptance(self):
        result = self.inspect(versions=VERSIONS + "Name: GLIBC_2.34 Flags: none\n", requested=None)
        self.assertEqual(result["minimumGlibc"], "2.34")

    def test_musl_identity_does_not_claim_versioned_baseline(self):
        dynamic = DYNAMIC.replace("libc.so.6", "libc.musl-x86_64.so.1")
        result = self.inspect(dynamic=dynamic, versions="", requested="linux-musl-x64")
        self.assertEqual(result["runtimeIdentifier"], "linux-musl-x64")
        self.assertIsNone(result["minimumGlibc"])
        with self.assertRaises(ValueError):
            self.inspect(dynamic=dynamic, versions=VERSIONS, requested="linux-musl-x64")


class DarwinAuditTests(unittest.TestCase):
    def inspect(self, **changes):
        arguments = dict(architectures="arm64\n", loads="cmd LC_BUILD_VERSION\nplatform MACOS\nminos 14.0\n",
                         dependencies="library:\n @rpath/libembedio_quic_tls.dylib (compatibility version 0.0.0)\n /usr/lib/libSystem.B.dylib (compatibility version 1.0.0)\n",
                         exports="".join("0000000000000010 T _" + name + "\n" for name in sorted(module.EXPORTS)),
                         architecture="arm64", requested="osx-arm64")
        arguments.update(changes)
        return module.inspect_darwin(**arguments)

    def test_deployment_identity(self):
        result = self.inspect()
        self.assertEqual(result["runtimeIdentifier"], "osx-arm64")
        self.assertEqual(result["minimumMacOS"], "14.0")

    def test_rejects_incompatible_deployment(self):
        for changes in (dict(architectures="x86_64 arm64\n"), dict(requested="osx-x64"),
                        dict(loads="cmd LC_BUILD_VERSION\nplatform MACOS\nminos 15.0\n"),
                        dict(loads="cmd LC_BUILD_VERSION\nplatform MACCATALYST\nminos 14.0\n"),
                        dict(loads="cmd LC_BUILD_VERSION\nplatform 1\nminos 26.0\n"),
                        dict(loads="cmd LC_VERSION_MIN_MACOSX\nversion 15.0\n"),
                        dict(loads="cmd LC_BUILD_VERSION\nplatform 1\nminos 14.0\ncmd LC_RPATH\n"),
                        dict(dependencies="library:\n /usr/lib/libSystem.B.dylib (compatibility version 1.0.0)\n"),
                        dict(exports="")):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.inspect(**changes)


if __name__ == "__main__":
    unittest.main()
