"""The inventory must count distinct diagnostics and skip only workload projects."""
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

from analyzer_inventory import count_diagnostics, inventory, requires_workload, solution_members, summary_row


class InventoryTests(unittest.TestCase):
    def test_duplicate_diagnostic_lines_count_once(self):
        log = "\n".join([
            r"C:\repo\src\A.cs(10,5): warning CA1031: Modify to catch a more specific type [A.csproj]",
            r"C:\repo\src\A.cs(10,5): warning CA1031: Modify to catch a more specific type [A.csproj]",
            r"C:\repo\src\B.cs(3,1): error CS0103: The name 'x' does not exist [B.csproj]",
            "    0 Warning(s)",
            "Build succeeded.",
        ])
        self.assertEqual(count_diagnostics(log), (1, 1))

    def test_summary_lines_are_not_diagnostics(self):
        self.assertEqual(count_diagnostics("    2 Warning(s)\n    0 Error(s)\n"), (0, 0))

    def test_solution_members_normalize_separators(self):
        listing = "Project(s)\n----------\nsrc\\EmbedIO\\EmbedIO.csproj\ntest/EmbedIO.Tests/EmbedIO.Tests.csproj\n"
        self.assertEqual(solution_members(listing), {"src/EmbedIO/EmbedIO.csproj", "test/EmbedIO.Tests/EmbedIO.Tests.csproj"})

    def test_platform_targets_require_a_workload(self):
        self.assertTrue(requires_workload("<TargetFramework>net10.0-android</TargetFramework>"))
        self.assertTrue(requires_workload("<TargetFramework Condition=\"'$(P)' == 'ios'\">net10.0-ios</TargetFramework>"))
        self.assertTrue(requires_workload("<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>"))
        self.assertFalse(requires_workload("<TargetFramework>net10.0</TargetFramework>"))
        self.assertFalse(requires_workload("<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>"))
        self.assertFalse(requires_workload("<TargetFramework>net472</TargetFramework>"))

    def test_summary_row_is_tab_separated(self):
        self.assertEqual(summary_row("EmbedIO.sln", "locked", "ok", 0, 0), "EmbedIO.sln\tlocked\tok\t0\t0")


    def test_failed_locked_restore_cannot_pass_using_cached_build(self):
        for failed_target in ("EmbedIO.sln", "test/Probe/Probe.csproj"):
            with self.subTest(target=failed_target), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                project = root / "test/Probe/Probe.csproj"
                project.parent.mkdir(parents=True)
                project.write_text("<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
                (project.parent / "packages.lock.json").write_text("{}")

                def command(arguments, cwd, log_path):
                    if arguments[1] == "restore" and arguments[2] == failed_target:
                        return 1, "locked restore failed"
                    if arguments[1] == "sln":
                        return 0, "Project(s)\n----------\n"
                    return 0, "Build succeeded."

                with patch("analyzer_inventory.run", side_effect=command) as invoked:
                    rows, failures = inventory(root, root / "results")
                self.assertGreater(failures, 0)
                self.assertTrue(any("locked-FAILED" in row for row in rows))
                self.assertFalse(any(call.args[0][1:3] == ["build", failed_target] for call in invoked.call_args_list))

    def test_failed_solution_listing_fails_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)

            def command(arguments, cwd, log_path):
                return (1, "cannot list solution") if arguments[1] == "sln" else (0, "Build succeeded.")

            with patch("analyzer_inventory.run", side_effect=command):
                _, failures = inventory(root, root / "results")
            self.assertGreater(failures, 0)

if __name__ == "__main__":
    unittest.main()
