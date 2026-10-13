"""Bounded HTTP resource snapshots shared by the HTTP/2 and HTTP/3 drivers.

No protocol-client dependency. Drivers load this sibling by anchored file path so
Python isolated mode (-I) does not depend on the current directory or sys.path.
"""
import json
import socket
import time


def stats(host, port):
    try:
        return _stats(host, port)
    except (OSError, ValueError) as error:  # Missing or malformed observations fail closed.
        return {"error": f"{type(error).__name__}: {error}"}


def _stats(host, port):
    """Reads /__stats over a cleartext HTTP/1.1 port with a minimal request."""
    deadline = time.monotonic() + 10
    maximum_bytes = 64 * 1024  # Snapshot JSON is small; include response framing in the bound.

    def remaining():
        seconds = deadline - time.monotonic()
        if seconds <= 0:
            raise TimeoutError("Stats response exceeded its 10-second budget")
        return seconds

    with socket.create_connection((host, port), timeout=10) as sock:
        sock.settimeout(remaining())
        sock.sendall(f"GET /__stats HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n".encode())
        data = b""
        while True:
            sock.settimeout(remaining())
            chunk = sock.recv(min(65536, maximum_bytes - len(data) + 1))
            remaining()
            if not chunk:
                break
            data += chunk
            if len(data) > maximum_bytes:
                raise ValueError("Stats response exceeded its 64-KiB byte limit")
    head, separator, body = data.partition(b"\r\n\r\n")
    status = head.split(b"\r\n", 1)[0].split()
    if not separator or len(status) < 2 or status[0] not in (b"HTTP/1.0", b"HTTP/1.1") or status[1] != b"200":
        raise ValueError("Stats response must be HTTP/1.x status 200")
    headers = {}
    for line in head.split(b"\r\n")[1:]:
        name, colon, value = line.partition(b":")
        if not colon or not name or (name.lower() in (b"content-length", b"transfer-encoding") and name.lower() in headers):
            raise ValueError("Malformed or duplicate stats response header")
        headers[name.lower()] = value.strip()
    transfer = headers.get(b"transfer-encoding")
    length = headers.get(b"content-length")
    if transfer is not None:
        if transfer.lower() != b"chunked" or length is not None:
            raise ValueError("Unsupported stats response framing")
        decoded = bytearray()
        while True:
            size_line, separator, rest = body.partition(b"\r\n")
            token = size_line.split(b";", 1)[0]
            if not separator or not token or any(c not in b"0123456789abcdefABCDEF" for c in token):
                raise ValueError("Malformed stats chunk size")
            size = int(token, 16)
            if size == 0:
                # Accept a complete bounded trailer section, including no trailers.
                if rest != b"\r\n":
                    if not rest.endswith(b"\r\n\r\n") or any(b":" not in line for line in rest[:-4].split(b"\r\n")):
                        raise ValueError("Incomplete stats chunk termination or malformed trailers")
                break
            if len(rest) < size + 2 or rest[size:size + 2] != b"\r\n":
                raise ValueError("Truncated stats chunk")
            decoded.extend(rest[:size])
            body = rest[size + 2:]
        body = bytes(decoded)
    elif length is not None:
        if not length.isdigit() or int(length) != len(body):
            raise ValueError("Stats Content-Length does not match complete body")
    snapshot = json.loads(body)
    error = observation_error(snapshot)
    if error:
        raise ValueError(error)
    return snapshot


def observation_error(snapshot):
    if not isinstance(snapshot, dict):
        return "Stats response must be a JSON object"
    if "error" in snapshot:
        return "Stats observation reported an error"
    for name in ("activeHandlers", "handles", "managedBytes"):
        value = snapshot.get(name)
        if type(value) is not int or value < 0:
            return f"Stats counter {name} must be a nonnegative integer"
    return None


def resource_error(before, after):
    """Requested snapshots must be available, valid, and settled."""
    if before is None and after is None:
        return None
    if observation_error(before) or observation_error(after):
        return "resource observations unavailable"
    if after["activeHandlers"] != 0:
        return "resources did not settle"
    return None


def apply_resource_verdict(report, before, after, check_growth=False, required=False):
    error = "resource observations unavailable" if required and before is None and after is None else resource_error(before, after)
    if check_growth and before is not None and after is not None and not observation_error(before) and not observation_error(after):
        report["handleGrowth"] = after["handles"] - before["handles"]
        report["managedGrowth"] = after["managedBytes"] - before["managedBytes"]
        if report["handleGrowth"] > 64 or report["managedGrowth"] > 32 << 20:
            error = "resources did not settle"
    if error:
        report["error"] = error
        if "result" in report:
            report["result"] = "FAIL"
    return report
