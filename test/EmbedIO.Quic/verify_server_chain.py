import argparse, asyncio, hashlib, importlib.metadata, json, os, ssl, sys
from verify import provenance
from cryptography.hazmat.primitives import hashes
from pathlib import Path
from aioquic.asyncio import connect, QuicConnectionProtocol
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.events import ConnectionTerminated

async def case(args, profile):
    output=args.output/profile;output.mkdir(exist_ok=False)
    process=await asyncio.create_subprocess_exec(args.dotnet,args.assembly,'serve-server-chain',profile,str(output),stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
    ready=b'';stdout=b'';stderr=b'';failure=None;tcp_ok=False;quic_ok=False;observed=[]
    class Peer(QuicConnectionProtocol):
        def quic_event_received(self,event):
            if isinstance(event,ConnectionTerminated):observed.append(dict(code=event.error_code,reason=event.reason_phrase))
            return super().quic_event_received(event)
    try:
        async with asyncio.timeout(12):
            ready=await process.stdout.readline();fields=json.loads(ready)
            assert fields['intermediates']==len(fields['issuers']),fields
            context=ssl.create_default_context(cafile=str(output/'root.pem'))
            context.verify_flags |= ssl.VERIFY_X509_STRICT
            reader,writer=await asyncio.open_connection('127.0.0.1',fields['tcp'],ssl=context,server_hostname='localhost')
            assert await reader.readexactly(12)==b'tcp baseline'
            writer.close();await writer.wait_closed();tcp_ok=True
            config=QuicConfiguration(is_client=True,alpn_protocols=['h3'],server_name='localhost',verify_mode=ssl.CERT_REQUIRED)
            config.load_verify_locations(cafile=str(output/'root.pem'))
            async with connect('127.0.0.1',fields['port'],configuration=config,create_protocol=Peer) as peer:
                actual=[certificate.fingerprint(hashes.SHA256()).hex().upper() for certificate in peer._quic.tls._peer_certificate_chain]
                assert actual==fields['issuers'],(actual,fields['issuers'])
                reader,writer=await peer.create_stream()
                writer.write(b'chain');writer.write_eof()
                assert await asyncio.wait_for(reader.read(),5)==b'chain'
                quic_ok=True
    except Exception as error:failure=repr(error)
    finally:
        try:
            if process.returncode is None:
                process.stdin.write(b'stop\n');await process.stdin.drain()
            stdout,stderr=await asyncio.wait_for(process.communicate(),10)
        except Exception as error:
            failure=failure or repr(error)
            if process.returncode is None:process.kill()
            stdout,stderr=await process.communicate()
    (output/'host.stdout').write_bytes(ready+stdout);(output/'host.stderr').write_bytes(stderr)
    receipt=dict(profile=profile,passed=failure is None and tcp_ok and quic_ok and process.returncode==0,tcp=tcp_ok,quic=quic_ok,failure=failure,exit=process.returncode,termination=observed,scope='Same ephemeral configured certificate and disposable current-user CA store, BCL TCP control versus actual EmbedIO.Quic common TLS factory; peer trusts only root. No host trust store changed.')
    (output/'receipt.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8');print(json.dumps(receipt),flush=True)
    return receipt

async def main(args):
    if os.environ.get('EMBEDIO_QUIC_DISPOSABLE_CERTIFICATE_STORE') != '1':
        raise RuntimeError('Explicit disposable certificate-store profile required')
    lock=Path(__file__).resolve().parents[1]/'EmbedIO.Conformance/drivers/requirements.txt'
    pins=[line.split('==',1)[1].split()[0] for line in lock.read_text(encoding='utf-8').splitlines() if line.startswith('aioquic==')]
    if len(pins)!=1 or importlib.metadata.version('aioquic')!=pins[0]:
        raise RuntimeError('Independent peer does not match the dependency lock')
    args.openssl='portable-rsa'
    args.output.mkdir(exist_ok=False)
    results=[]
    for profile in ('complete','partial','two-intermediates','direct-root','self-signed'):
        results.append(await case(args,profile))
        if not results[-1]['passed']:break
    receipt={**provenance(args),'passed':len(results)==5 and all(result['passed'] for result in results),'cases':results}
    (args.output/'receipt.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
    return 0 if receipt['passed'] else 1

if __name__=='__main__':
    parser=argparse.ArgumentParser(description='Disposable Linux server-chain interoperability checks')
    parser.add_argument('--dotnet',required=True);parser.add_argument('--assembly',required=True)
    parser.add_argument('--output',type=Path,required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
