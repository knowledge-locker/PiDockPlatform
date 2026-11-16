#!/usr/bin/env python3
"""Create a private managed-data filesystem. Factory system/boot images remain credential-free."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--owner',required=True,type=Path)
    parser.add_argument('--identity',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--network',type=Path)
    parser.add_argument('--bytes',required=True,type=int)
    parser.add_argument('--fakeroot',required=True)
    args=parser.parse_args()
    if args.bytes<1<<30 or args.bytes%4096:raise ValueError('Invalid managed-data filesystem size')
    with tempfile.TemporaryDirectory() as temporary:
        seed=Path(temporary);appliance=seed/'lib/pidock/appliance';keys=appliance/'keys';ca=seed/'lib/pidock/identity/ca'
        keys.mkdir(parents=True);ca.mkdir(parents=True)
        account=args.owner/'accounts.protected'
        if account.is_symlink() or not account.is_file() or not 0<account.stat().st_size<=65536:raise ValueError('Private owner setup is missing or invalid')
        shutil.copy2(account,appliance/account.name)
        sources=list((args.owner/'keys').iterdir())
        if not 1<=len(sources)<=64:raise ValueError('Invalid private key-ring count')
        for source in sources:
            if source.is_symlink() or not source.is_file() or not re.fullmatch(r'key-[0-9a-fA-F-]{36}\.xml',source.name) or not 0<source.stat().st_size<=65536:raise ValueError('Invalid private key-ring file')
            shutil.copy2(source,keys/source.name)
        for name in ('ca.key','ca.crt'):
            source=args.identity/name
            if source.is_symlink() or not source.is_file() or not 0<source.stat().st_size<=65536:raise ValueError('Private local TLS identity is missing')
            shutil.copy2(source,ca/name)
        if args.network is not None:
            source=args.network
            if source.is_symlink() or not source.is_file() or not 0<source.stat().st_size<=16384 or source.stat().st_mode&0o077:raise ValueError('Private Wi-Fi setup is missing or has unsafe permissions')
            network=seed/'lib/pidock/network';network.mkdir(parents=True)
            shutil.copy2(source,network/'wpa_supplicant.conf')
        # No GPIO grants or cloud permissions are imported from another appliance.
        for directory in seed.rglob('*'):
            if directory.is_dir():directory.chmod(0o755)
            elif directory.is_file():directory.chmod(0o600)
        appliance.chmod(0o700);keys.chmod(0o700);(ca/'ca.crt').chmod(0o644)
        with args.output.open('xb') as output:
            os.chmod(args.output,0o600);output.truncate(args.bytes)
        # fakeroot records filesystem ownership without granting the builder privileges.
        mkfs=Path(args.fakeroot).parent.parent/'sbin/mke2fs'
        script='set -eu; chown -R 0:0 "$1"; chown -R 1654:1654 "$1/lib/pidock/appliance"; "$3" -q -F -t ext4 -L pidock-data -E root_owner=0:0 -d "$1" "$2"'
        subprocess.run([args.fakeroot,'--','sh','-c',script,'personalize',str(seed),str(args.output),str(mkfs)],check=True)
        print('Private owner and local TLS identity added to the managed-data filesystem; no credentials were printed.')

if __name__=='__main__':main()
