"""Create disposable key/certificate inputs for the standalone ownership test."""
import argparse
import datetime
from pathlib import Path
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('output', type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
key = ec.generate_private_key(ec.SECP256R1())
now = datetime.datetime.now(datetime.timezone.utc)
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'disposable ownership fixture')])
certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
    .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(hours=1))
    .not_valid_after(now + datetime.timedelta(hours=1)).sign(key, hashes.SHA256()))
pfx = args.output / 'credential.pfx'
pfx.write_bytes(pkcs12.serialize_key_and_certificates(b'fixture', key, certificate, None, serialization.NoEncryption()))
pfx.chmod(0o600)
(args.output / 'certificate.der').write_bytes(certificate.public_bytes(serialization.Encoding.DER))
