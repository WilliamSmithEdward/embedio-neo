"""Independent wire regression for per-frame SETTINGS acknowledgements; no server required."""
import importlib.util
from pathlib import Path
import socket

import h2.exceptions
import h2.settings
from hyperframe import frame as hf

spec = importlib.util.spec_from_file_location("campaign", Path(__file__).with_name("h2_campaign.py"))
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)


def check(reject_overrun=False, finish_empty=False):
    local, peer = socket.socketpair()

    class Endpoint:
        host, port, tls, authority = "127.0.0.1", 1, False, b"example.test"

        def connect(self):
            return local

    client = campaign.Client(Endpoint(), settings={h2.settings.SettingCodes.MAX_FRAME_SIZE: 1048576})
    try:
        sid = client.request(b"GET", b"/")
        client.update_settings({h2.settings.SettingCodes.INITIAL_WINDOW_SIZE: 0})
        client.update_settings({h2.settings.SettingCodes.INITIAL_WINDOW_SIZE: 65535})
        client.flush()
        ack = hf.SettingsFrame(0)
        ack.flags.add("ACK")
        # The initial ACK cannot apply either later SETTINGS value. Fragment its wire.
        wire = hf.SettingsFrame(0).serialize() + ack.serialize()
        peer.sendall(wire[:-2])
        client.receive(1)
        assert client.conn.local_settings.initial_window_size == 65535
        peer.sendall(wire[-2:])
        client.receive(1)
        assert client.conn.local_settings.initial_window_size == 65535
        assert client.conn.max_inbound_frame_size == 16384
        peer.sendall(ack.serialize())  # Only MAX_FRAME_SIZE is acknowledged.
        client.receive(1)
        assert client.conn.max_inbound_frame_size == 1048576
        assert client.conn.local_settings.initial_window_size == 65535
        headers = hf.HeadersFrame(sid, data=b"\x88")  # Static HPACK :status=200.
        headers.flags.add("END_HEADERS")
        peer.sendall(headers.serialize() + hf.DataFrame(sid, data=b"abc").serialize())
        client.receive(1)
        assert client.streams[sid]["body"] == b"abc"
        peer.sendall(ack.serialize())  # Window now shrinks by 65535.
        client.receive(1)
        assert client.conn.local_settings.initial_window_size == 0
        assert client.conn.streams[sid].inbound_flow_control_window == -3
        if finish_empty:
            end = hf.DataFrame(sid, data=b"")
            end.flags.add("END_STREAM")
            peer.sendall(end.serialize())
            client.receive(1)
            assert client.streams[sid]["body"] == b"abc"
            assert client.done(sid)
            assert client.conn.streams[sid].inbound_flow_control_window == 0
            return
        if reject_overrun:
            peer.sendall(hf.DataFrame(sid, data=b"x").serialize())
            try:
                client.receive(1)
            except h2.exceptions.FlowControlError:
                return
            raise AssertionError("DATA after the acknowledged zero window must fail")
        peer.sendall(ack.serialize())  # Restore window on its own acknowledgement.
        client.receive(1)
        assert client.conn.local_settings.initial_window_size == 65535
        data = hf.DataFrame(sid, data=b"def")
        data.flags.add("END_STREAM")
        peer.sendall(data.serialize())
        client.receive(1)
        assert client.streams[sid]["body"] == b"abcdef"
        assert client.done(sid)
        assert not client.pending_settings
    finally:
        local.close()
        peer.close()


check()
check(reject_overrun=True)
check(finish_empty=True)
print("PASS: ordered ACKs, legal DATA and empty END_STREAM pass; an actual window overrun fails")