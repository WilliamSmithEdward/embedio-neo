"""Run a load-benchmark command under the shared benchmark lock with a watchdog.

The machine is shared with other agents. A comparison is evidence only when no
other test suite or load generator ran during it, so this wrapper:

1. waits until TestResults/BENCHMARK-LOCK.txt is absent and no foreign test or
   benchmark process has been seen for --idle-seconds (default 120);
2. creates the lock atomically (O_CREAT | O_EXCL) and never overwrites a lock
   written by another owner;
3. starts the command and checks every --watch-interval seconds that the lock
   is still ours and that no foreign process appeared;
4. on either event kills the command's process tree, writes INVALID.txt into the
   attempt's output directory and retries in a fresh directory after the idle
   gate, up to --attempts times. Invalid attempts are kept, never deleted.

The lock is released only when its first line still carries this owner. Nothing
inside the command is retried: a failed sample stays failed in its own output.

usage:
  python -I scripts/guarded_load_benchmark.py --owner "<who>" --output <dir> \
      [--own-marker <path fragment>] [--idle-seconds 120] [--attempts 5] \
      [--expected-minutes 20] -- <command and arguments>

The command's --output argument is rewritten per attempt: the wrapper appends
"-a<attempt>" to --output and passes the result to the command as its own
--output (the load-benchmark orchestrator requires a fresh directory).
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def shared_lock_path():
    """TestResults/BENCHMARK-LOCK.txt of the primary checkout. A worktree's own
    TestResults is private to it, so the lock must live beside the common git
    directory, where every worktree of this repository looks for it."""
    try:
        common = subprocess.check_output(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                         cwd=ROOT, text=True, stderr=subprocess.DEVNULL).strip()
        return Path(common).parent / "TestResults/BENCHMARK-LOCK.txt"
    except (OSError, subprocess.CalledProcessError):
        return ROOT / "TestResults/BENCHMARK-LOCK.txt"


DEFAULT_LOCK = shared_lock_path()
# Command-line fragments that identify another agent's tests or load.
FOREIGN_PATTERNS = ("EmbedIO.Tests", "LoadBenchmark", " test ", "Conformance", "EmbedIO.Fuzz", "EmbedIO.Performance")


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def process_table():
    """Return (pid, parent pid, command line) for every process, best effort."""
    if platform.system() == "Windows":
        script = ("Get-CimInstance Win32_Process | ForEach-Object { "
                  "'{0}\t{1}\t{2}' -f $_.ProcessId, $_.ParentProcessId, ($_.CommandLine -replace '[\\r\\n]', ' ') }")
        output = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True, check=False).stdout
        separator = "\t"
    else:
        output = subprocess.run(["ps", "-eo", "pid=,ppid=,args="], capture_output=True, text=True, check=False).stdout
        separator = None
    rows = []
    for line in output.splitlines():
        parts = line.strip().split(separator, 2)
        if len(parts) == 3 and parts[0].strip().isdigit() and parts[1].strip().isdigit():
            rows.append((int(parts[0]), int(parts[1]), parts[2]))
    return rows


def related(table, own_pids):
    """This process, its ancestors (the shells that started it) and every
    descendant of own_pids. Their command lines repeat this script's own
    arguments, so they must never count as foreign."""
    parents = {pid: ppid for pid, ppid, _ in table}
    children = {}
    for pid, ppid, _ in table:
        children.setdefault(ppid, set()).add(pid)
    descendants = set(own_pids) | {os.getpid()}
    result = set(descendants)
    pid = os.getpid()
    while pid in parents and parents[pid] not in result:
        pid = parents[pid]
        result.add(pid)
    # Ancestors are excluded themselves, but their other children are foreign.
    # Expanding descendants of an ancestor would hide sibling agent jobs.
    pending = list(descendants)
    while pending:
        for child in children.get(pending.pop(), ()):
            if child not in result:
                result.add(child)
                pending.append(child)
    return result


def foreign_processes(own_markers, own_pids):
    table = process_table()
    skip = related(table, own_pids)
    found = []
    for pid, _, command in table:
        if pid in skip:
            continue
        pattern = next((pattern for pattern in FOREIGN_PATTERNS if pattern in command), None)
        if pattern is None:
            continue
        if any(marker and marker in command for marker in own_markers):
            continue
        found.append((pid, f"[{pattern.strip()}] " + command[:160]))
    return found


def lock_is_ours(lock, owner_line):
    try:
        with lock.open("r", encoding="utf-8") as stream:
            return stream.readline().rstrip("\r\n") == owner_line
    except OSError:
        return False


def try_acquire(lock, owner_line, expected_minutes, command):
    try:
        descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        return False
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(f"{owner_line}\n")
        stream.write(f"pid: {os.getpid()}\n")
        stream.write(f"start: {utc_now()}\n")
        stream.write(f"expected end: +{expected_minutes} min\n")
        stream.write(f"command: {' '.join(command)}\n")
    return True


def release(lock, owner_line):
    if lock_is_ours(lock, owner_line):
        try:
            lock.unlink()
        except OSError:
            pass


def kill_tree(process):
    if process.poll() is not None:
        return
    if platform.system() == "Windows":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
    else:
        try:
            os.killpg(os.getpgid(process.pid), 9)
        except OSError:
            process.kill()


def wait_idle(lock, idle_seconds, own_markers, own_pids, log):
    since = time.monotonic()
    while True:
        busy = []
        if lock.exists():
            busy.append(f"lock present: {lock.read_text(encoding='utf-8', errors='replace').splitlines()[:1]}")
        busy.extend(f"{pid} {command}" for pid, command in foreign_processes(own_markers, own_pids))
        if busy:
            since = time.monotonic()
            log(f"waiting, busy: {busy[0]}")
        elif time.monotonic() - since >= idle_seconds:
            return
        time.sleep(15)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--owner", required=True, help="first line of the lock, for example an agent and branch name")
    parser.add_argument("--output", required=True, type=Path, help="output prefix; attempts use <output>-a<n>")
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--own-marker", action="append", default=[], help="command-line fragment identifying this run's own processes")
    parser.add_argument("--idle-seconds", type=int, default=120)
    parser.add_argument("--watch-interval", type=int, default=10)
    parser.add_argument("--attempts", type=int, default=5)
    parser.add_argument("--expected-minutes", type=int, default=20)
    parser.add_argument("command", nargs=argparse.REMAINDER, help="the command, after --")
    args = parser.parse_args()
    command = args.command[1:] if args.command and args.command[0] == "--" else args.command
    if not command:
        parser.error("a command is required after --")
    if args.idle_seconds < 0 or args.watch_interval <= 0 or args.attempts <= 0 or args.expected_minutes <= 0:
        parser.error("idle seconds must be nonnegative; watch interval, attempts and expected minutes must be positive")
    owner_line = f"owner: {args.owner}"
    prefix = args.output.resolve()
    prefix.parent.mkdir(parents=True, exist_ok=True)
    own_markers = list(args.own_marker) + [str(prefix.parent)]
    journal = prefix.parent / (prefix.name + ".guard.log")

    def log(message):
        line = f"{utc_now()} {message}"
        print(line, flush=True)
        with journal.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")

    for attempt in range(1, args.attempts + 1):
        out = prefix.parent / f"{prefix.name}-a{attempt}"
        wait_idle(args.lock, args.idle_seconds, own_markers, set(), log)
        if not try_acquire(args.lock, owner_line, args.expected_minutes, command):
            log("lock appeared while acquiring; waiting again")
            continue
        log(f"attempt {attempt}: lock acquired, output {out}")
        attempt_command = [str(out) if (index > 0 and command[index - 1] == "--output") else token for index, token in enumerate(command)]
        stdout = out.parent / (out.name + ".log")
        verdict = {"attempt": attempt, "output": str(out), "startedUtc": utc_now(), "command": attempt_command}
        reason = None
        process = None
        try:
            with stdout.open("w", encoding="utf-8") as stream:
                popen_kwargs = {"stdout": stream, "stderr": subprocess.STDOUT, "cwd": str(ROOT)}
                if platform.system() != "Windows":
                    popen_kwargs["start_new_session"] = True
                process = subprocess.Popen(attempt_command, **popen_kwargs)
                own = {process.pid}
                while process.poll() is None:
                    time.sleep(args.watch_interval)
                    if not lock_is_ours(args.lock, owner_line):
                        reason = "lock no longer ours"
                    else:
                        foreign = foreign_processes(own_markers, own)
                        if foreign:
                            reason = "foreign tests or load: " + "; ".join(f"{pid} {text}" for pid, text in foreign[:3])
                    if reason:
                        kill_tree(process)
                        process.wait()
                        break
                verdict["exitCode"] = process.returncode
        finally:
            # An exception in monitoring must not leave our child running after
            # releasing the coordination lock. If cleanup fails, retain our lock.
            if process is not None and process.poll() is None:
                kill_tree(process)
                process.wait(timeout=15)
            release(args.lock, owner_line)
        verdict["endedUtc"] = utc_now()
        if reason is None and verdict.get("exitCode") == 0:
            verdict["status"] = "completed"
            (out if out.is_dir() else out.parent).joinpath(out.name + ".attempt.json" if not out.is_dir() else "attempt.json").write_text(
                json.dumps(verdict, indent=2) + "\n", encoding="utf-8")
            log(f"completed {out}")
            return 0
        verdict["status"] = "invalid" if reason else "failed"
        verdict["reason"] = reason or f"command exit {verdict.get('exitCode')}"
        out.mkdir(parents=True, exist_ok=True)
        (out / "INVALID.txt").write_text(f"INVALID attempt {attempt} ({utc_now()}): {verdict['reason']}\n", encoding="utf-8")
        (out / "attempt.json").write_text(json.dumps(verdict, indent=2) + "\n", encoding="utf-8")
        log(f"attempt {attempt} {verdict['status']}: {verdict['reason']}")
        if not reason:
            # A command that failed on its own (for example a failed sample) is not
            # retried: the failure is the result.
            return verdict.get("exitCode") or 1
    log("no attempt completed without foreign load")
    return 2


if __name__ == "__main__":
    sys.exit(main())
