"""Execute the MAUI HTTPS app using normal platform/browser trust on disposable CI hosts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import ssl
import subprocess
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('platform', choices=['windows', 'ios', 'maccatalyst', 'android'])
args = parser.parse_args()
if os.environ.get('GITHUB_ACTIONS') != 'true':
    raise SystemExit('Run this trust-provisioning harness only on a disposable GitHub Actions host.')

ROOT = Path('TestResults/maui-https') / args.platform
ROOT.mkdir(parents=True, exist_ok=True)
CERTS = Path('TestResults/maui-https/certificates').resolve()
PACKAGE = 'io.embedioneo.https'
PORT = 59626
BASE = f'https://127.0.0.1:{PORT}/'
EXPECTED = 'EmbedIO MAUI HTTPS rendered'
CA_SHA1 = hashlib.sha1((CERTS / 'https-test-root.cer').read_bytes()).hexdigest().upper()
context = ssl.create_default_context(cafile=str(CERTS / 'https-test-root.pem'))
process = None
simulator = None
trust_installed = False
process_log = None


def invoke(*arguments, timeout=120):
    return subprocess.check_output(arguments, text=True, stderr=subprocess.STDOUT, timeout=timeout).strip()


def unique(pattern):
    matches = list(Path('test/EmbedIO.MauiHttpsSmoke/bin/Debug').glob(pattern))
    if len(matches) != 1:
        raise RuntimeError(f'Expected one app for {pattern}, found {matches}')
    return matches[0].resolve()


def request(path, data=None):
    with urllib.request.urlopen(urllib.request.Request(BASE + path, data=data), context=context, timeout=10) as response:
        if response.status != 200:
            raise RuntimeError(f'Unexpected HTTP status {response.status}')
        return response.read()


def wait_for_ready():
    deadline = time.monotonic() + 180
    last = None
    while time.monotonic() < deadline:
        try:
            last = json.loads(request('state'))
            if last.get('error') or last.get('phase') == 'failed':
                raise RuntimeError(f'App tests failed: {last}')
            if last.get('phase') == 'ready':
                return last
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            last = str(error)
        if process is not None and process.poll() is not None:
            raise RuntimeError(f'App exited with {process.returncode}; last state: {last}')
        time.sleep(1)
    raise RuntimeError(f'App did not become ready; last state: {last}')


try:
    if args.platform == 'windows':
        invoke('certutil', '-user', '-addstore', 'Root', str(CERTS / 'https-test-root.cer'))
        trust_installed = True
        app = unique('net10.0-windows10.0.19041.0/win-x64/EmbedIO.MauiHttpsSmoke.exe')
        process_log = (ROOT / 'app.log').open('w')
        environment = dict(os.environ, EMBEDIO_HTTPS_RESULTS=str(ROOT.resolve()))
        process = subprocess.Popen([str(app)], stdout=process_log, stderr=subprocess.STDOUT,
                                   env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    elif args.platform == 'ios':
        invoke('xcrun', 'simctl', 'list', '--json')  # Initialize CoreSimulator services.
        simulator = invoke('xcrun', 'simctl', 'create', 'EmbedIO HTTPS test',
                           'com.apple.CoreSimulator.SimDeviceType.iPhone-16',
                           'com.apple.CoreSimulator.SimRuntime.iOS-26-5')
        invoke('xcrun', 'simctl', 'boot', simulator)
        invoke('xcrun', 'simctl', 'bootstatus', simulator, '-b', timeout=240)
        invoke('xcrun', 'simctl', 'keychain', simulator, 'add-root-cert', str(CERTS / 'https-test-root.cer'))
        app = unique('net10.0-ios/iossimulator-arm64/*.app')
        invoke('xcrun', 'simctl', 'install', simulator, str(app))
        invoke('xcrun', 'simctl', 'launch', simulator, PACKAGE)
    elif args.platform == 'maccatalyst':
        invoke('sudo', 'security', 'add-trusted-cert', '-d', '-r', 'trustRoot',
               '-k', '/Library/Keychains/System.keychain', str(CERTS / 'https-test-root.cer'))
        trust_installed = True
        app = unique('net10.0-maccatalyst/maccatalyst-arm64/*.app')
        invoke('codesign', '--verify', '--deep', '--strict', str(app))
        inspection = subprocess.run(['codesign', '-d', '--entitlements', ':-', str(app)],
                                    capture_output=True, check=True, timeout=120)
        (ROOT / 'entitlements.plist').write_bytes(inspection.stdout)
        entitlements = plistlib.loads(inspection.stdout)
        for name in ['com.apple.security.app-sandbox', 'com.apple.security.network.server', 'com.apple.security.network.client']:
            if entitlements.get(name) is not True:
                raise RuntimeError(f'Required entitlement missing: {name}')
        invoke('open', '-n', str(app))
    else:
        adb = str(Path(os.environ['ANDROID_HOME']) / 'platform-tools/adb')
        invoke(adb, 'wait-for-device', timeout=240)
        deadline = time.monotonic() + 240
        while invoke(adb, 'shell', 'getprop', 'sys.boot_completed') != '1':
            if time.monotonic() > deadline:
                raise RuntimeError('Android emulator did not boot.')
            time.sleep(1)
        invoke(adb, 'shell', 'input', 'keyevent', '82')
        app = unique('net10.0-android/android-x64/*-Signed.apk')
        invoke(adb, 'install', '-r', str(app), timeout=180)
        invoke(adb, 'forward', f'tcp:{PORT}', f'tcp:{PORT}')
        invoke(adb, 'shell', 'am', 'start', '-n', PACKAGE + '/.MainActivity')

    initial = wait_for_ready()
    if EXPECTED not in request('').decode('utf-8'):
        raise RuntimeError('External HTTPS client did not retrieve the expected page.')
    # A context without the test CA must reject it, including on Windows where
    # the fixture has deliberately added that CA to the disposable user's store.
    try:
        urllib.request.urlopen(BASE, context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT), timeout=10).close()
        raise RuntimeError('External untrusted client unexpectedly accepted the test CA.')
    except urllib.error.URLError as error:
        if not isinstance(error.reason, ssl.SSLCertVerificationError):
            raise
    final = json.loads(request('finish', data=b''))
    required = ['transport_and_untrusted_certificate', 'platform_client_trust', 'webview_trust_and_render', 'external_https']
    if final.get('passed') is not True or any(final.get('checks', {}).get(name) != 'passed' for name in required):
        raise RuntimeError(f'Incomplete app validation: {final}')
    final['host_negative_trust'] = 'passed'
    final['ca_sha1'] = CA_SHA1
    final['app'] = str(app)
    if args.platform == 'ios':
        final['simulator'] = simulator
        final['ios_runtime'] = 'iOS-26-5'
    if args.platform == 'android':
        final['android_api'] = invoke(adb, 'shell', 'getprop', 'ro.build.version.sdk')
    (ROOT / 'result.json').write_text(json.dumps(final, indent=2))
    print(json.dumps(final, indent=2))
except Exception as error:
    (ROOT / 'result.json').write_text(json.dumps({'passed': False, 'error': str(error)}, indent=2))
    raise
finally:
    if args.platform == 'android':
        try:
            (ROOT / 'logcat.txt').write_text(invoke(adb, 'logcat', '-d'))
        except (OSError, subprocess.SubprocessError):
            pass
    if simulator:
        try:
            container = Path(invoke('xcrun', 'simctl', 'get_app_container', simulator, PACKAGE, 'data'))
            for report in container.rglob('https-result.json'):
                (ROOT / 'app-result.json').write_bytes(report.read_bytes())
            invoke('xcrun', 'simctl', 'shutdown', simulator)
            invoke('xcrun', 'simctl', 'delete', simulator)
        except (OSError, subprocess.SubprocessError):
            pass
    if process is not None and process.poll() is None:
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=10)
    if process_log is not None:
        process_log.close()
    if trust_installed and args.platform == 'windows':
        invoke('certutil', '-user', '-delstore', 'Root', CA_SHA1)
    if trust_installed and args.platform == 'maccatalyst':
        invoke('sudo', 'security', 'remove-trusted-cert', '-d', str(CERTS / 'https-test-root.cer'))
        invoke('sudo', 'security', 'delete-certificate', '-Z', CA_SHA1, '/Library/Keychains/System.keychain')
