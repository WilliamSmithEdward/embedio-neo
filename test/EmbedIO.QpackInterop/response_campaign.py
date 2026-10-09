"""Decode the production response planner with pinned pylsqpack.

pylsqpack does not expose a flush for standalone Insert Count Increments.
The driver emits those only after the independent decoder accepts each complete
insertion. Section acknowledgments are produced by pylsqpack itself. Selected
stream cancellations are driver-generated to simulate abandoned sections.
"""

import random

import pylsqpack


def integer(value, bits, flags=0):
    mask = (1 << bits) - 1
    if value < mask:
        return bytes([flags | value])
    result = bytearray([flags | mask])
    value -= mask
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def verify_responses(command, capacity):
    peer = pylsqpack.Decoder(capacity, 0)
    rng = random.Random(9204003 + capacity)
    received = known = 0
    pending = {}
    metrics = dict(sections=0, dynamic_sections=0, encoder_bytes=0,
                   section_bytes=0, stateless_bytes=0, cancellations=0)

    def feedback(wire):
        nonlocal known
        reply = None
        for byte in wire:
            reply = command(op="response-feedback", wire=bytes([byte]).hex())
            known = reply["known"]
        return reply

    def progress():
        if received > known:
            feedback(integer(received - known, 6))

    def flush(cancel=False):
        streams = list(pending)
        rng.shuffle(streams)
        for stream in streams:
            acknowledgments = pending.pop(stream)
            if cancel and stream % 13 == 0:
                feedback(integer(stream, 6, 64))
                metrics["cancellations"] += 1
            else:
                # Feedback can arrive out of order across streams, but sections
                # on the same stream must retain their acknowledgment order.
                for acknowledgment in acknowledgments:
                    feedback(acknowledgment)

    def exchange(stream, fields, headers_first):
        nonlocal received, known
        reply = command(op="response", stream=stream, fields=fields)
        known = reply["known"]
        response = reply["response"]
        wire = bytes.fromhex(response["wire"])
        instructions = [bytes.fromhex(part) for part in response["instructions"]]
        metrics["sections"] += 1
        metrics["section_bytes"] += len(wire)
        metrics["stateless_bytes"] += len(bytes.fromhex(response["stateless"]))
        metrics["dynamic_sections"] += int(wire[0] != 0)
        metrics["encoder_bytes"] += sum(map(len, instructions))

        def deliver_instructions():
            nonlocal received
            for insertion in instructions:
                offset = 0
                while offset < len(insertion):
                    size = rng.randint(1, 13)
                    assert not peer.feed_encoder(insertion[offset:offset + size])
                    offset += size
                # Each queued item contains one insertion, possibly preceded by
                # the initial capacity update. Count it only after peer parsing.
                received += 1

        if not headers_first:
            deliver_instructions()
        acknowledgment, decoded = peer.feed_header(stream, wire)
        assert decoded == [(name.encode("latin1"), value.encode("latin1"))
                           for name, value, _ in fields], (stream, fields, decoded)
        if acknowledgment:
            assert acknowledgment == integer(stream, 7, 128)
            pending.setdefault(stream, []).append(acknowledgment)
        if headers_first:
            deliver_instructions()
        return reply

    for index in range(256):
        fields = [[":status", "200", False],
                  ["x-group", "group-" + str(index // 2 % 7), False],
                  ["x-repeat", "value-" + str(index // 2 % 3), False],
                  ["set-cookie", "session=" + str(index), False],
                  ["x-private", "never-" + str(index), True]]
        if index % 19 == 0:
            fields += [["x-octets", "".join(map(chr, range(32, 256))), False],
                       ["x-repeat", "second", False]]
        exchange(index * 4, fields, index % 2 == 0)
        if index % 4 == 3:
            progress()
        if index % 16 == 15:
            flush(cancel=True)
    flush()
    progress()

    # One entry occupies more than half the usable table. Referencing it while
    # introducing another large field must not evict it before peer decoding.
    size = min(capacity, 4096) // 2 + 8
    first = ["x-pin", "p" * size, False]
    second = ["x-new", "n" * size, False]
    exchange(2048, [first], True)
    progress()
    flush()
    pinned = exchange(2052, [first, second], False)
    assert not pinned["response"]["instructions"]
    if capacity:
        assert bytes.fromhex(pinned["response"]["wire"])[0] != 0
    flush()
    exchange(2056, [second], False)
    progress()
    flush()

    steady = [[":status", "200", False], ["x-steady", "hello", False]]
    exchange(4096, steady, True)
    progress()
    flush()
    before = metrics["dynamic_sections"]
    for index in range(300):
        reply = exchange(4100 + index * 4, steady, bool(index % 2))
        assert reply["pending"] <= 256
    # The production owner has a 256-section budget. Holding every ACK forces
    # the remaining responses to use literals, without blocking or pinning more.
    assert metrics["dynamic_sections"] - before == (256 if capacity else 0)
    flush()
    progress()

    for index in range(48):
        stream = 8192 + index * 4
        exchange(stream, [[":status", "103", False], ["x-steady", "hello", False]], True)
        exchange(stream, steady, False)
        if index % 8 == 7:
            flush()
            progress()
    flush()
    progress()
    final = command(op="response-feedback", wire="")
    assert final["pending"] == 0
    assert final["known"] == received
    assert (metrics["dynamic_sections"] > 0) == bool(capacity)
    metrics["insertions"] = received
    metrics["total_encoder_and_section_bytes"] = metrics["encoder_bytes"] + metrics["section_bytes"]
    return metrics
