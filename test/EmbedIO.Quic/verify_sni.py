"""Independent public-client checks of recordless TLS certificate selection."""
import argparse
import asyncio
import json
from pathlib import Path
import subprocess

from verify import case, provenance


async def main(args):
    args.openssl = "schannel-sni"
    args.output.mkdir(parents=True, exist_ok=False)
    subprocess.run([args.dotnet, args.assembly, "certificate", str(args.output)], check=True, capture_output=True)
    default = args.output / "certificate.pem"
    selected = args.output / "certificate.pem.selected.pem"
    results = []
    for name, hostname, expected, trust in [
        ("default", "localhost", "localhost", default),
        ("selected", "selected.example", "selected.example", selected),
        ("selected-case", "SELECTED.example", "SELECTED.example", selected),
        ("selected-wrong-trust", "selected.example", "localhost", default),
        ("unknown-fallback", "unknown.example", "localhost", default),
        ("absent-sni", None, None, default),
    ]:
        result = await case(args, name, b"selected certificate stream", hostname=hostname,
                            expected_hostname=expected, trust_certificate=trust)
        diagnostics = result.get("server", {}).get("tlsDiagnostics", [])
        names = [line for line in diagnostics if line.startswith("server-name=")]
        requested = hostname if hostname is not None else "(none)"
        result["passed"] = result["passed"] and names == ["server-name=" + requested]
        results.append(result)
        print(json.dumps(result), flush=True)
        if not result["passed"]:
            break
    receipt = {"passed": len(results) == 6 and all(result["passed"] for result in results),
               "cases": results, **provenance(args)}
    (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
