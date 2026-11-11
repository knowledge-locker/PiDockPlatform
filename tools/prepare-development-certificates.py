#!/usr/bin/env python3
"""Create independent, short-lived PiDock development PKI on the Linux host; never rotate existing identities."""
import grp
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile

HOST = 'pidock-dev.knowledgelocker.ai'
ROOT = Path('/etc/pidock/development/secrets')


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout.strip()


def main():
    if os.uname().sysname != 'Linux' or os.geteuid() != 0:
        raise RuntimeError('Run as root on the development Linux host.')
    os.umask(0o077)
    for path in [ROOT, *ROOT.parents]:
        if path.is_symlink():
            raise RuntimeError('Certificate paths must not contain symbolic links.')
    targets = [ROOT / 'mqtt' / name for name in ('ca.crt', 'broker.crt', 'broker.key', 'worker.crt', 'worker.key')]
    targets += [ROOT / 'enrollment' / name for name in ('issuer.crt', 'issuer.key')]
    if any(path.exists() or path.is_symlink() for path in targets):
        raise RuntimeError('Existing or partially provisioned identities require operator review; nothing was replaced.')
    if not ROOT.is_dir():
        raise RuntimeError('Prepare development storage first.')
    group = grp.getgrnam('prox3mus-agent').gr_gid
    with tempfile.TemporaryDirectory(prefix='certificate-setup-', dir=ROOT) as temporary:
        stage = Path(temporary)
        ca_key, ca_cert = stage / 'ca.key', stage / 'ca.crt'
        run('openssl', 'req', '-x509', '-newkey', 'rsa:4096', '-nodes', '-sha256', '-days', '365',
            '-keyout', str(ca_key), '-out', str(ca_cert), '-subj', '/CN=PiDock Development CA',
            '-addext', 'basicConstraints=critical,CA:TRUE,pathlen:0', '-addext', 'keyUsage=critical,keyCertSign,cRLSign')
        for name, subject, purpose in [('broker', HOST, 'serverAuth'), ('worker', 'pidock-ingest', 'clientAuth')]:
            run('openssl', 'req', '-new', '-newkey', 'rsa:3072', '-nodes', '-sha256',
                '-keyout', str(stage / (name + '.key')), '-out', str(stage / (name + '.csr')), '-subj', '/CN=' + subject)
            extension = stage / (name + '.ext')
            extension.write_text('basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=' + purpose + '\n' +
                                 ('subjectAltName=DNS:' + HOST + ',DNS:pidock-development-broker\n' if name == 'broker' else ''))
            run('openssl', 'x509', '-req', '-in', str(stage / (name + '.csr')), '-CA', str(ca_cert), '-CAkey', str(ca_key),
                '-set_serial', '0x' + secrets.token_hex(16), '-days', '90', '-sha256', '-extfile', str(extension), '-out', str(stage / (name + '.crt')))
            run('openssl', 'verify', '-CAfile', str(ca_cert), '-purpose', 'sslserver' if name == 'broker' else 'sslclient', str(stage / (name + '.crt')))
        assignments = [(ca_cert, ROOT / 'mqtt/ca.crt', 1654, 1883, 0o440), (ca_cert, ROOT / 'enrollment/issuer.crt', 1654, 1654, 0o400),
                       (ca_key, ROOT / 'enrollment/issuer.key', 1654, 1654, 0o400)]
        for name, uid in [('broker', 1883), ('worker', 1654)]:
            for suffix in ('.crt', '.key'):
                assignments.append((stage / (name + suffix), ROOT / 'mqtt' / (name + suffix), uid, uid, 0o400))
        # Validate every certificate before installing any material. Never print a private key.
        for source, target, uid, gid, mode in assignments:
            target.parent.mkdir(exist_ok=True)
            os.chown(target.parent, 0, group); target.parent.chmod(0o710)
            with target.open('xb') as stream:
                stream.write(source.read_bytes()); stream.flush(); os.fsync(stream.fileno())
            os.chown(target, uid, gid); target.chmod(mode)
    for name in ('ca', 'broker', 'worker'):
        certificate = ROOT / ('mqtt/' + name + '.crt')
        print(json.dumps({'certificate': name, 'metadata': run('openssl', 'x509', '-in', str(certificate), '-noout', '-subject', '-enddate', '-fingerprint', '-sha256')}))


if __name__ == '__main__':
    main()
