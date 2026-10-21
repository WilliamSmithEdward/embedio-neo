"""Independent HTTP/3 WebServer/controller/authentication/cancellation validation."""
import argparse
import asyncio
import importlib.metadata
import json
import ssl
from pathlib import Path
from aioquic.asyncio import connect
from aioquic.quic.configuration import QuicConfiguration
from verify import provenance
from verify_http3 import HttpPeer


def configuration(args):
    config = QuicConfiguration(is_client=True, alpn_protocols=['h3'], server_name='localhost',
                               verify_mode=ssl.CERT_REQUIRED, idle_timeout=10, max_datagram_frame_size=65536)
    config.load_verify_locations(cafile=str(args.output / 'certificate.pem'))
    return config


async def client(args, port, index, barrier):
    async with connect('127.0.0.1', port, configuration=configuration(args), create_protocol=HttpPeer) as peer:
        peer.authority = f'127.0.0.1:{port}'.encode()
        await barrier.wait()
        async def expect(path, status, body=None, **kwargs):
            stream, fields, actual = await peer.request(path, **kwargs)
            assert fields[b':status'] == str(status).encode(), (path, fields, actual)
            if body is not None:
                assert actual == body, (path, actual[:200])
            return stream, fields, actual
        await expect('/health', 200, b'controller HTTP/3')
        await expect('/number/42', 200, b'42')
        await expect('/secure', 401)
        await expect('/secure', 401, headers=[(b'authorization', b'Bearer invalid')])
        await expect('/secure', 403, headers=[(b'authorization', b'Bearer writer')])
        await expect('/secure', 200, b'reader', headers=[(b'authorization', b'Bearer reader')])
        payload = bytes((value + index) % 251 for value in range(131072))
        await expect('/echo', 200, payload, data=payload)
        _, _, first = await expect('/metadata', 200)
        _, _, second = await expect('/metadata', 200)
        assert first == second and first.startswith(f'127.0.0.1:{port}|127.0.0.1:'.encode())
        stream, _, _ = await expect('/timeout', 408, b'complete timeout response', data=b'incomplete', incomplete=True)
        await peer.ping()
        assert peer.stopped.get(stream) == 0x100, peer.stopped
        blocked, future = peer.start_request(f'/blocked/{index}')
        async def state(wanted):
            async with asyncio.timeout(5):
                while True:
                    _, fields, body = await peer.request(f'/state/{index}')
                    assert fields[b':status'] == b'200'
                    if body == wanted:
                        return
                    await asyncio.sleep(0.01)
        await state(b'started')
        await expect('/health', 200, b'controller HTTP/3')
        future.cancel()
        peer._quic.stop_stream(blocked, 0x10c)
        peer._quic.reset_stream(blocked, 0x10c)
        peer.transmit()
        await state(b'aborted')
        await expect('/health', 200, b'controller HTTP/3')


async def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match the repository dependency lock')
    args.output.mkdir(parents=True, exist_ok=False)
    certificate = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'certificate', str(args.output),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    out, err = await certificate.communicate()
    if certificate.returncode != 0:
        raise RuntimeError(repr((out, err)))
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'serve-webserver', args.openssl,
        str(args.output / 'certificate.pem'), str(args.output / 'key.pem'), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    failure = None
    ready = {}
    startup = bytearray()
    try:
        async with asyncio.timeout(8):
            while not ready.get('ready'):
                line = await process.stdout.readline()
                startup.extend(line)
                if not line:
                    raise RuntimeError('Host ended before readiness')
                if line.startswith(b'{'):
                    ready = json.loads(line)
        barrier = asyncio.Barrier(4)
        async with asyncio.TaskGroup() as group:
            for index in range(4):
                group.create_task(client(args, ready['port'], index, barrier))
        async with connect('127.0.0.1', ready['port'], configuration=configuration(args), create_protocol=HttpPeer) as peer:
            peer.authority = f"127.0.0.1:{ready['port']}".encode()
            _, pending = peer.start_request('/drain')
            process.stdin.write(b'drain\n')
            await process.stdin.drain()
            fields, body = await asyncio.wait_for(pending, 8)
            assert dict(fields)[b':status'] == b'200' and body == b'd' * 131072
        stdout, stderr = await asyncio.wait_for(process.communicate(), 10)
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if process.returncode is None:
            process.kill()
        stdout, stderr = await process.communicate()
    (args.output / 'host.stdout.log').write_bytes(startup + stdout)
    (args.output / 'host.stderr.log').write_bytes(stderr)
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b'{')]
    report = reports[-1] if reports else {}
    result = {**provenance(args), 'passed': failure is None and process.returncode == 0
              and report.get('completed') is True and report.get('aborted') == 4
              and report.get('drainCompleted') is True and report.get('rebind') is True
              and report.get('healthRequests') == 12 and report.get('numberRequests') == 4
              and report.get('secureRequests') == 4,
              'failure': failure, 'server': report, 'clients': 4,
              'scope': 'Real WebServer, UDP listener, controller routing, bearer policy, upload timeout, stream reset, endpoint isolation, active response drain and rebind; development TLS credentials.'}
    (args.output / 'receipt.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('passed', 'failure', 'server')}), flush=True)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dotnet', 'assembly', 'openssl'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--output', type=Path, required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
