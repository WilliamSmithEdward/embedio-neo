"""Independent native stream/FIN lifetime peer using the pinned aioquic lock."""
import argparse
import asyncio
import hashlib
import json
import sys
from aioquic.asyncio import connect
from aioquic.quic.configuration import QuicConfiguration
from cryptography.hazmat.primitives.serialization import Encoding


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--length", type=int, required=True)
    parser.add_argument("--cafile", required=True)
    parser.add_argument("--sha256", required=True)
    args = parser.parse_args()
    if args.length not in (0, 1024, 1048576):
        raise ValueError("unexpected bounded test length")
    config = QuicConfiguration(is_client=True, alpn_protocols=["h3"], server_name="localhost")
    config.load_verify_locations(args.cafile)
    data = bytes((index * 7) & 255 for index in range(args.length))
    async with connect("127.0.0.1", args.port, configuration=config) as peer:
        await peer.wait_connected()
        certificate = peer._quic.tls._peer_certificate.public_bytes(Encoding.DER)
        if hashlib.sha256(certificate).hexdigest().lower() != args.sha256.lower():
            raise AssertionError("unexpected server certificate")
        reader, writer = await peer.create_stream()
        writer.write(data)
        writer.write_eof()
        await writer.drain()
        received = await asyncio.wait_for(reader.read(), 15)
        if received != data:
            raise AssertionError("stream bytes or FIN did not match")
        print(json.dumps({"event": "stream-complete", "length": len(received)}), flush=True)
        peer.close(error_code=0x100)
        await peer.wait_closed()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        print(json.dumps({"event": "failure", "error": str(error)}), flush=True)
        sys.exit(1)
