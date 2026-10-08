"""Strict HTTPS probe reusable with the fixture on simulators or physical devices.

This client changes no OS trust settings and never bypasses chain or hostname checks.
"""
import argparse
import json
from pathlib import Path
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request

MARKER = 'EmbedIO MAUI HTTPS rendered'


def validate(base, cafile, timeout=180, progress=None):
    parsed = urllib.parse.urlparse(base)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError('Specify an HTTPS URL without credentials.')
    base = base.rstrip('/') + '/'
    trusted = ssl.create_default_context(cafile=str(cafile))

    def emit(phase, **details):
        if progress is not None:
            progress(dict(phase=phase, **details))

    def request(path, data=None):
        emit('request-start', path=path)
        started = time.monotonic()
        try:
            with urllib.request.urlopen(urllib.request.Request(base + path, data=data), context=trusted, timeout=10) as response:
                if response.status != 200:
                    raise RuntimeError(f'Unexpected HTTP status {response.status}')
                result = response.read()
                emit('request-complete', path=path, seconds=time.monotonic() - started)
                return result
        except Exception as error:
            emit('request-failed', path=path, seconds=time.monotonic() - started, error=str(error))
            raise

    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            last = json.loads(request('state'))
            emit('app-state', state=last)
            if last.get('error') or last.get('phase') == 'failed':
                raise RuntimeError(f'App tests failed: {last}')
            if last.get('phase') == 'ready':
                break
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            last = str(error)
        time.sleep(1)
    else:
        raise RuntimeError(f'App did not become ready; last state: {last}')
    if MARKER not in request('').decode('utf-8'):
        raise RuntimeError('External HTTPS client did not retrieve the expected page.')
    # No roots loaded: even a locally installed fixture CA must be rejected.
    try:
        urllib.request.urlopen(base, context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT), timeout=10).close()
        raise RuntimeError('External untrusted client unexpectedly accepted the test CA.')
    except urllib.error.URLError as error:
        if not isinstance(error.reason, ssl.SSLCertVerificationError):
            raise
    final = json.loads(request('finish', data=b''))
    required = ['transport_and_untrusted_certificate', 'platform_client_trust', 'webview_trust_and_render', 'external_https']
    if final.get('passed') is not True or any(final.get('checks', {}).get(name) != 'passed' for name in required):
        raise RuntimeError(f'Incomplete app validation: {final}')
    emit('validation-passed', state=final)
    final['host_negative_trust'] = 'passed'
    final['verified_url'] = base
    return final


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True)
    parser.add_argument('--ca', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = validate(args.url, args.ca)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))
