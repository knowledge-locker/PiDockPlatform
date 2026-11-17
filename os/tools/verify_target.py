#!/usr/bin/env python3
"""Check a public target filesystem before creating signed boot images. Never reads private owner setup."""
import argparse,json,os,shlex,stat
from pathlib import Path
from image_components import resolve_target

def verify_updater_connection(units):
    appliance=(units/'pidock-appliance.service').read_text()
    if 'Environment=Updates__SocketPath=/run/pidock-updater/update.sock\n' not in appliance:
        raise ValueError('Appliance must connect to the private OS updater socket')
    updater=(units/'pidock-updater.service').read_text()
    if 'Group=pidock\n' not in updater or 'RuntimeDirectory=pidock-updater pidock-image\n' not in updater or 'RuntimeDirectoryMode=0750\n' not in updater or 'UMask=0007\n' not in updater:
        raise ValueError('Updater socket must be accessible to the appliance group only')

def verify(target):
    units=target/'usr/lib/systemd/system'
    verify_updater_connection(units)
    checked=[]
    for unit in sorted(units.glob('pidock-*.service')):
        for line in unit.read_text().splitlines():
            if line.startswith(('ExecStart=','ExecStartPost=','ExecStartPre=')):
                command=shlex.split(line.split('=',1)[1])[0].lstrip('-:+!@')
                executable=resolve_target(target,command)
                if not executable.is_file() or not executable.stat().st_mode&0o111:raise ValueError('Image service executable missing: '+unit.name+' '+command)
                checked.append(command)
    for link in (target/'etc/systemd/system').rglob('*'):
        if link.is_symlink():
            destination=os.readlink(link)
            if destination=='/dev/null':continue
            if not resolve_target(target,'/'+str(link.relative_to(target))).exists():raise ValueError('Dangling enabled service: '+str(link.relative_to(target)))
    broker=(units/'pidock-broker.service').read_text()
    if 'ExecStart=/usr/lib/pidock/broker/PiDock.Appliance.Broker\n' not in broker:raise ValueError('Broker must use its self-contained executable')
    if 'Environment=Broker__StateDirectory=/var/lib/pidock/broker/apps-state\n' not in broker or 'StateDirectoryMode=0700\n' not in broker or 'TimeoutStopSec=220\n' not in broker:
        raise ValueError('Factory broker requires private durable app state and coordinated-stop timeout')
    clock=(units/'pidock-time.service').read_text()
    if 'CapabilityBoundingSet=CAP_DAC_OVERRIDE\n' not in clock or 'ProtectClock=true\n' not in clock:
        raise ValueError('Time guard must query the chrony socket while retaining clock protection')
    for file in (target/'usr/lib/pidock/broker').glob('appsettings*.json'):
        if file.name not in ('appsettings.json','appsettings.Production.json'):continue
        values=json.loads(file.read_text())
        if values.get('Gpio',{}).get('Outputs'):raise ValueError('Image must not approve GPIO outputs')
    state=target/'var/lib/pidock'
    if state.exists() and any(p.is_file() for p in state.rglob('*')):raise ValueError('Public image contains per-device managed state')
    if any((target/'lib/modules').rglob('*.ko.xz')):
        kmod=resolve_target(target,'/usr/lib/libkmod.so.2')
        # Buildroot enables kmod's dlopen mode, so liblzma need not be DT_NEEDED.
        if b'liblzma.so.5' not in kmod.read_bytes() or not resolve_target(target,'/usr/lib/liblzma.so.5').is_file():
            raise ValueError('XZ-compressed kernel modules require kmod XZ support and liblzma')
    profile=json.loads((target/'etc/pidock/image-profile.json').read_text())
    expected=json.loads((Path(__file__).resolve().parents[1]/'buildroot/board/pidock/image-profile.json').read_text())
    if any(profile.get(k)!=expected[k] for k in ('version','sequence','development')):raise ValueError('Unexpected development image profile')
    if profile.get('setupSchema')!=2 or profile.get('usbNodeProtocol')!=1:raise ValueError('Expanded setup and USB node protocol must be declared by the signed image')
    if len(profile.get('sourceCommit',''))!=40:raise ValueError('Image source revision is missing')
    return dict(serviceExecutables=len(checked),gpioOutputs='disabled',privateState='absent',profile=profile)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('target',type=Path);args=parser.parse_args();print(json.dumps(verify(args.target)))
