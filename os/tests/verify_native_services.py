#!/usr/bin/env python3
"""Exercise shipped ARM64 services in a disposable container, using public fixtures only.

Run with TARGET_ROOT ROOTFS_EXT4, as for verify_headless_import.py. The private
updater protocol fixture does not write slots or approve installation. It proves
that the real appliance process consumes the shipped unit's updater connection.
"""
import json,os,ssl,socket,subprocess,time,urllib.request,urllib.error
import datetime,http.server,shlex,socketserver,threading
from pathlib import Path
# Reuse the existing isolated read-only overlay and public Main/Node acceptance first.
exec(compile(Path('/repo/os/tests/verify_headless_import.py').read_text(),'/repo/os/tests/verify_headless_import.py','exec'),{'__name__':'__main__','__file__':'/repo/os/tests/verify_headless_import.py'})
root=Path('/tmp/image-root');tls=root/'var/lib/pidock/fixture-tls';tls.mkdir(mode=0o755)
def run(args):return subprocess.run(args,check=True,capture_output=True,text=True)
run(['openssl','req','-x509','-newkey','rsa:2048','-nodes','-days','1','-subj','/CN=127.0.0.1','-addext','subjectAltName=IP:127.0.0.1','-keyout',str(tls/'key'),' -out'.strip(),str(tls/'cert')])
run(['openssl','pkcs12','-export','-out',str(tls/'cert.pfx'),'-inkey',str(tls/'key'),'-in',str(tls/'cert'),'-passout','pass:'])
os.chown(tls/'cert.pfx',1654,1654);(tls/'cert.pfx').chmod(0o600)
brokerEnv={**os.environ,'ASPNETCORE_ENVIRONMENT':'Production','Broker__SocketPath':'/run/pidock/broker.sock','Broker__StateDirectory':'/var/lib/pidock/broker/apps-state','Broker__EnableAppResources':'true','Broker__EnableLifecycle':'true','Broker__RequireAppImageApproval':'true','Gpio__PolicyPath':'/var/lib/pidock/broker/gpio-policy.json','Logging__LogLevel__Default':'Warning'}
run(['chroot',str(root),'/bin/chown','-R','pidock:pidock','/var/lib/pidock/appliance'])
updaterQueried=threading.Event()
class UpdaterStatus(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.path!='/status':self.send_error(404);return
  profile=json.loads((root/'etc/pidock/image-profile.json').read_text())
  body=json.dumps(dict(observedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),available=True,cloudEnabled=False,runtime='linux-arm64',installedVersion=profile['version'],installedDigest=None,staged=None,operation=None,status='Native protocol fixture',updateKind='image')).encode()
  self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.send_header('Connection','close');self.end_headers();self.wfile.write(body);updaterQueried.set()
 def log_message(self,*args):pass
class UpdaterServer(socketserver.ThreadingMixIn,socketserver.UnixStreamServer):
 daemon_threads=True
updaterDirectory=root/'run/pidock-updater';updaterDirectory.mkdir(mode=0o750,exist_ok=True);os.chown(updaterDirectory,0,1654)
updaterSocket=updaterDirectory/'update.sock'
updaterServer=UpdaterServer(str(updaterSocket),UpdaterStatus);os.chown(updaterSocket,0,1654);updaterSocket.chmod(0o660)
threading.Thread(target=updaterServer.serve_forever,daemon=True).start()
processes=[]
try:
 broker=subprocess.Popen(['chroot',str(root),'/usr/bin/setpriv','--regid=1654','--clear-groups','/usr/lib/pidock/broker/PiDock.Appliance.Broker'],env=brokerEnv,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True);processes.append(broker)
 for _ in range(80):
  if (root/'run/pidock/broker.sock').exists():break
  if broker.poll() is not None:raise RuntimeError(broker.stdout.read()[-2000:])
  time.sleep(.25)
 with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as client:
  client.settimeout(5);client.connect(str(root/'run/pidock/broker.sock'));client.sendall(b'GET /health/update HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n');assert b'200 OK' in client.recv(8192)
 # Read the actual installed unit. Do not supply Updates__SocketPath here: doing
 # so would conceal precisely the disconnected image configuration being tested.
 unitEnvironment={}
 for line in (root/'usr/lib/systemd/system/pidock-appliance.service').read_text().splitlines():
  if line.startswith('Environment='):
   for entry in shlex.split(line.split('=',1)[1]):
    name,value=entry.split('=',1);unitEnvironment[name]=value
 env={**{k:v for k,v in os.environ.items() if not k.startswith(('Updates__','Broker__','PiDock__'))},**unitEnvironment,'Kestrel__Endpoints__Local__Url':'https://127.0.0.1:7445','Kestrel__Endpoints__Lan__Url':'https://127.0.0.1:7446','Kestrel__Certificates__Default__Path':'/var/lib/pidock/fixture-tls/cert.pfx','Mqtt__Enabled':'false','Logging__LogLevel__Default':'Warning'}
 web=subprocess.Popen(['chroot',str(root),'/usr/bin/setpriv','--reuid=1654','--regid=1654','--clear-groups','/usr/lib/pidock/appliance/PiDock.Appliance.Web'],env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True);processes.append(web)
 context=ssl.create_default_context(cafile=str(tls/'cert'))
 for _ in range(100):
  if web.poll() is not None:raise RuntimeError(web.stdout.read()[-4000:])
  try:
   with urllib.request.urlopen('https://127.0.0.1:7445/health/update',context=context,timeout=2) as response:
    health=json.load(response)
    if health['ready']:break
  except (OSError,urllib.error.URLError):pass
  time.sleep(.25)
 else:raise RuntimeError('Appliance failed fresh-monitoring health check')
 assert updaterQueried.wait(10),'Shipped appliance unit did not connect to its private updater socket'
 print(json.dumps(dict(nativeApplianceHttps='passed',nativeBrokerUnixHealth='passed',shippedUnitUpdaterConnection='passed',nonRootAppliance=True,freshMonitoring=True,platform='linux/arm64')),flush=True)
finally:
 for process in reversed(processes):
  process.terminate()
  try:process.wait(timeout=15)
  except subprocess.TimeoutExpired:process.kill();process.wait()
 updaterServer.shutdown();updaterServer.server_close()
