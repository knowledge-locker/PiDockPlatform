#!/usr/bin/env python3
"""Native first-boot acceptance using public fixtures and an ephemeral overlay; run only in the isolated Linux builder."""
import json,os,subprocess,time,hashlib
from pathlib import Path
import sys
if len(sys.argv)!=3 or not Path('/.dockerenv').exists():raise SystemExit('Run inside the isolated builder: verify_headless_import.py TARGET_ROOT ROOTFS_EXT4')
target=Path(sys.argv[1]).resolve();filesystem=Path(sys.argv[2]).resolve()
assert target!=Path('/') and (target/'etc/pidock/image-profile.json').is_file()
root=Path('/tmp/image-root')
def run(args,**kwargs):
 result=subprocess.run(args,capture_output=True,text=True,**kwargs)
 if result.returncode:
  print((result.stdout+result.stderr)[-5000:],flush=True);result.check_returncode()
 return result
for name in ('image-root','image-upper','image-work'): (Path('/tmp')/name).mkdir()
run(['mount','-t','overlay','overlay','-o','lowerdir='+str(target)+',upperdir=/tmp/image-upper,workdir=/tmp/image-work',str(root)])
# Buildroot's staging tree is builder-owned; the shipped filesystem is root-owned.
os.chown(root,0,0)
run(['mount','-o','remount,ro',str(root)])
for name in ('passwd','group'):
 out=Path('/tmp')/('pidock-image-'+name)
 run(['debugfs','-R','dump /etc/'+name+' '+str(out),str(filesystem)])
 run(['mount','--bind',str(out),str(root/'etc'/name)])
fstab=Path('/tmp/image-fstab')
fstab.write_text('tmpfs /boot/control tmpfs defaults 0 0\ntmpfs /var tmpfs defaults 0 0\n')
run(['mount','--bind',str(fstab),str(root/'etc/fstab')])
for name in ('proc','dev','sys'):run(['mount','--bind','/'+name,str(root/name)])
for name in ('var','run','tmp','boot/control'):
 run(['mount','-t','tmpfs','-o','mode='+('1777' if name=='tmp' else '0755')+',nosuid,nodev'+(',noexec' if name in ('var','tmp','boot/control') else ''),'tmpfs',str(root/name)])
# Match the directories and permissions prepared before the real import service.
run(['chroot',str(root),'/usr/bin/systemd-tmpfiles','--create','/usr/lib/tmpfiles.d/pidock.conf'])
setup={'schema':2,'mode':'Main','username':'fixture-owner','passwordHash':'','secret':'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ','lastStep':1001,'wifi':{'ssid':'5069446f636b46697874757265','psk':'0'*64,'country':'US'},'network':{'ethernet':{'enabled':False,'method':'dhcp'},'wifi':{'enabled':True,'method':'dhcp'}},'cloud':None}
# Public fixture only; the production owner's setup is never mounted in this container.
import base64,struct
setup['passwordHash']=base64.b64encode(b'\x01'+struct.pack('>III',2,210000,16)+b'\0'*16+b'\0'*32).decode()
boot=root/'boot/control/pidock-provisioning.json';boot.write_text(json.dumps(setup))
env={**os.environ,'PiDock__StateDirectory':'/var/lib/pidock/appliance','ASPNETCORE_ENVIRONMENT':'Production'}
result=run(['chroot',str(root),'/usr/lib/pidock/appliance/PiDock.Appliance.Web','--import-media'],env=env,timeout=60)
assert (root/'var/lib/pidock/appliance/media-provisioned').exists()
assert not boot.exists()
assert (root/'var/lib/pidock/network/wpa_supplicant.conf').exists()
assert json.loads((root/'var/lib/pidock/device-mode.json').read_text())['mode']=='Main'
run(['chroot',str(root),'/usr/lib/pidock/appliance/PiDock.Appliance.Web','--apply-media-network'],env=env,timeout=60)
wireless=(root/'run/systemd/network/10-pidock-wireless.network').read_text();assert 'DHCP=yes' in wireless
run(['chroot',str(root),'/usr/bin/setpriv','--regid=1654','--clear-groups','/usr/lib/pidock/updater/PiDock.Updater','host-first-boot-log-once'],env=env,timeout=30)
installer=(root/'var/lib/pidock/diagnostics/install.log').read_text()
card_report=(root/'boot/control/pidock-install.log').read_text()
assert 'setup=applied' in installer and 'mode=Main' in installer
for secret in (setup['secret'],setup['wifi']['ssid'],setup['wifi']['psk']):
 assert secret not in installer and secret not in card_report
run(['chroot',str(root),'/usr/bin/setpriv','--reuid=1654','--regid=1654','--clear-groups','/bin/cat','/var/lib/pidock/diagnostics/install.log'])
print('Sanitized installer report generated, exported to the boot partition and readable by the appliance user.',flush=True)
account=root/'var/lib/pidock/appliance/accounts.protected'
before=hashlib.sha256(account.read_bytes()).digest()
setup['username']='replacement-owner'
run(['mount','-o','remount,rw',str(root/'boot/control')])
boot.write_text(json.dumps(setup))
run(['chroot',str(root),'/usr/lib/pidock/appliance/PiDock.Appliance.Web','--import-media'],env=env,timeout=60)
assert hashlib.sha256(account.read_bytes()).digest()==before and not boot.exists()
print('Native Main import/replay passed: owner preserved, setup consumed, Wi-Fi/interface configuration generated.',flush=True)
run(['umount',str(root/'var')])
run(['mount','-t','tmpfs','-o','mode=0755,nosuid,nodev,noexec','tmpfs',str(root/'var')])
run(['chroot',str(root),'/usr/bin/systemd-tmpfiles','--create','/usr/lib/tmpfiles.d/pidock.conf'])
node={'schema':2,'mode':'Node','username':'','passwordHash':'','secret':'','lastStep':0,'wifi':None,'network':{'ethernet':{'enabled':False,'method':'dhcp'},'wifi':{'enabled':False,'method':'dhcp'}}}
run(['mount','-o','remount,rw',str(root/'boot/control')])
boot.write_text(json.dumps(node))
run(['chroot',str(root),'/usr/lib/pidock/appliance/PiDock.Appliance.Web','--import-media'],env=env,timeout=60)
assert (root/'var/lib/pidock/node-mode').read_text().strip()=='unpaired'
assert not account.exists() and not boot.exists()
run(['chroot',str(root),'/usr/lib/pidock/appliance/PiDock.Appliance.Web','--apply-media-network'],env=env,timeout=60)
assert (root/'run/systemd/network/05-pidock-usb.network').exists()
print('Native Node import passed: unpaired USB mode, no human owner or external network.',flush=True)
run(['chroot',str(root),'/usr/lib/pidock/updater/PiDock.Updater','host-runtime-check'],timeout=30)
print('Native runtime cryptography/globalization passed.',flush=True)
