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

def adb(*args, timeout=30):
    return subprocess.check_output([ADB, *args], text=True, stderr=subprocess.STDOUT, timeout=timeout).strip()

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
    with urllib.request.urlopen('http://127.0.0.1:59698/host-state', timeout=5) as response:
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
    wait_for(lambda: adb('shell', 'getprop', 'sys.boot_completed'), lambda value: value == '1', 180)
    adb('shell', 'input', 'keyevent', '82')
    apks = list(Path('test/EmbedIO.AndroidSmoke/bin/Debug').rglob('*-Signed.apk'))
    if len(apks) != 1:
        raise RuntimeError(f'Expected one self-contained debug APK, found {apks}')
    # A self-contained MAUI debug APK can take longer on a freshly booted emulator.
    adb('install', '-r', str(apks[0]), timeout=180)
    adb('forward', 'tcp:59697', 'tcp:59697')
    adb('forward', 'tcp:59698', 'tcp:59698')
    launch()
    initial = wait_for(state, lambda value: value['generation'] == 1 and value['resumed'] > 0 and value['frontend_state'] == 'Listening' and value['backend_state'] == 'Listening' and value['https'] == 'passed')
    observations = {'initial': initial}
    def pair(_):
        with urllib.request.urlopen('http://127.0.0.1:59698/index.html', timeout=10) as response:
            if response.read() != b'frontend':
                raise RuntimeError('Unexpected static frontend content.')
        with urllib.request.urlopen('http://127.0.0.1:59697/api', timeout=10) as response:
            if json.load(response)['request'] < 1:
                raise RuntimeError('Unexpected API response.')
    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(pair, range(320)))
    observations['two_server_load'] = state()
    if observations['two_server_load']['requests'] != 320:
        raise RuntimeError('The API did not process every request.')
    with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:59697/work', data=b'', method='POST'), timeout=10) as response:
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
    with urllib.request.urlopen('http://127.0.0.1:59698/index.html', timeout=10) as response:
        if response.read() != b'frontend':
            raise RuntimeError('Backend disposal interrupted the frontend.')
    if observations['backend_disposed']['pid'] != initial['pid']:
        raise RuntimeError('Backend disposal restarted the app process.')
    report = {'passed': True, 'android_api': adb('shell', 'getprop', 'ro.build.version.sdk'), 'observations': observations}
    (ROOT / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
finally:
    for filename, command in [('logcat.txt', ['logcat', '-d']), ('activity.txt', ['shell', 'dumpsys', 'activity'])]:
        try:
            (ROOT / filename).write_text(adb(*command), encoding='utf-8')
        except (OSError, subprocess.SubprocessError) as error:
            (ROOT / filename).write_text(str(error), encoding='utf-8')
