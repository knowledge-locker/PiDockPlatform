#!/usr/bin/env python3
"""Pre-commissioning acceptance check for the named development host only.

Briefly stops/restarts PiDockCloud to install/remove one disposable invitation.
Never creates a user, changes an existing identity, or prints enrollment secrets.
"""
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request
import uuid

CLOUD = 'prox3mus-pidockplatform-eca278f0'
HOST = 'pidock-dev.knowledgelocker.ai'
IMAGE = 'eclipse-mosquitto:2.0.22@sha256:212f89e1eaeb2c322d6441b64396e3346026674db8fa9c27beac293405c32b3c'


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout.strip()


def save(path, value):
    temporary = path.with_name(uuid.uuid4().hex + '.tmp')
    with temporary.open('x') as stream:
        json.dump(value, stream); stream.flush(); os.fsync(stream.fileno())
    os.chown(temporary, 1654, 1654); temporary.chmod(0o600); temporary.replace(path)


def ready():
    for _ in range(30):
        try:
            with urllib.request.urlopen('https://' + HOST + '/health/live', timeout=3) as response:
                if response.status == 200:
                    return
        except Exception:
            time.sleep(1)
    raise RuntimeError('Development HTTPS did not become healthy.')


def main():
    if os.geteuid() != 0 or os.uname().sysname != 'Linux':
        raise RuntimeError('Run on the development Linux host as root.')
    os.umask(0o077)
    root = Path(json.loads(run('docker', 'volume', 'inspect', 'pidock-development-cloud'))[0]['Mountpoint'])
    if (root / 'accounts.protected').exists():
        raise RuntimeError('This pre-commissioning test refuses cloud storage with accounts. Use normal authenticated enrollment instead.')
    invitation_path = root / 'enrollments.json'
    tenant, device = 'diagnostic-' + uuid.uuid4().hex[:12], 'transport-check'
    now = dt.datetime.now(dt.timezone.utc); code = uuid.uuid4().hex.upper()
    invitation = {'id': str(uuid.uuid4()), 'tenantId': tenant, 'deviceId': device, 'name': 'Disposable connectivity check',
                  'createdBy': 'host-acceptance-check', 'createdAt': now.isoformat(), 'expiresAt': (now + dt.timedelta(minutes=10)).isoformat(),
                  'codeHash': hashlib.sha256(code.encode()).hexdigest().upper(), 'state': 'Pending'}
    test_client = 'pidock-development-connectivity-check'
    with tempfile.TemporaryDirectory(prefix='pidock-connectivity-', dir='/run') as temporary:
        stage = Path(temporary)
        try:
            run('docker', 'stop', '--time', '20', CLOUD)
            existing = json.loads(invitation_path.read_text()) if invitation_path.exists() else []
            save(invitation_path, [*existing, invitation])
            run('docker', 'start', CLOUD); ready()
            run('openssl', 'req', '-new', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(stage / 'device.key'),
                '-out', str(stage / 'device.csr'), '-subj', '/CN=ignored-diagnostic-request')
            request = urllib.request.Request('https://' + HOST + '/api/enrollment/redeem',
                data=json.dumps({'code': code, 'csrPem': (stage / 'device.csr').read_text()}).encode(), headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=20) as response:
                reply = json.load(response)
            if reply['tenantId'] != tenant or reply['deviceId'] != device or reply['mqttHost'] != HOST or reply['mqttPort'] != 8883:
                raise RuntimeError('Enrollment returned an unexpected identity or endpoint.')
            (stage / 'device.crt').write_text(reply['certificatePem']); (stage / 'ca.crt').write_text(reply['caPem'])
            for path in stage.iterdir():
                os.chown(path, 1654, 1654); path.chmod(0o400)
            os.chown(stage, 1654, 1654)
            print('PASS: public HTTPS enrollment and constrained certificate identity.', flush=True)
            command = ['docker', 'run', '--rm', '--name', test_client, '--network', 'host', '--read-only', '--cap-drop', 'ALL',
                       '--security-opt', 'no-new-privileges:true', '--user', '1654:1654', '--mount', 'type=bind,src=' + str(stage) + ',dst=/diagnostic,readonly']
            tls = ['-h', HOST, '-p', '8883', '--cafile', '/diagnostic/ca.crt', '--cert', '/diagnostic/device.crt', '--key', '/diagnostic/device.key', '-V', 'mqttv5']
            wrong = subprocess.run([*command, '--entrypoint', 'mosquitto_pub', IMAGE, *tls, '-q', '1', '-t', 'pidock/v1/tenants/other/devices/transport-check/telemetry', '-m', '{}'], capture_output=True, text=True, timeout=20)
            # Mosquitto 2.0 can return zero even for a denied PUBACK; inspect its explicit reason.
            if 'not authorized' not in (wrong.stdout + wrong.stderr).lower():
                raise RuntimeError('The broker did not explicitly reject cross-tenant publication: ' + str(wrong.returncode) + ' ' + wrong.stdout[-500:] + wrong.stderr[-500:])
            print('PASS: broker rejected cross-tenant publication.', flush=True)
            message = str(uuid.uuid4())
            payload = {'schemaVersion': 1, 'tenantId': tenant, 'deviceId': device, 'messageId': message, 'bootId': str(uuid.uuid4()), 'sequence': 1,
                       'snapshot': {'observedAt': dt.datetime.now(dt.timezone.utc).isoformat(), 'hostname': 'disposable-connectivity-check', 'platform': 'Diagnostic fixture',
                       'cpuPercent': None, 'memoryTotalBytes': None, 'memoryAvailableBytes': None, 'temperatureC': None, 'uptimeSeconds': 0,
                       'storage': {'budgetBytes': 0, 'reserveBytes': 0, 'usedBytes': 0, 'freeBytes': 0, 'underPressure': False, 'classLimits': {}, 'totalBytes': 0},
                       'interfaces': [], 'clockState': 'Diagnostic fixture'}}
            topic = 'pidock/v1/tenants/' + tenant + '/devices/' + device
            receipt = json.loads(run(*command, '--entrypoint', 'mosquitto_rr', IMAGE, *tls, '-q', '1', '-W', '25', '-t', topic + '/telemetry', '-e', topic + '/receipts', '-m', json.dumps(payload)))
            if receipt.get('messageId') != message or receipt.get('tenantId') != tenant:
                raise RuntimeError('No matching persistence receipt was returned.')
            fleet = json.loads((root / 'fleet.json').read_text())
            if not any(entry['tenantId'] == tenant and entry['lastMessageId'] == message for entry in fleet):
                raise RuntimeError('Acknowledged telemetry was not saved.')
            print('PASS: certificate MQTT telemetry persisted and its scoped receipt returned.', flush=True)
        finally:
            subprocess.run(['docker', 'rm', '--force', test_client], capture_output=True)
            run('docker', 'stop', '--time', '20', CLOUD)
            try:
                for path in (invitation_path, root / 'fleet.json'):
                    if path.exists():
                        records = json.loads(path.read_text())
                        save(path, [entry for entry in records if entry.get('tenantId') != tenant])
                for path in (root / 'telemetry-archive').glob('*.json'):
                    if json.loads(path.read_text()).get('envelope', {}).get('tenantId') == tenant:
                        path.unlink()
            finally:
                run('docker', 'start', CLOUD)
    ready()
    print('PASS: disposable registration, telemetry and private key removed; cloud healthy.', flush=True)


if __name__ == '__main__':
    with open('/run/lock/pidock-storage-development.lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        main()
