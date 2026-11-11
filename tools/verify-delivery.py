#!/usr/bin/env python3
"""Exercise durable telemetry receipts across an isolated Docker broker outage."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import uuid
import threading
import socketserver
from http.server import BaseHTTPRequestHandler

REPO = Path(__file__).resolve().parents[1]


def run(args, **kwargs):
    return subprocess.run(args, cwd=REPO, check=True, capture_output=True, text=True, **kwargs).stdout.strip()


def wait_for(predicate, timeout, description):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(1)
    raise RuntimeError('Timed out: ' + description)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', default='pidock-preview-cloud')
    parser.add_argument('--enrollment', action='store_true', help='Issue a new device identity through the real HTTPS enrollment endpoint before testing delivery')
    parser.add_argument("--remote-command", action="store_true", help="Also exercise an opt-in, expiring MQTT container command against a disposable fake Unix broker")
    args = parser.parse_args()
    pki = REPO / '.state/pki'
    for name in ['ca.crt', 'broker.crt', 'broker.key', 'worker.crt', 'worker.key', 'device.crt', 'device.key', 'web.crt', 'web.key']:
        if not (pki / name).is_file():
            raise RuntimeError('Prepare the local preview PKI first with deploy/prepare-preview.sh')
    prefix = 'pidock-delivery-' + uuid.uuid4().hex[:8]
    scratch = Path(tempfile.mkdtemp(prefix=prefix + '-', dir='/private/tmp' if os.uname().sysname == 'Darwin' else None))
    scratch.chmod(0o700)
    for name in ['broker', 'cloud', 'agent']:
        (scratch / name).mkdir(mode=0o700)
    broker, cloud = prefix + '-broker', prefix + '-cloud'
    agent = None
    fake_broker = None
    runtime_calls = []
    command_id = str(uuid.uuid4())
    logs = open(scratch / 'agent.log', 'w')
    user = str(os.getuid()) + ':' + str(os.getgid())

    def mount(source, target):
        return ['--mount', f'type=bind,src={source},dst={target},readonly']

    def records():
        result = []
        for path in (scratch / 'agent/history/Telemetry').glob('*.json'):
            try:
                value = json.loads(path.read_text())
                if 'envelope' in value:
                    result.append(value)
            except (OSError, json.JSONDecodeError):
                pass
        return result

    try:
        if args.remote_command:
            from datetime import datetime, timedelta, timezone
            now = datetime.now(timezone.utc)
            container_id = 'a' * 64
            pending = {'command': {'id': command_id, 'tenantId': 'local', 'deviceId': 'pi-enrolled' if args.enrollment else 'pi5',
                       'action': 'stop', 'containerId': container_id, 'issuedAt': now.isoformat(), 'expiresAt': (now + timedelta(minutes=2)).isoformat(), 'requestedBy': 'fixture-owner'}, 'result': None}
            (scratch / 'cloud/application-commands.json').write_text(json.dumps([pending]))
            (scratch / 'agent/remote-applications.json').write_text(json.dumps({'enabled': True, 'processed': []}))
            class RuntimeHandler(BaseHTTPRequestHandler):
                def log_message(self, *args): pass
                def respond(self, body, status=200):
                    encoded = json.dumps(body).encode(); self.send_response(status); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(encoded))); self.end_headers(); self.wfile.write(encoded)
                def do_GET(self):
                    self.respond([{'id': container_id, 'names': ['fixture-app'], 'image': 'fixture/image', 'state': 'running', 'status': 'Up'}])
                def do_POST(self):
                    if self.path != '/applications/' + container_id + '/stop': self.respond({}, 403); return
                    runtime_calls.append(self.path); self.respond({})
            fake_broker = socketserver.UnixStreamServer(str(scratch / 'runtime.sock'), RuntimeHandler)
            threading.Thread(target=fake_broker.serve_forever, daemon=True).start()
        run(['docker', 'network', 'create', prefix])
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            selected_port = str(reservation.getsockname()[1])
        command = ['docker', 'run', '-d', '--name', broker, '--network', prefix, '--network-alias', 'broker', '--user', user,
                   '-p', '127.0.0.1:' + selected_port + ':8883', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--read-only', '--tmpfs', '/tmp']
        command += mount(REPO / 'deploy/mosquitto/mosquitto.conf', '/mosquitto/config/mosquitto.conf')
        command += mount(REPO / 'deploy/mosquitto/acl', '/mosquitto/config/acl')
        for name in ['ca.crt', 'broker.crt', 'broker.key']:
            command += mount(pki / name, '/certs/' + name)
        command += ['--mount', f'type=bind,src={scratch / "broker"},dst=/mosquitto/data', 'eclipse-mosquitto:2.0.22@sha256:212f89e1eaeb2c322d6441b64396e3346026674db8fa9c27beac293405c32b3c']
        run(command)
        binding = json.loads(run(['docker', 'inspect', broker]))[0]['NetworkSettings']['Ports']['8883/tcp'][0]['HostPort']
        command = ['docker', 'run', '-d', '--name', cloud, '--network', prefix, '--user', user,
                   '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--read-only', '--tmpfs', '/tmp',
                   '--mount', f'type=bind,src={scratch / "cloud"},dst=/var/lib/pidock']
        if args.enrollment:
            command += ['-p', '127.0.0.1::8443']
            command += mount(pki / 'ca.key', '/enrollment/ca.key')
            for value in ['Enrollment__IssuerCertificatePath=/certs/ca.crt', 'Enrollment__IssuerKeyPath=/enrollment/ca.key',
                          'Enrollment__BrokerCaPath=/certs/ca.crt', 'Enrollment__MqttHost=localhost', 'Enrollment__MqttPort=' + binding]:
                command += ['-e', value]
            enrollment_code = uuid.uuid4().hex.upper()
            from datetime import datetime, timedelta, timezone
            now = datetime.now(timezone.utc)
            invitation = {'id': str(uuid.uuid4()), 'tenantId': 'local', 'deviceId': 'pi-enrolled', 'name': 'Isolated enrollment test',
                          'createdBy': 'test-fixture', 'createdAt': now.isoformat(), 'expiresAt': (now + timedelta(minutes=10)).isoformat(),
                          'codeHash': hashlib.sha256(enrollment_code.encode()).hexdigest().upper(), 'state': 'Pending'}
            (scratch / 'cloud/enrollments.json').write_text(json.dumps([invitation]))
        for name in ['ca.crt', 'worker.crt', 'worker.key', 'web.crt', 'web.key']:
            command += mount(pki / name, '/certs/' + name)
        for value in ['Mqtt__Enabled=true', 'Mqtt__Host=broker', 'Mqtt__CertificatePath=/certs/worker.crt',
                      'Mqtt__PrivateKeyPath=/certs/worker.key', 'Mqtt__CaPath=/certs/ca.crt']:
            command += ['-e', value]
        run(command + [args.image])
        device_certificate, device_key, device_id = pki / 'device.crt', pki / 'device.key', 'pi5'
        if args.enrollment:
            cloud_port = json.loads(run(['docker', 'inspect', cloud]))[0]['NetworkSettings']['Ports']['8443/tcp'][0]['HostPort']
            endpoint = 'https://localhost:' + cloud_port
            def cloud_ready():
                result = subprocess.run(['curl', '--silent', '--fail', '--cacert', str(pki / 'ca.crt'), endpoint + '/health/live'], capture_output=True)
                return result.returncode == 0
            wait_for(cloud_ready, 45, 'HTTPS enrollment endpoint')
            device_key, device_certificate, device_id = scratch / 'agent/device.key', scratch / 'agent/device.crt', 'pi-enrolled'
            csr = scratch / 'agent/device.csr'
            run(['openssl', 'req', '-new', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(device_key), '-out', str(csr), '-subj', '/CN=untrusted-requested-name'])
            device_key.chmod(0o600)
            request_path = scratch / 'request.json'
            request_path.write_text(json.dumps({'code': enrollment_code, 'csrPem': csr.read_text()})); request_path.chmod(0o600)
            reply = json.loads(run(['curl', '--silent', '--show-error', '--fail', '--cacert', str(pki / 'ca.crt'), '-H', 'Content-Type: application/json',
                                    '--data-binary', '@' + str(request_path), endpoint + '/api/enrollment/redeem']))
            device_certificate.write_text(reply['certificatePem'])
            if reply['deviceId'] != device_id or reply['tenantId'] != 'local':
                raise RuntimeError('Enrollment returned an unexpected device identity')
            print('PASS: single-use HTTPS enrollment issued a device certificate', flush=True)
            check = ['docker', 'run', '--rm', '--network', prefix, '--entrypoint', 'mosquitto_pub']
            for source, target in [(pki / 'ca.crt', '/test/ca.crt'), (device_certificate, '/test/device.crt'), (device_key, '/test/device.key')]:
                check += mount(source, target)
            check += ['eclipse-mosquitto:2.0.22@sha256:212f89e1eaeb2c322d6441b64396e3346026674db8fa9c27beac293405c32b3c',
                      '-h', 'broker', '-p', '8883', '--cafile', '/test/ca.crt', '--cert', '/test/device.crt', '--key', '/test/device.key',
                      '-V', 'mqttv5', '-q', '1', '-t', 'pidock/v1/tenants/other/devices/pi-enrolled/telemetry', '-m', '{}', '-d']
            denied = subprocess.run(check, capture_output=True, text=True)
            if 'Not authorized' not in denied.stdout + denied.stderr:
                raise RuntimeError('Wrong-tenant MQTT publication was not denied')
            print('PASS: enrolled certificate cannot publish into another tenant', flush=True)
        env = os.environ.copy()
        env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'PiDock__StateDirectory': str(scratch / 'agent'),
                    'Mqtt__Enabled': 'true', 'Mqtt__Host': 'localhost', 'Mqtt__Port': binding,
                    'Mqtt__TenantId': 'local', 'Mqtt__DeviceId': device_id,
                    'Mqtt__CertificatePath': str(device_certificate), 'Mqtt__PrivateKeyPath': str(device_key),
                    'Mqtt__CaPath': str(pki / 'ca.crt')})
        if args.remote_command: env["Broker__SocketPath"] = str(scratch / "runtime.sock")
        agent = subprocess.Popen(['dotnet', 'run', '-c', 'Release', '--no-build', '--no-launch-profile', '--project', 'src/PiDock.Appliance.Web',
                                  '--urls', 'http://127.0.0.1:0'], cwd=REPO, env=env, stdout=logs, stderr=logs, start_new_session=True)
        wait_for(lambda: any(not r['pending'] for r in records()), 45, 'first durable cloud receipt')
        print('PASS: first sample acknowledged only after cloud persistence', flush=True)
        if args.remote_command:
            def completed():
                entries = json.loads((scratch / 'cloud/application-commands.json').read_text())
                return any(e['command']['id'] == command_id and e.get('result', {}).get('state') == 'Completed' for e in entries if e.get('result'))
            wait_for(completed, 75, 'MQTT container command completion in cloud telemetry')
            if len(runtime_calls) != 1: raise RuntimeError('Remote command was lost or executed more than once.')
            print('PASS: scoped expiring command travelled in a TLS receipt, executed once and its result returned through telemetry', flush=True)
        run(['docker', 'stop', '-t', '3', broker])
        wait_for(lambda: sum(r['pending'] for r in records()) >= 2, 75, 'two samples buffered while broker is unavailable')
        print('PASS: local sampling continues with the broker stopped', flush=True)
        run(['docker', 'start', broker])
        restarted_port = json.loads(run(['docker', 'inspect', broker]))[0]['NetworkSettings']['Ports']['8883/tcp'][0]['HostPort']
        if restarted_port != binding:
            raise RuntimeError('Broker test endpoint changed during restart')
        wait_for(lambda: records() and all(not r['pending'] for r in records()), 90, 'buffered samples receive cloud receipts after reconnect')
        archived = [json.loads(p.read_text()) for p in (scratch / 'cloud/telemetry-archive').glob('*.json')]
        if not any(item['envelope']['isReplay'] for item in archived):
            raise RuntimeError('No historical replay reached the cloud archive')
        ids = [item['envelope']['messageId'] for item in archived]
        if len(ids) != len(set(ids)):
            raise RuntimeError('Duplicate telemetry archive entries')
        print('PASS: reconnect drained the bounded queue with deduplicated historical replay', flush=True)
        print(json.dumps({'archivedSamples': len(archived), 'pendingSamples': sum(r['pending'] for r in records())}), flush=True)
    except Exception:
        for name in [broker, cloud]:
            result = subprocess.run(['docker', 'logs', '--tail', '15', name], capture_output=True, text=True)
            print(result.stdout + result.stderr)
        logs.flush()
        print((scratch / 'agent.log').read_text()[-6000:])
        raise
    finally:
        if agent is not None:
            try:
                os.killpg(agent.pid, signal.SIGTERM)
                agent.wait(timeout=10)
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline:
                    try:
                        os.killpg(agent.pid, 0)
                    except ProcessLookupError:
                        break
                    time.sleep(.1)
                else:
                    os.killpg(agent.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except subprocess.TimeoutExpired:
                os.killpg(agent.pid, signal.SIGKILL); agent.wait(timeout=5)
        if fake_broker is not None: fake_broker.shutdown(); fake_broker.server_close()
        logs.close()
        for name in [cloud, broker]:
            subprocess.run(['docker', 'rm', '-f', name], capture_output=True)
        subprocess.run(['docker', 'network', 'rm', prefix], capture_output=True)
        shutil.rmtree(scratch)


if __name__ == '__main__':
    main()
