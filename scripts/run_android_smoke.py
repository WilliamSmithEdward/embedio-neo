"""Run the test-only MAUI app on a booted emulator and retain lifecycle evidence."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request

ROOT = Path('TestResults/android')
ADB = str(Path(os.environ['ANDROID_HOME']) / 'platform-tools/adb')
PACKAGE = 'io.embedioneo.smoke597'
ACTIVITY = PACKAGE + '/.MainActivity'
SERIAL = None
FORWARDS = []
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def adb(*args, timeout=30):
    target = ['-s', SERIAL] if SERIAL else []
    return subprocess.check_output([ADB, *target, *args], text=True, stderr=subprocess.STDOUT, timeout=timeout).strip()

def forward(local, remote):
    result = adb('forward', '--no-rebind', local, remote)
    local = 'tcp:' + result if local == 'tcp:0' else local
    FORWARDS.append(local)
    return local

def remove_forward(local):
    adb('forward', '--remove', local)
    FORWARDS.remove(local)

def wait_for(operation, predicate, seconds=90):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            last = operation()
            if predicate(last):
                return last
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            last = str(error)
        time.sleep(1)
    raise RuntimeError(f'Timed out; last observation: {last}')

def state():
    with HTTP.open('http://127.0.0.1:59698/host-state', timeout=5) as response:
        result = json.load(response)
    if result.get('error'):
        raise RuntimeError(result['error'])
    return result

def launch(action=None):
    args = ['shell', 'am', 'start', '-n', ACTIVITY]
    if action:
        args += ['--es', 'smoke_action', action]
    return adb(*args)

ROOT.mkdir(parents=True, exist_ok=True)
try:
    devices = [line.split()[0] for line in adb('devices').splitlines()[1:]
               if len(line.split()) >= 2 and line.split()[1] == 'device' and line.startswith('emulator-')]
    SERIAL = os.environ.get('ANDROID_SERIAL')
    if SERIAL:
        if SERIAL not in devices:
            raise RuntimeError(f'Selected emulator is not online: {SERIAL}')
    elif len(devices) == 1:
        SERIAL = devices[0]
    else:
        raise RuntimeError(f'Select one online emulator with ANDROID_SERIAL: {devices}')
    wait_for(lambda: adb('shell', 'getprop', 'sys.boot_completed'), lambda value: value == '1', 180)
    adb('shell', 'input', 'keyevent', '82')
    apks = list(Path('test/EmbedIO.AndroidSmoke/bin/Debug').rglob('*-Signed.apk'))
    if len(apks) != 1:
        raise RuntimeError(f'Expected one self-contained debug APK, found {apks}')
    # A self-contained MAUI debug APK can take longer on a freshly booted emulator.
    adb('install', '-r', str(apks[0]), timeout=180)
    forward('tcp:59697', 'tcp:59697')
    forward('tcp:59698', 'tcp:59698')
    launch()
    initial = wait_for(state, lambda value: value['generation'] == 1 and value['resumed'] > 0 and value['frontend_state'] == 'Listening' and value['backend_state'] == 'Listening' and value['https'] == 'passed')
    observations = {'initial': initial}
    # Host -> device: the URL uses the HOST port, not the guest's network address.
    mapped = forward('tcp:0', 'tcp:59697')
    port = int(mapped.split(':')[1])
    if port == 59697:
        raise RuntimeError('Expected distinct host/device ports.')
    url = f'http://127.0.0.1:{port}/state'
    def forwarded_state():
        with HTTP.open(url, timeout=5) as response:
            if response.status != 200:
                raise RuntimeError('Unexpected forwarded status.')
            return json.load(response)
    if forwarded_state()['pid'] != initial['pid']:
        raise RuntimeError('Different-port forwarding reached the wrong process.')
    mappings = adb('forward', '--list').splitlines()
    if f'{SERIAL} {mapped} tcp:59697' not in mappings:
        raise RuntimeError(f'Forwarding table does not contain the selected mapping: {mappings}')
    try:
        adb('forward', '--no-rebind', mapped, 'tcp:59698')
    except subprocess.CalledProcessError:
        pass
    else:
        raise RuntimeError('A conflicting --no-rebind forward unexpectedly succeeded.')
    if forwarded_state()['pid'] != initial['pid']:
        raise RuntimeError('Rejected rebinding changed the original destination.')
    remove_forward(mapped)
    try:
        forwarded_state()
    except urllib.error.HTTPError:
        raise RuntimeError('Removed forward still returns an HTTP response.')
    except urllib.error.URLError:
        pass
    else:
        raise RuntimeError('Removed forward still accepts HTTP requests.')
    if state()['pid'] != initial['pid']:
        raise RuntimeError('Removing one mapping interrupted the other listener.')
    forward(mapped, 'tcp:59697')
    if forwarded_state()['pid'] != initial['pid']:
        raise RuntimeError('Recreated forward did not reach the same process.')
    observations['forwarding'] = {'serial': SERIAL, 'host_port': port, 'device_port': 59697,
                                 'different_ports': True, 'removed_unreachable': True,
                                 'recreated_same_process': True, 'other_mapping_healthy': True,
                                 'conflicting_rebind_rejected': True}
    remove_forward(mapped)
    def pair(_):
        with HTTP.open('http://127.0.0.1:59698/index.html', timeout=10) as response:
            if response.read() != b'frontend':
                raise RuntimeError('Unexpected static frontend content.')
        with HTTP.open('http://127.0.0.1:59697/api', timeout=10) as response:
            if json.load(response)['request'] < 1:
                raise RuntimeError('Unexpected API response.')
    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(pair, range(320)))
    observations['two_server_load'] = state()
    if observations['two_server_load']['requests'] != 320:
        raise RuntimeError('The API did not process every request.')
    with HTTP.open(urllib.request.Request('http://127.0.0.1:59697/work', data=b'', method='POST'), timeout=10) as response:
        if response.status != 200 or response.read() != b'accepted':
            raise RuntimeError('Background job did not return the expected immediate response.')
    observations['background_error'] = wait_for(state, lambda value: value['observed'] == 1)
    adb('shell', 'settings', 'put', 'system', 'accelerometer_rotation', '0')
    adb('shell', 'settings', 'put', 'system', 'user_rotation', '1')
    observations['rotated'] = wait_for(state, lambda value: value['configured'] > initial['configured'])
    before = state()
    launch('recreate')
    observations['recreated'] = wait_for(state, lambda value: value['created'] > before['created'])
    before = state()
    adb('shell', 'input', 'keyevent', 'KEYCODE_HOME')
    observations['backgrounded'] = wait_for(state, lambda value: value['stopped'] > before['stopped'])
    before = state()
    launch()
    observations['resumed'] = wait_for(state, lambda value: value['resumed'] > before['resumed'])
    for phase, value in observations.items():
        if phase == 'forwarding':
            continue
        if value['pid'] != initial['pid'] or value['generation'] != 1 or value['frontend_state'] != 'Listening' or value['backend_state'] != 'Listening':
            raise RuntimeError(f'{phase}: the process or listener unexpectedly restarted: {value}')
    pair(0)
    launch('restart')
    observations['rebound'] = wait_for(state, lambda value: value['generation'] == 2 and value['frontend_state'] == 'Listening' and value['backend_state'] == 'Listening')
    if observations['rebound']['pid'] != initial['pid']:
        raise RuntimeError('Explicit listener restart changed the app process.')
    pair(0)
    launch('dispose-backend')
    observations['backend_disposed'] = wait_for(state, lambda value: value['backend_state'] == 'Stopped' and value['backend_completed'] and value['frontend_state'] == 'Listening')
    with HTTP.open('http://127.0.0.1:59698/index.html', timeout=10) as response:
        if response.read() != b'frontend':
            raise RuntimeError('Backend disposal interrupted the frontend.')
    if observations['backend_disposed']['pid'] != initial['pid']:
        raise RuntimeError('Backend disposal restarted the app process.')
    api = adb('shell', 'getprop', 'ro.build.version.sdk')
    if api != '29':
        raise RuntimeError(f'Expected Android API 29, found {api}')
    report = {'passed': True, 'android_api': api, 'adb_version': adb('version'),
              'observations': observations}
    (ROOT / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
finally:
    cleanup_errors = []
    for local in FORWARDS.copy():
        try:
            remove_forward(local)
        except (OSError, subprocess.SubprocessError) as error:
            cleanup_errors.append(str(error))
    (ROOT / 'forwarding-cleanup.json').write_text(json.dumps({'remaining_owned': FORWARDS,
        'errors': cleanup_errors}, indent=2), encoding='utf-8')
    for filename, command in [('logcat.txt', ['logcat', '-d']), ('activity.txt', ['shell', 'dumpsys', 'activity'])]:
        try:
            (ROOT / filename).write_text(adb(*command), encoding='utf-8')
        except (OSError, subprocess.SubprocessError) as error:
            (ROOT / filename).write_text(str(error), encoding='utf-8')
    if cleanup_errors:
        raise RuntimeError(f'Failed to remove owned forwards: {cleanup_errors}')
