"""Independent packet/key-update vectors and forced-suite HTTP/3 listener checks."""
import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import random
import subprocess
import sys
from types import SimpleNamespace
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDFExpand
from aioquic.quic.crypto import CryptoContext
from aioquic.tls import CipherSuite
from verify import provenance


SUITES = ('AES_128_GCM_SHA256', 'AES_256_GCM_SHA384', 'CHACHA20_POLY1305_SHA256')


def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match its repository lock')
    if not 3 <= args.vectors <= 1024:
        raise ValueError('Use 3..1024 vectors')
    if args.schannel and os.name != 'nt':
        raise RuntimeError('Schannel requires Windows')
    args.output.mkdir(parents=True, exist_ok=False)
    randomizer = random.Random(args.seed)
    vectors = []
    expected = []
    for index in range(args.vectors):
        suite = CipherSuite[SUITES[index % 3]]
        size = 48 if suite == CipherSuite.AES_256_GCM_SHA384 else 32
        secret = randomizer.randbytes(size)
        number = randomizer.choice((0, 1, 255, 65535, 2**32-1, 2**32, 2**62-1))
        header = b'\x43' + randomizer.randbytes(index % 21) + (number % 2**32).to_bytes(4, 'big')
        payload = randomizer.randbytes(randomizer.choice((0, 1, 15, 16, 31, 32, 128, 1200, 4096)))
        sample = randomizer.randbytes(16)
        context = CryptoContext()
        context.setup(cipher_suite=suite, secret=secret, version=1)
        algorithm = hashes.SHA384() if size == 48 else hashes.SHA256()
        label = b'tls13 quic ku'
        info = size.to_bytes(2, 'big') + bytes([len(label)]) + label + b'\0'
        updated_secret = HKDFExpand(algorithm=algorithm, length=size, info=info).derive(secret)
        updated = CryptoContext()
        updated.setup(cipher_suite=suite, secret=updated_secret, version=1)
        updated.hp = context.hp
        masked = context.hp.apply(b'\x43' + bytes(4), sample)
        vectors.append(dict(suite=int(suite), secret=secret.hex(), number=number, header=header.hex(),
                            payload=payload.hex(), sample=sample.hex(), offset=len(header)-4))
        expected.append(dict(packet=context.encrypt_packet(header, payload, number),
                             updated=updated.encrypt_packet(bytes([header[0] ^ 4]) + header[1:], payload, number),
                             mask=bytes([masked[0] ^ 0x43]) + masked[1:5]))
    inputs, outputs = args.output / 'vectors.json', args.output / 'actual.json'
    inputs.write_text(json.dumps(vectors), encoding='utf-8')
    result = subprocess.run([args.dotnet, args.assembly, 'cipher-oracle', str(inputs), str(outputs)],
                            capture_output=True, timeout=60)
    (args.output / 'oracle.log').write_bytes(result.stdout + result.stderr)
    result.check_returncode()
    actual = json.loads(outputs.read_text())
    assert len(actual) == len(expected)
    for index, (wanted, observed) in enumerate(zip(expected, actual)):
        for field in ('packet', 'updated'):
            assert bytes.fromhex(observed[field]) == wanted[field], (index, field)
        if index % 3 == 2:
            mask = bytes.fromhex(observed['mask'])
            assert bytes([mask[0] & 31]) + mask[1:] == wanted['mask'], (index, 'mask')
    records = []
    providers = []
    if args.openssl:
        providers.append(('openssl', args.openssl, SUITES))
    if args.schannel:
        providers.extend((('schannel-rsa', 'schannel-rsa', SUITES[:2]), ('schannel-ecdsa', 'schannel-ecdsa', SUITES[:2])))
    for provider, library, suites in providers:
        for suite in suites:
            name = provider + '-' + suite.lower()
            command = [sys.executable, str(Path(__file__).with_name('verify_listener.py')), '--dotnet', args.dotnet,
                       '--assembly', args.assembly, '--openssl', library, '--cipher', suite,
                       '--output', str(args.output / name)]
            result = subprocess.run(command, capture_output=True, timeout=90)
            (args.output / (name + '.log')).write_bytes(result.stdout + result.stderr)
            records.append(dict(provider=provider, cipher=suite, exit=result.returncode))
            if result.returncode:
                raise RuntimeError('Forced cipher listener failed: ' + name)
    source = provenance(SimpleNamespace(dotnet=args.dotnet, assembly=args.assembly,
                                       openssl='schannel-rsa' if args.schannel else args.openssl or 'none'))
    receipt = dict(**source, passed=True, seed=args.seed, vectors=args.vectors,
                   independentPeer='aioquic ' + importlib.metadata.version('aioquic'),
                   oracleInputSha256=hashlib.sha256(inputs.read_bytes()).hexdigest(), listeners=records,
                   scope='Full packet and key-update comparison for all three suites, random header-mask samples; configured native listener profiles through WebServer/controllers/authentication/cancellation/drain/rebind.')
    (args.output / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(json.dumps(dict(passed=True, vectors=args.vectors, listeners=len(records))), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--assembly', required=True)
    parser.add_argument('--openssl')
    parser.add_argument('--schannel', action='store_true')
    parser.add_argument('--vectors', type=int, default=384)
    parser.add_argument('--seed', type=int, default=1729)
    parser.add_argument('--output', type=Path, required=True)
    main(parser.parse_args())
