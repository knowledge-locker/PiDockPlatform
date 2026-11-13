#!/usr/bin/env python3
"""Check a public target filesystem before creating signed boot images. Never reads private owner setup."""
import argparse,json,os,shlex,stat
from pathlib import Path
from image_components import resolve_target

def verify(target):
    units=target/'usr/lib/systemd/system'
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
    for file in (target/'usr/lib/pidock/broker').glob('appsettings*.json'):
        if file.name not in ('appsettings.json','appsettings.Production.json'):continue
        values=json.loads(file.read_text())
        if values.get('Gpio',{}).get('Outputs'):raise ValueError('Image must not approve GPIO outputs')
    state=target/'var/lib/pidock'
    if state.exists() and any(p.is_file() for p in state.rglob('*')):raise ValueError('Public image contains per-device managed state')
    profile=json.loads((target/'etc/pidock/image-profile.json').read_text())
    if profile.get('version')!='1.0.0-alpha.2' or profile.get('sequence')!=2 or not profile.get('development'):raise ValueError('Unexpected development image profile')
    if len(profile.get('sourceCommit',''))!=40:raise ValueError('Image source revision is missing')
    return dict(serviceExecutables=len(checked),gpioOutputs='disabled',privateState='absent',profile=profile)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('target',type=Path);args=parser.parse_args();print(json.dumps(verify(args.target)))
