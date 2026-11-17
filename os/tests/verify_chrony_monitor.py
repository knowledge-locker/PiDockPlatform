#!/usr/bin/env python3
"""Exercise the shipped chronyc runtime-write requirement in isolated containers.

Use trusted native Buildroot output; no network or host clock control is enabled.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time
import uuid

PREPARE = r'''
from pathlib import Path
import os,sys
sys.path.insert(0,'/repo/os/tools')
from image_components import copy_binary,copy_loader_layout
src=Path('/work')/sys.argv[1];root=Path('/fixture/root');root.mkdir()
for folder in ('bin','sbin','etc','dev','proc','run/chrony','var','tmp'):
    (root/folder).mkdir(parents=True,exist_ok=True)
(root/'var/run').symlink_to('../run')
for binary in ('/usr/bin/chronyc','/usr/sbin/chronyd'):
    copy_binary(src,root,binary)
copy_loader_layout(src,root)
(root/'etc/passwd').write_text('root:x:0:0:root:/root:/bin/sh\n')
(root/'etc/group').write_text('root:x:0:\n')
(root/'etc/chrony-test.conf').write_text('port 0\ncmdport 0\nbindcmdaddress /run/chrony/chronyd.sock\npidfile /run/chrony/chronyd.pid\n')
for name,minor in [('urandom',9),('null',3)]:
    os.mknod(root/'dev'/name,0o20666,os.makedev(1,minor))
os.chmod('/socket',0o750)
'''

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target-volume',default='pidock-pi5-build-2026081')
    parser.add_argument('--target-directory',default='output/target')
    parser.add_argument('--builder-image',default='pidock-image-builder:2026.08.1')
    args=parser.parse_args()
    source=Path(__file__).resolve().parents[1]
    prefix='pidock-time-test-'+uuid.uuid4().hex[:10]
    fixture=prefix+'-fixture';socket=prefix+'-socket';server=prefix+'-server'
    def run(*parts,check=True):
        return subprocess.run(['docker',*map(str,parts)],check=check,capture_output=True,text=True)
    def mount(name,dest,readonly=False):
        return 'type=volume,src='+name+',dst='+dest+(',readonly' if readonly else '')
    try:
        for volume in (fixture,socket):run('volume','create',volume)
        run('run','--rm','--user','0:0','--network','none','--cap-drop','ALL','--cap-add','MKNOD',
            '--mount',mount(args.target_volume,'/work',True),'--mount',mount(fixture,'/fixture'),
            '--mount',mount(socket,'/socket'),'--mount','type=bind,src='+str(source)+',dst=/repo/os,readonly',
            '--entrypoint','python3',args.builder_image,'-c',PREPARE,args.target_directory)
        run('run','-d','--name',server,'--user','0:0','--network','none','--cap-drop','ALL','--cap-add','SYS_CHROOT',
            '--mount',mount(fixture,'/fixture'),'--mount',mount(socket,'/fixture/root/run/chrony'),
            '--entrypoint','chroot',args.builder_image,'/fixture/root','/usr/sbin/chronyd','-d','-x','-u','root','-f','/etc/chrony-test.conf')
        def query(readonly):
            return run('run','--rm','--user','0:0','--network','none','--cap-drop','ALL',
                '--cap-add','SYS_CHROOT','--cap-add','DAC_OVERRIDE','--security-opt','no-new-privileges=true',
                '--mount',mount(fixture,'/fixture',True),'--mount',mount(socket,'/fixture/root/run/chrony',readonly),
                '--entrypoint','chroot',args.builder_image,'/fixture/root','/usr/bin/chronyc',
                '-h','/run/chrony/chronyd.sock','-n','tracking',check=False)
        deadline=time.monotonic()+10
        while True:
            writable=query(False)
            if writable.returncode==0:break
            if time.monotonic()>=deadline:raise RuntimeError('Test daemon did not become queryable: '+run('logs',server).stderr)
            time.sleep(.1)
        readonly=query(True)
        assert readonly.returncode!=0 and 'Read-only file system' in readonly.stderr,readonly.stderr
        assert 'Leap status' in writable.stdout and 'Not synchronised' in writable.stdout
        print(json.dumps({'shippedBinaries':True,'readonlySocketDirectoryRejected':True,
            'writableSocketDirectoryQueryable':True,'reportRemainsUnsynchronized':True,'networkDisabled':True}))
    finally:
        run('rm','-f',server,check=False)
        for volume in (socket,fixture):run('volume','rm',volume,check=False)

if __name__=='__main__':main()
