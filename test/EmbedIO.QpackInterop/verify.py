"""Independent QPACK codec/feedback checks; run from the repository root."""

import hashlib
import json
from pathlib import Path
import queue
import random
import subprocess
import sys
import threading

import pylsqpack

from response_campaign import verify_responses


def run(capacity, assembly):
    rng = random.Random(9204002 + capacity)
    log_dir = Path("TestResults/qpack-interop")
    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / f"{Path(assembly).parent.name}-{capacity}-stderr.log"
    executable = "test/EmbedIO.QpackInterop/bin/Release/net10.0/EmbedIO.QpackInterop.dll"
    with log_path.open("w", encoding="utf8") as errors:
        process = subprocess.Popen(
            ["dotnet", executable, assembly, str(capacity)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=errors,
            text=True,
            encoding="utf8",
        )
        responses = queue.Queue()

        def read_responses():
            for line in process.stdout:
                responses.put(line)
            responses.put(None)

        reader = threading.Thread(target=read_responses, daemon=True)
        reader.start()
        encoder = pylsqpack.Encoder()
        peer = pylsqpack.Decoder(0, 0)
        expected = {}
        delivered = blocked = cancelled = feedback_batches = 0

        def command(**data):
            nonlocal delivered, feedback_batches
            process.stdin.write(json.dumps(data) + "\n")
            process.stdin.flush()
            try:
                line = responses.get(timeout=15)
            except queue.Empty as error:
                raise RuntimeError(f"QPACK tool response timed out; see {log_path}") from error
            if line is None:
                raise RuntimeError(f"QPACK tool exited early; see {log_path}")
            reply = json.loads(line)
            for section in reply["ready"]:
                assert section["fields"] == expected.pop(section["stream"]), section
                response_feedback, response_fields = peer.feed_header(
                    section["stream"], bytes.fromhex(section["wire"])
                )
                assert not response_feedback
                assert response_fields == [
                    (name.encode("latin1"), value.encode("latin1"))
                    for name, value in section["fields"]
                ]
                delivered += 1
            if reply["feedback"]:
                encoder.feed_decoder(bytes.fromhex(reply["feedback"]))
                feedback_batches += 1
            return reply

        try:
            command(op="feed", wire=encoder.apply_settings(capacity, 16).hex())
            for batch in range(100):
                instructions = bytearray()
                sections = []
                for position in range(4):
                    index = batch * 4 + position
                    stream = index * 4
                    headers = [
                        (b":method", b"GET"),
                        (b":scheme", b"https"),
                        (b":authority", b"example.com"),
                        (b":path", b"/path/" + str(index % 7).encode()),
                        (b"x-repeat", b"value-" + str(index % 3).encode()),
                        (b"cookie", b"a=b; c=d"),
                        (b"x-empty", b""),
                    ]
                    if index % 11 == 0:
                        headers += [(b"x-octets", bytes(range(32, 256))), (b"x-repeat", b"other")]
                    updates, wire = encoder.encode(stream, headers)
                    instructions.extend(updates)
                    sections.append((stream, wire))
                    expected[stream] = [
                        [name.decode("latin1"), value.decode("latin1")]
                        for name, value in headers
                    ]
                rng.shuffle(sections)
                for stream, wire in sections:
                    reply = command(op="submit", stream=stream, wire=wire.hex())
                    if reply["blocked"]:
                        blocked += 1
                        if stream % 28 == 0:
                            command(op="cancel", stream=stream)
                            expected.pop(stream)
                            cancelled += 1
                offset = 0
                while offset < len(instructions):
                    size = rng.randint(1, 13)
                    command(op="feed", wire=instructions[offset:offset + size].hex())
                    offset += size
                assert not expected, expected
            assert delivered + cancelled == 400
            if capacity:
                assert blocked > 0 and feedback_batches > 0
            dynamic_insertions = 0
            dynamic_peer = pylsqpack.Decoder(capacity, 16)
            for index in range(100):
                name = "x-insert-" + str(index % 5)
                value = "value-" + str(index)
                stream = index * 4
                reply = command(op="insert", name=name, value=value, stream=stream)
                inserted = reply["insertion"]
                if not capacity:
                    assert inserted is None
                    continue
                assert inserted is not None and inserted["index"] == index
                dynamic_insertions += 1
                instructions = bytes.fromhex(inserted["instructions"])
                for octet in instructions:
                    assert not dynamic_peer.feed_encoder(bytes([octet]))
                acknowledgment, fields = dynamic_peer.feed_header(stream, bytes.fromhex(inserted["wire"]))
                assert fields == [(name.encode(), value.encode())]
                assert acknowledgment
                for octet in acknowledgment:
                    command(op="encoder-feedback", wire=bytes([octet]).hex())
            response_planner = verify_responses(command, capacity)
            process.stdin.close()
            process.wait(timeout=10)
            assert process.returncode == 0, log_path
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
            if not process.stdin.closed:
                process.stdin.close()
            reader.join(timeout=1)
            process.stdout.close()
        result = {
            "capacity": capacity,
            "response_planner": response_planner,
            "dynamic_insertions": dynamic_insertions,
            "bidirectional_sections": delivered,
            "blocked": blocked,
            "canceled": cancelled,
            "accepted_feedback_batches": feedback_batches,
        }
        print(json.dumps(result))
        return result


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: verify.py <compiled EmbedIO.dll path>")
    print(f"Independent QPACK interop: pylsqpack {pylsqpack.__version__}; {sys.argv[1]}")
    if pylsqpack.__version__ != "0.3.24":
        raise SystemExit("Install the pinned QPACK requirements before running this probe.")
    results = [run(capacity, sys.argv[1]) for capacity in (0, 220, 4096)]
    report = {
        "assembly": sys.argv[1],
        "assembly_sha256": hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest(),
        "python": sys.version,
        "pylsqpack": pylsqpack.__version__,
        "results": results,
    }
    report_path = Path("TestResults/qpack-interop") / f"{Path(sys.argv[1]).parent.name}-report.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf8")
