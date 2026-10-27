"""The QUIC peer scripts' clients cannot share a port with an IPv4 socket."""
import ast
import asyncio
import datetime
import socket
import ssl
import unittest
from pathlib import Path
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID
from aioquic.asyncio import serve
from aioquic.quic.configuration import QuicConfiguration
from quic_client import client_socket, connect


class ClientSocketTests(unittest.IsolatedAsyncioTestCase):
    def test_clients_avoid_ports_ipv4_sockets_hold(self):
        # Beside 256 IPv4 sockets, macOS gave aioquic's dual-stack client one of their ports
        # in about one bind of 64, so these 2048 binds would meet about 32.
        holders = []
        try:
            for index in range(256):
                holder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                holders.append(holder)
                holder.bind(('127.0.0.1' if index % 2 == 0 else '0.0.0.0', 0))
            held = {holder.getsockname()[1] for holder in holders}
            for _ in range(2048):
                with client_socket(('127.0.0.1', 4433)) as sock:
                    self.assertEqual(sock.family, socket.AF_INET)
                    self.assertNotIn(sock.getsockname()[1], held)
        finally:
            for holder in holders:
                holder.close()

    def test_scripts_connect_through_quic_client(self):
        root = Path(__file__).resolve().parent
        offenders = []
        for path in [*root.glob('*.py'), *(root.parent / 'EmbedIO.Quic.PackageConsumer').glob('*.py')]:
            if path.name == 'quic_client.py':
                continue
            for node in ast.walk(ast.parse(path.read_text(encoding='utf-8'))):
                if (isinstance(node, ast.ImportFrom) and (node.module or '').startswith('aioquic.asyncio')
                        and any(alias.name == 'connect' for alias in node.names)):
                    offenders.append(f'{path.name}:{node.lineno}')
        self.assertEqual(offenders, [], "aioquic's connect() binds a dual-stack socket; use quic_client.connect")

    async def test_connect_completes_a_handshake_from_an_ipv4_socket(self):
        key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'localhost')])
        now = datetime.datetime.now(datetime.timezone.utc)
        certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=1))
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .add_extension(x509.SubjectAlternativeName([x509.DNSName('localhost')]), critical=False)
            .sign(key, hashes.SHA256()))
        server_config = QuicConfiguration(is_client=False, alpn_protocols=['hq-interop'])
        server_config.certificate, server_config.private_key = certificate, key
        server = await serve('127.0.0.1', 0, configuration=server_config)
        try:
            port = server._transport.get_extra_info('sockname')[1]
            client_config = QuicConfiguration(is_client=True, alpn_protocols=['hq-interop'], server_name='localhost',
                                              verify_mode=ssl.CERT_REQUIRED)
            client_config.load_verify_locations(cadata=certificate.public_bytes(serialization.Encoding.PEM))
            async with asyncio.timeout(10):
                async with connect('127.0.0.1', port, configuration=client_config) as client:
                    self.assertEqual(client._transport.get_extra_info('socket').family, socket.AF_INET)
                    self.assertEqual(client._transport.get_extra_info('sockname')[0], '127.0.0.1')
                    await client.ping()
        finally:
            server.close()


if __name__ == '__main__':
    unittest.main()
