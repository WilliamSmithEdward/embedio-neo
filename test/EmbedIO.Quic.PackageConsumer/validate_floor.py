"""Run the package consumers on the oldest glibc or musl that .NET 10 supports.

validate.py first builds and checks the consumers on the actual RID. This script then
starts each consumer's server in a container of the floor image, which holds only the
OS packages .NET needs, with the exact .NET runtime that built the consumers mounted
read-only. The independent peer stays on the host and reaches the server through the
shared host network. A Native AOT binary only runs on the glibc it was built against or
a newer one, so on glibc the AOT consumer is published again inside the floor's build
stage before it runs there.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
import zipfile
from defusedxml import ElementTree as SafeET

sys.path.insert(0, str(Path(__file__).resolve().parent))
from validate import CIPHERS, ROOT, require, verify_transitive_runtime  # noqa: E402

# The libc each floor image must report; see Dockerfile.floor-glibc and Dockerfile.floor-musl.
FLOORS = {"linux-x64": ("glibc", "2.27"), "linux-arm64": ("glibc", "2.27"),
          "linux-musl-x64": ("musl", "1.2.3"), "linux-musl-arm64": ("musl", "1.2.3")}
LIBRARY = "libembedio_quic_tls.so"
ENTRY = "EmbedIO.Quic.Development"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, log, timeout=600, check=True, environment=None):
    with log.open("wb") as stream:
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, timeout=timeout, env=environment)
    require(not check or result.returncode == 0, "Step failed, see " + log.name)
    return result.returncode


def image_libc(image, kind):
    # glibc's ldd prints its version on stdout; musl's loader prints it on stderr and exits 1.
    result = subprocess.run(["docker", "run", "--rm", image, "ldd", "--version"], capture_output=True, text=True, timeout=120)
    text = (result.stdout + result.stderr).strip()
    lines = text.splitlines()
    if kind == "glibc":
        version = lines[0].rsplit(" ", 1)[-1] if lines else ""
    else:
        version = next((line.split(" ", 1)[1] for line in lines if line.startswith("Version ")), "")
    return version, text


def stage_runtime(dotnet_root, target):
    """Copy only the runtime the pinned SDK bundles, so the container cannot pick another."""
    sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    props = dotnet_root / "sdk" / sdk / "Microsoft.NETCoreSdk.BundledVersions.props"
    version = SafeET.parse(props, forbid_dtd=True, forbid_entities=True, forbid_external=True).findtext(
        ".//{*}BundledNETCoreAppPackageVersion")
    require(bool(version), "The SDK does not name its bundled runtime")
    framework = dotnet_root / "shared" / "Microsoft.NETCore.App" / version
    fxr = dotnet_root / "host" / "fxr" / version
    require(framework.is_dir() and fxr.is_dir() and (dotnet_root / "dotnet").is_file(),
            "The SDK's bundled runtime " + version + " is not installed beside it")
    target.mkdir(parents=True, exist_ok=False)
    shutil.copy2(dotnet_root / "dotnet", target / "dotnet")
    shutil.copytree(fxr, target / "host" / "fxr" / version, symlinks=True)
    shutil.copytree(framework, target / "shared" / "Microsoft.NETCore.App" / version, symlinks=True)
    return sdk, version


def container(image, label, mounts, environment=(), network="host", interactive=False, workdir=None):
    command = ["docker", "run", "--rm", "--init", "--label", label, "--network", network,
               "--user", f"{os.getuid()}:{os.getgid()}", "--env", "HOME=/tmp",
               "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_NOLOGO=1"]
    if interactive:
        command.append("-i")
    if workdir:
        command += ["--workdir", str(workdir)]
    for source, target, readonly in mounts:
        command += ["--mount", f"type=bind,source={source},target={target}" + (",readonly" if readonly else "")]
    for name, value in environment:
        command += ["--env", f"{name}={value}"]
    return command + [image]


def validate(args):
    kind, floor = FLOORS[args.rid]
    require(sys.platform == "linux", "The floor runs need Linux containers on the actual architecture")
    receipt = json.loads((args.consumers / "receipt.json").read_text(encoding="utf-8"))
    require(receipt.get("passed") is True and receipt.get("rid") == args.rid,
            "The consumers did not pass validate.py on this RID")
    version = SafeET.parse(ROOT / "Directory.Build.props", forbid_dtd=True, forbid_entities=True,
                           forbid_external=True).findtext(".//Version")
    with zipfile.ZipFile(args.feed / f"EmbedIO-Neo.Quic.Native.{version}.nupkg") as bundle:
        native_bytes = bundle.read(f"runtimes/{args.rid}/native/{LIBRARY}")
    require(hashlib.sha256(native_bytes).hexdigest() == receipt["nativeSha256"],
            "The feed's native runtime differs from the one the consumers were checked with")
    args.output.mkdir(parents=True, exist_ok=False)
    actual, libc_text = image_libc(args.image, kind)
    require(actual == floor, f"The floor image reports {kind} {actual}, not {floor}")
    image_id = json.loads(subprocess.check_output(["docker", "image", "inspect", args.image]))[0]["Id"]
    runtime = args.output / "dotnet"
    sdk, runtime_version = stage_runtime(args.dotnet_root, runtime)
    label = "org.embedio.quic-floor=" + uuid.uuid4().hex
    steps = []
    profiles = {"portable": args.consumers / "portable", "rid": args.consumers / "rid", "aot": args.consumers / "aot"}
    aot_build = None
    try:
        if args.build_image:
            # Publish the AOT consumer on the floor itself, with the host's SDK mounted.
            target = args.output / "aot-floor"
            lock = args.output / "aot-floor.lock.json"
            # validate.py's source mapping: the exact packages from the feed, the rest from nuget.org.
            config = args.output / "nuget.config"
            shutil.copy2(args.consumers / "nuget.config", config)
            consumer = str(ROOT / "test/EmbedIO.Quic.PackageConsumer/EmbedIO.Quic.PackageConsumer.csproj")
            properties = ["-r", args.rid, "-p:PublishAot=true", "-p:NuGetLockFilePath=" + str(lock)]
            # The working directory makes global.json select the pinned SDK.
            build = container(args.build_image, label, [(ROOT, ROOT, False), (args.output, args.output, False),
                                                        (args.dotnet_root, "/opt/dotnet", True), (args.feed, args.feed, True)],
                              [("DOTNET_ROOT", "/opt/dotnet"), ("NUGET_PACKAGES", str(args.output / "nuget"))],
                              network="bridge", workdir=ROOT)
            run(build + ["/opt/dotnet/dotnet", "restore", consumer, "--configfile", str(config), "--use-lock-file", *properties],
                args.output / "aot-floor-restore.log")
            steps.append({"name": "aot-floor-restore", "exit": 0})
            verify_transitive_runtime(lock, version)
            run(build + ["/opt/dotnet/dotnet", "publish", consumer, "--no-restore", "-c", "Release", "-o", str(target), *properties],
                args.output / "aot-floor-publish.log", timeout=1200)
            steps.append({"name": "aot-floor-publish", "exit": 0})
            require((target / LIBRARY).read_bytes() == native_bytes, "The floor AOT publish did not select the package native asset")
            aot_build = {"image": json.loads(subprocess.check_output(["docker", "image", "inspect", args.build_image]))[0]["Id"],
                         "entrySha256": digest(target / ENTRY)}
            profiles["aot"] = target
        mounts = [(args.consumers, args.consumers, True), (args.output, args.output, False), (runtime, "/opt/dotnet", True)]
        launcher = container(args.image, label, mounts, [("DOTNET_ROOT", "/opt/dotnet")], interactive=True)
        verifier = str(ROOT / "test/EmbedIO.Quic.PackageConsumer/verify.py")
        for profile, directory in profiles.items():
            require((directory / LIBRARY).read_bytes() == native_bytes if profile != "portable" else
                    (directory / "runtimes" / args.rid / "native" / LIBRARY).read_bytes() == native_bytes,
                    "Consumer native bytes differ: " + profile)
            entry = directory / (ENTRY if profile == "aot" else ENTRY + ".dll")
            mode = ["--native"] if profile == "aot" else ["--dotnet", "/opt/dotnet/dotnet"]
            for cipher in CIPHERS:
                name = profile + "-" + cipher
                run([str(args.peer), verifier, "--entry", str(entry), "--cipher", cipher, "--launcher", json.dumps(launcher),
                     "--output", str(args.output / name), *mode], args.output / (name + ".log"), timeout=120)
                steps.append({"name": name, "exit": 0})
            missing = args.output / (profile + "-missing-runtime")
            shutil.copytree(directory, missing, ignore=shutil.ignore_patterns(LIBRARY))
            entry = missing / (ENTRY if profile == "aot" else ENTRY + ".dll")
            name = profile + "-missing-tls"
            run([str(args.peer), verifier, "--entry", str(entry), "--cipher", CIPHERS[0], "--missing-tls",
                 "--launcher", json.dumps(launcher), "--output", str(args.output / name), *mode],
                args.output / (name + ".log"), timeout=60)
            steps.append({"name": name, "exit": 0})
    finally:
        # A verifier that times out kills only the docker client; remove its container too.
        leftovers = subprocess.run(["docker", "ps", "-aq", "--filter", "label=" + label], capture_output=True, text=True).stdout.split()
        if leftovers:
            subprocess.run(["docker", "rm", "-f", *leftovers], capture_output=True)
    result = {"passed": True, "rid": args.rid, "libc": {"kind": kind, "version": actual, "report": libc_text},
              "image": image_id, "sdk": sdk, "runtime": runtime_version, "nativeSha256": receipt["nativeSha256"],
              "aotBuiltOnFloor": aot_build, "steps": steps, "leftoverContainers": len(leftovers),
              "scope": "Package-only portable, RID-specific and Native AOT consumers on the oldest libc .NET 10 supports, "
                       "with all three cipher profiles and the missing-runtime control. The independent peer ran on the host."}
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "rid": args.rid, "libc": f"{kind} {actual}", "steps": len(steps)}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=sorted(FLOORS), required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--consumers", type=Path, required=True, help="validate.py output with the published consumers")
    parser.add_argument("--image", required=True, help="Floor runtime image")
    parser.add_argument("--build-image", help="Floor build image; publishes the Native AOT consumer on the floor")
    parser.add_argument("--dotnet-root", type=Path, required=True, help="Directory holding the pinned SDK and its runtime")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--peer", type=Path, required=True,
                        help="Python of the independent peer environment (validate.py --peer-directory)")
    arguments = parser.parse_args()
    # Keep the virtual environment's interpreter path; resolving it would leave the environment.
    arguments.peer = Path(os.path.abspath(arguments.peer))
    for name in ("feed", "consumers", "dotnet_root", "output"):
        setattr(arguments, name, getattr(arguments, name).resolve())
    validate(arguments)
