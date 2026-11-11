#!/usr/bin/env python3
"""Prepare PiDock's external Docker volumes and encrypted, offline restic backups.

Run on the Linux Docker host as root. No command restores over live storage or
deletes a production volume. Backup consumers must be stopped beforehand.
"""
import argparse
import fcntl
import grp
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import stat
import subprocess
import sys
import uuid


def run(*command):
    result = subprocess.run(command, check=True, capture_output=True, text=True)
    return result.stdout.strip()


def safe_path(path):
    path = Path(path)
    for parent in [path, *path.parents]:
        if parent.is_symlink():
            raise RuntimeError('Symbolic links are not allowed in storage paths: ' + str(parent))
    return path


def private_directory(path):
    path = safe_path(path)
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    if path.stat().st_uid != 0:
        raise RuntimeError('Storage administration directories must belong to root: ' + str(path))
    path.chmod(0o700)
    return path


def write_private(path, value):
    safe_path(path)
    temporary = path.with_name(uuid.uuid4().hex + '.tmp')
    try:
        with open(temporary, 'x', opener=lambda name, flags: os.open(name, flags, 0o600)) as stream:
            stream.write(value); stream.flush(); os.fsync(stream.fileno())
        temporary.replace(path)
        descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


class Storage:
    def __init__(self, environment):
        if environment not in ('development', 'production') and not re.fullmatch(r'test-[a-f0-9]{8}', environment):
            raise ValueError('Choose development or production; test instances require test-<8 hex digits>.')
        self.environment = environment
        self.settings = Path('/etc/pidock') / environment
        self.backups = Path('/var/backups/pidock') / environment
        self.secrets = self.settings / 'secrets'
        self.configuration = self.settings / 'configuration'
        self.password = self.settings / 'backup-password'
        self.repository = self.backups / 'repository'
        self.volumes = {'cloud': (f'pidock-{environment}-cloud', 1654), 'broker': (f'pidock-{environment}-broker', 1883)}

    def volume(self, role, create=False):
        name, uid = self.volumes[role]
        found = subprocess.run(['docker', 'volume', 'inspect', name], capture_output=True, text=True)
        if found.returncode:
            if not create:
                raise RuntimeError('Missing external volume: ' + name)
            # A failed daemon connection is not evidence that a volume is absent.
            run('docker', 'info', '--format', '{{.ServerVersion}}')
            run('docker', 'volume', 'create', '--label', 'io.pidock.managed=true', '--label', 'io.pidock.environment=' + self.environment,
                '--label', 'io.pidock.role=' + role, name)
            item = json.loads(run('docker', 'volume', 'inspect', name))[0]
        else:
            item = json.loads(found.stdout)[0]
        labels = item.get('Labels') or {}
        if item['Driver'] != 'local' or item.get('Options') or labels.get('io.pidock.managed') != 'true' or labels.get('io.pidock.environment') != self.environment or labels.get('io.pidock.role') != role:
            raise RuntimeError('Refusing to adopt an unrecognized volume: ' + name)
        root = Path(run('docker', 'info', '--format', '{{.DockerRootDir}}'))
        expected = root / 'volumes' / name / '_data'
        path = safe_path(item['Mountpoint'])
        if path != expected or not path.is_dir():
            raise RuntimeError('A local volume on this Docker host is required.')
        info = path.stat()
        if (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)) != (uid, uid, 0o700):
            if not create or any(path.iterdir()):
                raise RuntimeError('Existing volume permissions require manual review: ' + name)
            path.chmod(0o700); os.chown(path, uid, uid)
        return path

    def restic(self, *arguments):
        safe_path(self.password)
        if not self.password.is_file() or stat.S_IMODE(self.password.stat().st_mode) != 0o600 or self.password.stat().st_uid != 0:
            raise RuntimeError('An owner-only root backup password file is required.')
        return run('restic', '--repo', str(self.repository), '--password-file', str(self.password),
                   '--cache-dir', str(self.backups / 'cache'), *arguments)

    def prepare(self):
        if not shutil.which('restic'):
            raise RuntimeError('Install restic from your maintained OS repository first.')
        for path in (self.settings, self.backups, self.configuration, self.secrets, self.secrets / 'mqtt', self.secrets / 'enrollment'):
            private_directory(path)
        for role in self.volumes:
            self.volume(role, create=True)
        if not self.password.exists():
            if (self.repository / 'config').exists():
                raise RuntimeError('The backup repository exists but its password is missing. Restore the recovery key; do not generate a replacement.')
            write_private(self.password, secrets.token_urlsafe(48) + '\n')
        if not (self.repository / 'config').exists():
            if self.repository.exists() and any(self.repository.iterdir()):
                raise RuntimeError('Refusing to initialize a nonempty backup repository.')
            self.restic('init')
        else:
            self.restic('snapshots', '--json')  # Verify that the retained key opens the repository.
        runtime = self.configuration / 'storage.env'
        if not runtime.exists():
            hostname = {'development': 'pidock-dev.knowledgelocker.ai', 'production': 'pidock.knowledgelocker.ai'}.get(self.environment, 'localhost')
            write_private(runtime, '\n'.join([
                'PIDOCK_ENVIRONMENT=' + self.environment, 'PIDOCK_HOSTNAME=' + hostname,
                'PIDOCK_CLOUD_VOLUME=' + self.volumes['cloud'][0], 'PIDOCK_BROKER_VOLUME=' + self.volumes['broker'][0],
                'PIDOCK_SECRETS_DIRECTORY=' + str(self.secrets),
                'PIDOCK_CLOUD_IMAGE=ghcr.io/knowledge-locker/pidockplatform@sha256:8e673129ab177ca152d17591beb51ad485ffc7814435162469aafaf843cb8a9f',
                'PIDOCK_PROXY_ADDRESS=CONFIGURE_TRUSTED_PROXY_IP', 'PIDOCK_HTTP_PORT=10080', 'PIDOCK_MQTT_PORT=18883', ''
            ]))
        # Let the already-privileged host agent traverse known paths for mount
        # validation, without granting it file-content access to private keys.
        try:
            agent_group = grp.getgrnam('prox3mus-agent').gr_gid
        except KeyError:
            agent_group = None
        if agent_group is not None:
            for path in (self.settings.parent, self.settings, self.secrets, self.secrets / 'mqtt', self.secrets / 'enrollment'):
                os.chown(path, 0, agent_group); path.chmod(0o710)
            os.chown(self.configuration, 0, agent_group); self.configuration.chmod(0o750)
        return self.status()

    def status(self):
        return {'environment': self.environment, 'volumes': {role: {'name': name, 'uid': uid, 'mode': '0700', 'path': str(self.volume(role))}
                for role, (name, uid) in self.volumes.items()}, 'secretsDirectory': str(self.secrets), 'encryptedRepository': str(self.repository)}

    def consumers(self):
        found = set()
        for name, _ in self.volumes.values():
            found.update(run('docker', 'ps', '--filter', 'volume=' + name, '--format', '{{.ID}}').splitlines())
        return sorted(found)

    def backup(self):
        if self.consumers():
            raise RuntimeError('Stop PiDockCloud and its MQTT broker before backing up their JSON/key state. No services were stopped automatically.')
        roots = {role: self.volume(role) for role in self.volumes}
        roots['secrets'] = safe_path(self.secrets)
        roots['configuration'] = safe_path(self.configuration)
        manifest = {role: {'source': str(path), 'files': inventory(path)} for role, path in roots.items()}
        manifest_path = self.settings / 'backup-manifest.json'
        write_private(manifest_path, json.dumps(manifest, sort_keys=True))
        result = self.restic('backup', '--json', '--host', 'pidock-' + self.environment, '--tag', 'pidock-' + self.environment,
                             *[str(path) for path in roots.values()], str(manifest_path))
        if self.consumers():
            raise RuntimeError('A PiDock service started during backup. Repeat the backup in a coordinated maintenance window.')
        summary = [json.loads(line) for line in result.splitlines() if line.startswith('{') and json.loads(line).get('message_type') == 'summary']
        if not summary or not summary[-1].get('snapshot_id'):
            raise RuntimeError('No completed backup snapshot was reported.')
        return {'environment': self.environment, 'snapshot': summary[-1]['snapshot_id']}

    def check(self):
        self.restic('check', '--read-data')
        return {'environment': self.environment, 'encryptedDataCheck': 'passed'}

    def restore_test(self, snapshot='latest', retain=False):
        if snapshot != 'latest' and not re.fullmatch(r'[a-f0-9]{8,64}', snapshot):
            raise ValueError('Use latest or an exact snapshot ID.')
        target = private_directory(self.backups / ('restore-test-' + uuid.uuid4().hex))
        try:
            self.restic('restore', snapshot, '--host', 'pidock-' + self.environment, '--tag', 'pidock-' + self.environment,
                        '--target', str(target), '--verify')
            manifest_path = target / str(self.settings / 'backup-manifest.json').lstrip('/')
            manifest = json.loads(manifest_path.read_text())
            expected_roots = {role: str(self.volume(role)) for role in self.volumes} | {'secrets': str(self.secrets), 'configuration': str(self.configuration)}
            if {role: entry['source'] for role, entry in manifest.items()} != expected_roots:
                raise RuntimeError('Snapshot roots do not match this storage instance.')
            for role, entry in manifest.items():
                if inventory(target / entry['source'].lstrip('/')) != entry['files']:
                    raise RuntimeError('Restored content or file permissions differ for ' + role)
            result = {'environment': self.environment, 'restoreCheck': 'passed', 'filesChecked': sum(len(e['files']) for e in manifest.values())}
            if retain:
                result['stagingDirectory'] = str(target)
            return result
        finally:
            if not retain:
                shutil.rmtree(target)

    def cleanup_test(self):
        if not self.environment.startswith('test-'):
            raise RuntimeError('Only disposable test instances can be removed by this tool.')
        if self.consumers():
            raise RuntimeError('Stop test consumers first.')
        for role, (name, _) in self.volumes.items():
            self.volume(role)  # Check labels and host path before removing anything.
            run('docker', 'volume', 'rm', name)
        for path in (self.settings, self.backups):
            shutil.rmtree(safe_path(path))


