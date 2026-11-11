#!/usr/bin/env python3
"""Run on a Linux Docker host: test replacement and encrypted recovery using disposable volumes."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import uuid

spec = importlib.util.spec_from_file_location('pidock_storage', Path(__file__).with_name('cloud-storage.py'))
storage_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage_module)


def main():
    if os.geteuid() != 0 or os.uname().sysname != 'Linux':
        raise RuntimeError('Run as root on the Linux Docker host.')
    os.umask(0o077)
    environment = 'test-' + uuid.uuid4().hex[:8]
    storage = storage_module.Storage(environment)
    verifier = Path(os.environ.get('PIDOCK_STORAGE_VERIFIER', '/opt/pidock/storage-verifier')).resolve(strict=True)
    runtime = 'mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e'
    staging = None
    busy_container = None

    def execute(operation, mount):
        result = subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--read-only', '--tmpfs', '/tmp', '--cap-drop', 'ALL',
                                 '--security-opt', 'no-new-privileges:true', '--user', '1654:1654', '--entrypoint', 'dotnet',
                                 '-e', 'PIDOCK_STORAGE_TEST=1', '--mount', 'type=bind,src=' + str(verifier) + ',dst=/verification,readonly',
                                 '--mount', mount, runtime, '/verification/PiDock.Storage.Verify.dll', operation, '/state'],
                                check=True, capture_output=True, text=True)
        print(result.stdout.strip(), flush=True)

    try:
        storage.prepare()
        name = storage.volumes['cloud'][0]
        execute('seed', 'type=volume,src=' + name + ',dst=/state')
        # A second fresh container reuses the retained volume, with a new process/key-ring provider.
        execute('verify', 'type=volume,src=' + name + ',dst=/state')
        if os.environ.get('PIDOCK_VERIFY_PRX_AGENT') == '1':
            agent_env = os.environ.copy()
            agent_env.update({'PRX_STORAGE_REAL_DOCKER': '1', 'PRX_STORAGE_ENVIRONMENT': environment,
                              'DOCKER_CONFIG': '/etc/prox3mus-agent/docker'})
            result = subprocess.run(['/opt/pidock/Prox3mus.AgentVerification'], env=agent_env, check=True, capture_output=True, text=True)
            print(result.stdout.strip(), flush=True)
            execute('verify', 'type=volume,src=' + name + ',dst=/state')
        busy_container = storage_module.run('docker', 'run', '-d', '--network', 'none', '--read-only', '--cap-drop', 'ALL',
                                            '--user', '1654:1654', '--entrypoint', '/bin/sleep', '--mount', 'type=volume,src=' + name + ',dst=/state', runtime, '60')
        try:
            storage.backup()
        except RuntimeError as error:
            if not str(error).startswith('Stop PiDockCloud'):
                raise
        else:
            raise RuntimeError('Backup accepted an active volume consumer.')
        storage_module.run('docker', 'rm', '--force', busy_container); busy_container = None
        snapshot = storage.backup()['snapshot']; storage.check()
        restored = storage.restore_test(snapshot, retain=True)
        staging = Path(restored['stagingDirectory'])
        state = staging / str(storage.volume('cloud')).lstrip('/')
        execute('verify', 'type=bind,src=' + str(state) + ',dst=/state')
        # Ordinary Compose-style consumer removal did not remove the source volume.
        if not (storage.volume('cloud') / 'accounts.protected').is_file():
            raise RuntimeError('Source state disappeared.')
        print(json.dumps({'containerReplacement': 'passed', 'encryptedBackup': 'passed', 'restoredAuthentication': 'passed',
                          'activeConsumerGuard': 'passed', 'restoredFilesChecked': restored['filesChecked']}), flush=True)
    finally:
        if busy_container is not None:
            storage_module.run('docker', 'rm', '--force', busy_container)
        if staging is not None:
            shutil.rmtree(staging)
        storage.cleanup_test()


if __name__ == '__main__':
    main()