def inventory(root):
    result = {}
    for path in [root, *sorted(root.rglob('*'))]:
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)):
            raise RuntimeError('Only regular files and directories can be backed up: ' + str(path))
        value = {'mode': stat.S_IMODE(info.st_mode), 'uid': info.st_uid, 'gid': info.st_gid, 'kind': 'directory' if path.is_dir() else 'file'}
        if path.is_file():
            with path.open('rb') as stream:
                value['sha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
        result[str(path.relative_to(root))] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['prepare', 'status', 'backup', 'check', 'restore-test', 'cleanup-test'])
    parser.add_argument('--environment', required=True)
    parser.add_argument('--snapshot', default='latest')
    args = parser.parse_args()
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise RuntimeError('Run on the Linux Docker host as root.')
    os.umask(0o077)
    storage = Storage(args.environment)
    lock_path = safe_path(Path('/run/lock') / ('pidock-storage-' + args.environment + '.lock'))
    with open(lock_path, 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = storage.restore_test(args.snapshot) if args.operation == 'restore-test' else getattr(storage, args.operation.replace('-', '_'))()
        print(json.dumps(result or {'operation': args.operation, 'status': 'complete'}, sort_keys=True))


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, OSError, subprocess.CalledProcessError) as error:
        # Do not print subprocess output: a backup tool may describe private files.
        print('Storage operation failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
