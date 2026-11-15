// Device credentials stay in this DOM, never in Blazor state or requests.
const enc=new TextEncoder();
const preparations=new WeakMap();
const hex=b=>[...new Uint8Array(b)].map(x=>x.toString(16).padStart(2,'0')).join('');
export function clear(host){preparations.get(host)?.abort();preparations.delete(host);host.shadowRoot?.replaceChildren();}
export async function passwordHash(password,salt=crypto.getRandomValues(new Uint8Array(16))){
 const key=await crypto.subtle.importKey('raw',enc.encode(password),'PBKDF2',false,['deriveBits']);
 const hash=new Uint8Array(await crypto.subtle.deriveBits({name:'PBKDF2',salt,iterations:210000,hash:'SHA-512'},key,256));
 const out=new Uint8Array(61),v=new DataView(out.buffer);out[0]=1;v.setUint32(1,2);v.setUint32(5,210000);v.setUint32(9,16);out.set(salt,13);out.set(hash,29);return btoa(String.fromCharCode(...out));
}
export function passwordRequirement(password) {
 return password.length < 14 || password.length > 256 ? 'Use 14–256 characters. Spaces and passphrases are allowed.' : '';
}
export async function removeImageTemporaryFiles(attempt){
 if(!attempt)return;
 if(!/^pidock-image-[0-9]+-[0-9a-f-]+$/.test(attempt))throw Error('Invalid temporary image directory.');
 const directory=await navigator.storage.getDirectory();
 try{await directory.removeEntry(attempt,{recursive:true});}catch(error){if(error.name!=='NotFoundError')throw error;}
}
async function clearExpiredImages(){
 if(!navigator.storage?.getDirectory)return;
 const directory=await navigator.storage.getDirectory();
 for await(const name of directory.keys()){
  const match=/^pidock-image-([0-9]+)-[0-9a-f-]+$/.exec(name);
  if(match&&Date.now()-Number(match[1])>86400000)await removeImageTemporaryFiles(name);
 }
}
if(typeof window!=='undefined')clearExpiredImages().catch(()=>{});
export function prepareFactoryImage(url,ticket,payload,manifest,onUpdate,signal,attempt){
 return new Promise((resolve,reject)=>{
  if(signal.aborted){reject(new DOMException('Image preparation cancelled.','AbortError'));return;}
  const worker=new Worker(new URL('./provisioning-image-worker.js',import.meta.url),{type:'module'});
  const cleanup=()=>{signal.removeEventListener('abort',aborted);worker.terminate();};
  const fail=error=>{cleanup();removeImageTemporaryFiles(attempt).catch(()=>{});reject(error);};
  const aborted=()=>fail(new DOMException('Image preparation cancelled.','AbortError'));
  signal.addEventListener('abort',aborted,{once:true});
  worker.onerror=()=>fail(Error('Image processing stopped. Your setup remains here; retry the download.'));
  worker.onmessage=({data})=>{
   if(data.type==='complete'){cleanup();resolve(data.file);}
   else if(data.type==='error')fail(Error(data.message));
   else onUpdate(data);
  };
  try{worker.postMessage({url,ticket,setup:payload.buffer,manifest,attempt},[payload.buffer]);}
  catch(error){fail(error);}
 });
}
function imageProgress(root){
 const panel=root.querySelector('#imageProgress'),title=root.querySelector('#imageProgressTitle'),bar=root.querySelector('#imageProgressBar'),detail=root.querySelector('#imageProgressDetail'),timing=root.querySelector('#imageProgressTiming'),button=root.querySelector('button[type=submit]');
 let timer,phase='',start=0,lastData=0,loaded=0,total=0,lastRender=0;
 const size=n=>n>=1073741824?(n/1073741824).toFixed(2)+' GiB':(n/1048576).toFixed(1)+' MiB';
 const duration=n=>n<60?Math.floor(n)+'s':Math.floor(n/60)+'m '+Math.floor(n%60)+'s';
 const tick=()=>{
  const seconds=(performance.now()-start)/1000;
  let text='Elapsed '+duration(seconds);
  if(phase==='download'&&loaded>0&&seconds>1){const rate=loaded/seconds;text+=' · '+size(rate)+'/s';if(loaded<total)text+=' · about '+duration((total-loaded)/rate)+' remaining';}
  if(phase==='download'&&performance.now()-lastData>15000)text+=' · Waiting for more data; no new bytes for '+duration((performance.now()-lastData)/1000)+'.';
  timing.textContent=text;
 };
 return {
  stage(key,heading,message,label){
   clearInterval(timer);phase=key;start=performance.now();lastData=start;loaded=0;total=0;lastRender=0;
   panel.hidden=false;panel.dataset.state=key;title.textContent=heading;detail.textContent=message;bar.hidden=false;bar.removeAttribute('value');button.textContent=label;tick();timer=setInterval(tick,1000);panel.scrollIntoView({behavior:'smooth',block:'center'});
  },
  advance(count,length){
   if(count!==loaded)lastData=performance.now();loaded=count;total=length;
   if(count!==length&&performance.now()-lastRender<250)return;lastRender=performance.now();
   bar.value=Math.floor(count*100/length);detail.textContent=bar.value+'% · '+size(count)+' of '+size(length)+(phase==='download'?' received':' processed');tick();
  },
  stop(heading,message,success=false){clearInterval(timer);title.textContent=heading;detail.textContent=message;timing.textContent='';bar.hidden=!success;if(success)bar.value=100;button.textContent=success?'Image ready':'Create and download image';},
  dispose(){clearInterval(timer);}
 };
}
export function base32(bytes){let text='',bits=0,buf=0;for(const b of bytes){buf=(buf<<8)|b;bits+=8;while(bits>=5){bits-=5;text+='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'[(buf>>>bits)&31];}}return text;}
export async function totp(bytes,step){const b=new Uint8Array(8);new DataView(b.buffer).setBigUint64(0,BigInt(step));const key=await crypto.subtle.importKey('raw',bytes,{name:'HMAC',hash:'SHA-1'},false,['sign']);const h=new Uint8Array(await crypto.subtle.sign('HMAC',key,b));return ((new DataView(h.buffer).getUint32(h[19]&15)&0x7fffffff)%1000000).toString().padStart(6,'0');}
export function patchStream(extents,payload,size,onProgress){
 let end=0,total=0;for(const p of extents){if(!Number.isSafeInteger(p.offset)||!Number.isSafeInteger(p.length)||p.offset<end||p.length<=0||p.offset+p.length>size)throw Error('Invalid setup area');end=p.offset+p.length;total+=p.length;}
 if(total!==1048576||payload.length>total)throw Error('Invalid setup area');
 const data=new Uint8Array(total).fill(32);data.set(payload);let position=0;
 return new TransformStream({transform(chunk,c){const b=new Uint8Array(chunk).slice();let source=0;for(const p of extents){const lo=Math.max(position,p.offset),hi=Math.min(position+b.length,p.offset+p.length);if(lo<hi)b.set(data.subarray(source+lo-p.offset,source+hi-p.offset),lo-position);source+=p.length;}position+=b.length;if(position>size)throw Error('Image size mismatch');onProgress?.(position,size);c.enqueue(b);},flush(){if(position!==size)throw Error('Image size mismatch');data.fill(0);}});
}
export function interfaceSetup(enabled,method,address='',gateway='',dns=''){
 if(!enabled)return {enabled:false,method:'dhcp'};
 if(method==='dhcp')return {enabled:true,method};
 if(method!=='static')throw Error('Choose DHCP or a static IPv4 address.');
 const ipv4=value=>{if(!/^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}$/.test(value))return null;const parts=value.split('.').map(Number);return parts.some(x=>x>255)||parts[0]===0||parts[0]===127||parts[0]>=224?null:parts.reduce((n,x)=>(n*256+x)>>>0,0);};
 const [ip,prefix,...extra]=address.trim().split('/'),bits=Number(prefix),numeric=ipv4(ip||'');
 if(extra.length||!/^([1-9]|[12][0-9]|30)$/.test(prefix||'')||numeric===null)throw Error('Use a static IPv4 address with a prefix, for example 192.168.1.50/24.');
 const mask=(0xffffffff<<(32-bits))>>>0,host=(numeric&~mask)>>>0,max=(~mask)>>>0;
 if(host===0||host===max)throw Error('The static address cannot be a network or broadcast address.');
 const result={enabled:true,method,address:ip+'/'+bits};
 gateway=gateway.trim();if(gateway){const g=ipv4(gateway),h=(g&~mask)>>>0;if(g===null||(g&mask)!==(numeric&mask)||g===numeric||h===0||h===max)throw Error('The gateway must be another host on this interface’s subnet.');result.gateway=gateway;}
 const servers=dns.trim()?dns.trim().split(/[\s,]+/):[];
 if(servers.length>4||servers.some(x=>ipv4(x)===null))throw Error('Enter up to four IPv4 DNS server addresses.');
 if(servers.length)result.dns=servers;return result;
}
export function cloudSetup(enabled,url,register,code,remoteControl,remoteUi){
 if(!enabled)return null;
 let origin;try{origin=new URL(url);}catch{throw Error('Enter a valid HTTPS cloud address.');}
 if(origin.protocol!=='https:'||origin.username||origin.password||origin.pathname!=='/'||origin.search||origin.hash||url.length>512)throw Error('Use an HTTPS cloud origin without a path, username or query.');
 const result={url:origin.origin+'/'};
 if(register){const value=code.trim().replaceAll('-','').toUpperCase();if(!/^[0-9A-F]{32}$/.test(value))throw Error('Enter a full single-use cloud enrollment code.');result.enrollmentCode=value;result.remoteControl=!!remoteControl;result.remoteUi=!!remoteUi;}
 else if(remoteControl||remoteUi)throw Error('Remote permissions require cloud registration.');
 return result;
}
export function setupCodeControls(ui,bytes,signal,now=()=>Math.floor(Date.now()/30000)) {
 const {firstInput,secondInput,firstButton,secondButton,firstStatus,secondStatus,help}=ui;
 let first=-1,second=-1,observedFirst=firstInput.value,observedSecond=secondInput.value,revision=0;
 const instruction='Verify the first code, then wait for the next 30-second code before entering it here.';
 const reset=()=>{
  first=-1;second=-1;observedFirst=firstInput.value;observedSecond='';
  secondInput.value='';secondInput.disabled=true;secondInput.required=true;
  firstButton.disabled=!/^[0-9]{6}$/.test(firstInput.value);secondButton.disabled=true;
  firstStatus.textContent='';secondStatus.textContent='';help.textContent=instruction;
 };
 const firstEdited=()=>{if(firstInput.value===observedFirst)return;revision++;reset();};
 const secondEdited=()=>{
  if(secondInput.value===observedSecond)return;
  revision++;observedSecond=secondInput.value;second=-1;secondInput.required=true;
  secondButton.disabled=first<0||!/^[0-9]{6}$/.test(secondInput.value);secondStatus.textContent='';
 };
 for(const event of ['input','change']){firstInput.addEventListener(event,firstEdited);secondInput.addEventListener(event,secondEdited);}
 firstButton.onclick=async()=>{
  firstEdited();const candidate=firstInput.value,step=now(),attempt=revision;
  if(!/^[0-9]{6}$/.test(candidate)){reset();return;}
  const expected=await totp(bytes,step);
  if(signal.aborted||revision!==attempt||firstInput.value!==candidate)return;
  if(candidate!==expected){reset();firstStatus.textContent='Enter the current code from the new authenticator entry.';return;}
  first=step;second=-1;
  // Update observed values before clearing/focusing: Safari can emit change on blur.
  observedFirst='';observedSecond='';firstInput.value='';secondInput.value='';
  firstButton.disabled=true;secondInput.required=true;secondInput.disabled=false;secondButton.disabled=true;
  firstStatus.textContent='First code verified. Wait for the next code.';secondStatus.textContent='';help.textContent=instruction;
  secondInput.focus();
 };
 secondButton.onclick=async()=>{
  firstEdited();secondEdited();const candidate=secondInput.value,step=now(),originalFirst=first,attempt=revision;
  if(originalFirst<0||!/^[0-9]{6}$/.test(candidate))return;
  if(step<=originalFirst){secondStatus.textContent='Wait until your authenticator shows its next code, then verify it.';return;}
  if(step!==originalFirst+1){reset();secondStatus.textContent='The verification window expired. Verify a new first code, then its next code.';firstInput.focus();return;}
  const expected=await totp(bytes,step);
  if(signal.aborted||revision!==attempt||first!==originalFirst||secondInput.value!==candidate)return;
  if(candidate!==expected){secondStatus.textContent='That code did not match. Enter the next code from the new PiDockOS authenticator entry.';return;}
  second=step;observedSecond='';secondInput.value='';secondInput.required=false;secondInput.disabled=true;secondButton.disabled=true;
  firstStatus.textContent='First code verified.';secondStatus.textContent='Second code verified. Authenticator setup is complete.';
  help.textContent='Both codes are verified. You can finish the remaining settings before downloading.';
 };
 reset();
 return {get step(){firstEdited();secondEdited();return first>=0&&second===first+1?second:null;}};
}
function interfaceForm(name,title,enabled,extra=''){return `<fieldset class="interface"><legend>${title}</legend><label class="check"><input name="${name}Enabled" type="checkbox" ${enabled?'checked':''}>Enable ${title}</label><div id="${name}Settings"><label>IPv4 addressing<select name="${name}Method"><option value="dhcp">Automatic (DHCP)</option><option value="static">Static</option></select></label><div id="${name}Static" hidden><label>IPv4 address / prefix<input name="${name}Address" placeholder="192.168.1.50/24" autocomplete="off"></label><label>Gateway (optional)<input name="${name}Gateway" placeholder="192.168.1.1" autocomplete="off"></label><label>DNS servers (optional, separated by commas)<input name="${name}Dns" placeholder="192.168.1.1, 1.1.1.1" autocomplete="off"></label></div>${extra}</div></fieldset>`;}
export function configure(host,image,bridge){
 clear(host);clearExpiredImages().catch(()=>{});const controller=new AbortController();preparations.set(host,controller);const root=host.shadowRoot||host.attachShadow({mode:'open'}),expanded=image.manifest.setupSchema===2;
 const wifiFields='<div id="wifi"><label>Wi-Fi network name<input name="ssid" maxlength="32" autocomplete="off"></label><label>Wi-Fi password<input name="wifiPassword" type="password" autocomplete="off"></label><label>Country code<input name="country" value="US" maxlength="2" pattern="[A-Z]{2}"></label></div>';
 root.innerHTML=`<style>:host{font:inherit}label{display:block;margin:1em 0}input,select{display:block;box-sizing:border-box;width:100%;padding:.7em;margin-top:.4em;font:inherit;border:1px solid #bcb5b8;border-radius:6px}button{font:inherit;cursor:pointer;padding:.8em;border-radius:7px;border:0;background:#c4144e;color:white}fieldset{border:0;padding:0}fieldset.interface{border:1px solid #ded8dc;padding:1em;margin:1em 0}input[type=checkbox]{width:auto;display:inline;margin-right:.5em}.check{display:flex;align-items:center;gap:.4em}[hidden]{display:none!important}output{display:block;margin:1em 0}.preparation-progress{padding:1.2em;margin:1em 0;border:2px solid #c4144e;border-radius:10px;background:#fff2f6}.preparation-progress h3{margin:0 0 .6em}.preparation-progress progress{display:block;width:100%;height:1.6em;accent-color:#c4144e}.preparation-progress p{margin:.7em 0}code{user-select:all;overflow-wrap:anywhere}.credential-pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.2em}.credential-pair>div{min-width:0}.password-control{display:flex;align-items:stretch;gap:.4em}.password-control input{min-width:0;margin:0}.password-control button{flex-shrink:0;background:#eee6e9;color:#522333;padding:.5em .8em}.help{font-size:.92em;color:#5d5558}.credential-pair label{margin-bottom:.5em}button:disabled{opacity:.5;cursor:default}input:disabled{background:#f3f0f1}@media(max-width:640px){.credential-pair{grid-template-columns:1fr;gap:0}}</style><form><fieldset id="all">${expanded?`<label>Mode<select name="mode" aria-describedby="modeDescription"><option value="Main">Main — Primary or standalone Pi</option><option value="Node">Node — USB expansion for a Main</option></select></label><p id="modeDescription"></p><p id="nodeNote" hidden>Prepare an unpaired USB expansion node. No human owner, external network, cloud registration or remote permissions are configured. Connect it to a Main device over a data-capable USB link, then adopt and configure it from Main → USB nodes. The Main owner must confirm adoption.</p>`:'<p>This image supports basic Main setup. Choose an expanded-setup image for mode, static addressing and cloud options.</p>'}<fieldset id="main"><legend>Local appliance owner</legend>
 <label>Username<input name="username" pattern="[A-Za-z0-9-]{1,64}" maxlength="64" autocomplete="off" required></label>
 <p id="passwordRequirements" class="help">Use 14–256 characters. Spaces and passphrases are allowed. Confirmation must match exactly.</p>
 <div class="credential-pair"><div><label for="appliancePassword">Appliance password</label><div class="password-control"><input id="appliancePassword" name="password" type="password" minlength="14" maxlength="256" autocomplete="new-password" aria-describedby="passwordRequirements passwordFeedback" required><button type="button" data-reveal="password" aria-controls="appliancePassword" aria-label="Show appliance password" aria-pressed="false">Show</button></div><p id="passwordFeedback" class="help" role="status"></p></div>
 <div><label for="confirmPassword">Confirm password</label><div class="password-control"><input id="confirmPassword" name="confirm" type="password" maxlength="256" autocomplete="new-password" aria-describedby="confirmationHelp confirmationFeedback" disabled required><button type="button" data-reveal="confirm" aria-controls="confirmPassword" aria-label="Show password confirmation" aria-pressed="false" disabled>Show</button></div><p id="confirmationHelp" class="help">Enter a valid appliance password first.</p><p id="confirmationFeedback" class="help" role="status"></p></div></div>
 <p class="help">Your appliance password and confirmation are processed only in this browser. They are not sent to or stored by PiDockCloud. The appliance password is represented in the downloaded image by a salted PBKDF2-SHA512 hash (210,000 iterations with a random 128-bit salt). Its plaintext is never written to the image.</p>
 <p class="help">The image also contains your authenticator secret and, if configured, a Wi-Fi network key derived in this browser instead of the plaintext Wi-Fi password. The setup area in the download is not encrypted. Preparation uses temporary files in this browser’s private disk storage, including the configured image. These files are not uploaded to PiDockCloud. Use “Clear temporary image” after saving; otherwise this tab clears them after 30 minutes. Interrupted files older than 24 hours are cleared on the next visit. Keep the configured image private and delete it after flashing.</p>
 <p>Add a new PiDockOS entry to your authenticator with this setup key:</p><code id="secret"></code>
 <div class="credential-pair"><div><label>Current authenticator code<input name="code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off"></label><button id="verify" type="button" disabled>Verify first code</button><output id="verification" role="status"></output></div>
 <div><label>Next authenticator code<input name="next" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off" aria-describedby="nextCodeHelp" disabled required></label><button id="verifyNext" type="button" disabled>Verify second code</button><output id="nextVerification" role="status"></output><p id="nextCodeHelp" class="help">Verify the first code, then wait for the next 30-second code before entering it here.</p></div></div>
 ${expanded?interfaceForm('ethernet','Ethernet',true)+interfaceForm('wireless','Wi-Fi',true,wifiFields):''}
 <div ${expanded?'hidden':''}><label>Network<select name="network"><option value="wifi">Wi-Fi</option><option value="ethernet">Ethernet</option></select></label></div>${expanded?'':'<fieldset class="interface" id="legacyWifi"><legend>Wi-Fi</legend>'+wifiFields+'</fieldset>'}
 ${expanded?`<fieldset class="interface"><legend>PiDockCloud (optional)</legend><label class="check"><input name="cloudEnabled" type="checkbox" aria-describedby="cloudDefault">Configure PiDockCloud</label><p id="cloudDefault"></p><p id="cloudSuggestion"></p><fieldset id="cloudSettings" disabled hidden><label>Cloud URL<input name="cloudUrl" type="url" autocomplete="off" maxlength="512"></label><label class="check"><input name="register" type="checkbox">Register on first boot</label><fieldset id="registrationSettings" disabled hidden><label>Single-use enrollment code<input name="enrollmentCode" autocomplete="off" maxlength="64" spellcheck="false"></label><p>Generate an invitation in the chosen cloud shortly before creating the image. It must still be valid when the Pi first connects. An expired invitation can be replaced locally after boot.</p><label class="check"><input name="remoteControl" type="checkbox">Allow remote container control</label><label class="check"><input name="remoteUi" type="checkbox">Allow remote PiDockOS interface access</label><p>These are separate local permissions, off by default. They take effect only after this registration succeeds. Cloud actions still require fresh MFA; remote UI sessions also require PiDockOS sign-in. Cloud-managed OS updates remain off.</p></fieldset></fieldset></fieldset>`:'<p>Cloud enrollment and remote permissions are approved separately after first boot.</p>'}</fieldset><button type="submit">Create and download image</button></fieldset><section id="imageProgress" class="preparation-progress" hidden aria-labelledby="imageProgressTitle"><h3 id="imageProgressTitle"></h3><progress id="imageProgressBar" max="100" aria-label="Image preparation progress"></progress><p id="imageProgressDetail" role="status" aria-live="polite"></p><p id="imageProgressTiming" class="help" aria-live="off"></p><p class="help">Keep this tab open. Setup controls are locked while the prebuilt image downloads and is prepared. Safari’s Downloads list stays empty until the configured file is ready. There is no background compilation. Your private setup is added locally in this browser.</p></section><output id="status" role="status"></output><div id="download"></div></form>`;
 const progress=imageProgress(root),form=root.querySelector('form'),f=form.elements,status=root.querySelector('#status'),bytes=crypto.getRandomValues(new Uint8Array(20)),secret=base32(bytes);let active=false,objectUrl,payload,attempt,expiry;
 const clearTemporary=async()=>{clearTimeout(expiry);if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=null;await removeImageTemporaryFiles(attempt);attempt=null;root.querySelector('#download').replaceChildren();};
 controller.signal.addEventListener('abort',()=>{progress.dispose();bytes.fill(0);payload?.fill(0);clearTemporary().catch(()=>{});f.password.value='';f.confirm.value='';f.wifiPassword.value='';if(expanded)f.enrollmentCode.value='';},{once:true});
 root.querySelector('#secret').textContent=secret;
 const confirmationHelp=root.querySelector('#confirmationHelp');
 const passwordState=()=>{
  const error=passwordRequirement(f.password.value),ready=!error;
  f.password.setCustomValidity(error);f.confirm.disabled=!ready;
  root.querySelector('[data-reveal="confirm"]').disabled=!ready;
  if(!ready){f.confirm.value='';f.confirm.type='password';const toggle=root.querySelector('[data-reveal="confirm"]');toggle.textContent='Show';toggle.setAttribute('aria-pressed','false');toggle.setAttribute('aria-label','Show password confirmation');}
  confirmationHelp.textContent=ready?'Re-enter the same password.':'Enter a valid appliance password first.';
  root.querySelector('#passwordFeedback').textContent=f.password.value?(ready?'Password length requirement met.':error):'';
  const mismatch=ready&&f.confirm.value!==f.password.value;
  f.confirm.setCustomValidity(mismatch?'Passwords must match exactly.':'');
  root.querySelector('#confirmationFeedback').textContent=f.confirm.value?(mismatch?'Passwords do not match.':'Passwords match.') : '';
 };
 f.password.addEventListener('input',passwordState);f.password.addEventListener('change',passwordState);f.confirm.addEventListener('input',passwordState);f.confirm.addEventListener('change',passwordState);
 for(const toggle of root.querySelectorAll('[data-reveal]'))toggle.onclick=()=>{const input=f[toggle.dataset.reveal],show=input.type==='password';input.type=show?'text':'password';toggle.textContent=show?'Hide':'Show';toggle.setAttribute('aria-pressed',String(show));toggle.setAttribute('aria-label',(show?'Hide ':'Show ')+(toggle.dataset.reveal==='password'?'appliance password':'password confirmation'));};
 const codeVerification=setupCodeControls({firstInput:f.code,secondInput:f.next,firstButton:root.querySelector('#verify'),secondButton:root.querySelector('#verifyNext'),firstStatus:root.querySelector('#verification'),secondStatus:root.querySelector('#nextVerification'),help:root.querySelector('#nextCodeHelp')},bytes,controller.signal);
 passwordState();

 const sync=()=>{
  if(!expanded){root.querySelector('#legacyWifi').hidden=f.network.value!=='wifi';return;}
  const node=f.mode.value==='Node';root.querySelector('#modeDescription').textContent=node?'Adds Pi hardware to extend a Main. It is discovered and configured through the Main over USB.':'The primary Pi for a standalone device or a deployment with additional Pi nodes.';root.querySelector('#main').disabled=node;root.querySelector('#main').hidden=node;root.querySelector('#nodeNote').hidden=!node;
  for(const name of ['ethernet','wireless']){root.querySelector('#'+name+'Settings').hidden=!f[name+'Enabled'].checked;root.querySelector('#'+name+'Static').hidden=f[name+'Method'].value!=='static';}
  root.querySelector('#wifi').hidden=!f.wirelessEnabled.checked;
  root.querySelector('#cloudDefault').textContent=f.cloudEnabled.checked?'Cloud configuration is enabled. Registration and remote permissions require separate choices below.':'Default: no cloud connection or registration. Remote container control, remote interface access and cloud-managed OS updates stay off. You can connect to PiDockCloud locally after first boot.';
  root.querySelector('#cloudSuggestion').textContent='Suggested cloud URL when enabled: '+location.origin;
  root.querySelector('#cloudSettings').disabled=!f.cloudEnabled.checked;root.querySelector('#cloudSettings').hidden=!f.cloudEnabled.checked;
  root.querySelector('#registrationSettings').disabled=!f.register.checked;root.querySelector('#registrationSettings').hidden=!f.register.checked;
  if(!f.register.checked){f.remoteControl.checked=false;f.remoteUi.checked=false;}
 };
 if(expanded){f.cloudUrl.value=location.origin;for(const name of ['mode','ethernetEnabled','wirelessEnabled','ethernetMethod','wirelessMethod','cloudEnabled','register'])f[name].onchange=sync;}else f.network.onchange=sync;sync();
 form.onsubmit=async e=>{e.preventDefault();if(active)return;active=true;root.querySelector('#all').disabled=true;
 try{
 progress.stage('prepare','Preparing local setup','Checking settings and deriving credentials in your browser.','Preparing setup…');
 if(!isSecureContext||!globalThis.CompressionStream||!globalThis.DecompressionStream||!navigator.storage?.getDirectory)throw Error('Use a current browser over HTTPS with private disk storage enabled.');
 const node=expanded&&f.mode.value==='Node';
 if(!node&&passwordRequirement(f.password.value))throw Error(passwordRequirement(f.password.value));
 if(!node&&f.password.value!==f.confirm.value)throw Error('Passwords do not match.');
 const step=node?0:codeVerification.step;if(!node&&step===null)throw Error('Verify the first code and then click Verify second code before creating the image.');
 let wifi=null;if(!node&&(expanded?f.wirelessEnabled.checked:f.network.value==='wifi')){const ssid=enc.encode(f.ssid.value),pass=f.wifiPassword.value;if(ssid.length<1||ssid.length>32||!/^[\x20-\x7e]{8,63}$/.test(pass)||!/^[A-Z]{2}$/.test(f.country.value))throw Error('Enter a Wi-Fi name (1–32 bytes), password (8–63 printable characters) and country code.');const key=await crypto.subtle.importKey('raw',enc.encode(pass),'PBKDF2',false,['deriveBits']);wifi={ssid:hex(ssid),psk:hex(await crypto.subtle.deriveBits({name:'PBKDF2',salt:ssid,iterations:4096,hash:'SHA-1'},key,256)),country:f.country.value};}
 let network=null,cloud=null;
 if(expanded){
  const read=name=>interfaceSetup(f[name+'Enabled'].checked,f[name+'Method'].value,f[name+'Address'].value,f[name+'Gateway'].value,f[name+'Dns'].value);
  network=node?{ethernet:{enabled:false,method:'dhcp'},wifi:{enabled:false,method:'dhcp'}}:{ethernet:read('ethernet'),wifi:read('wireless')};
  if(!node&&!network.ethernet.enabled&&!network.wifi.enabled)throw Error('Enable Ethernet or Wi-Fi for a headless Main device.');
  if(!node)cloud=cloudSetup(f.cloudEnabled.checked,f.cloudUrl.value,f.register.checked,f.enrollmentCode.value,f.remoteControl.checked,f.remoteUi.checked);
 }
 const setup=node?{schema:2,mode:'Node',username:'',passwordHash:'',secret:'',lastStep:0,wifi:null,network}:{schema:expanded?2:1,username:f.username.value,passwordHash:await passwordHash(f.password.value),secret,lastStep:step,wifi};
 if(expanded&&!node){setup.mode='Main';setup.network=network;setup.cloud=cloud;}
 if(controller.signal.aborted)return;
 payload=enc.encode(JSON.stringify(setup));
 root.querySelector('#all').disabled=true;
 progress.stage('approval','Waiting for download approval','Confirm with your cloud credentials in the popup. The download has not started yet.','Awaiting approval…');
 status.textContent='Confirm this download with your cloud credentials in the popup.';
 const ticket=await bridge.invokeMethodAsync('ApproveDownload',image.digest);if(controller.signal.aborted)return;if(!ticket){progress.stop('Download cancelled','Your setup remains in this tab. You can retry.');status.textContent='Download cancelled.';root.querySelector('#all').disabled=false;return;}
 form.querySelector('fieldset').disabled=true;progress.stage('download','Downloading signed image','Connecting to PiDockCloud for the prebuilt image…','Downloading image…');status.textContent='';
 attempt='pidock-image-'+Date.now()+'-'+crypto.randomUUID();
 const workerPayload=payload.slice();
 const blob=await prepareFactoryImage('/api/factory-images/'+image.digest,ticket,workerPayload,image.manifest,event=>{
  if(event.type==='download')progress.advance(event.loaded,event.total);
  else if(event.type==='verified')progress.stage('personalize','Preparing your configured image','Signature digest checked. Decompressing, adding private setup and recompressing using temporary disk files.','Preparing image…');
  else if(event.type==='progress')progress.advance(event.loaded,event.total);
  else if(event.type==='finishing')progress.stage('finishing','Finishing image file','Writing the final compressed bytes to temporary disk storage. Safari’s download starts after this completes.','Finishing image…');
 },controller.signal,attempt);
 if(controller.signal.aborted)return;progress.stop('Configured image ready','Your browser is saving the configured image. If no save prompt appears, use Save configured image below.',true);payload.fill(0);bytes.fill(0);f.password.value='';f.confirm.value='';f.wifiPassword.value='';f.next.value='';if(expanded)f.enrollmentCode.value='';
 const a=document.createElement('a');objectUrl=URL.createObjectURL(blob);a.href=objectUrl;a.download='pidockos-'+image.manifest.version+'-'+(node?'node':'main')+'-private.img.gz';a.textContent='Save configured image';
 const remove=document.createElement('button');remove.type='button';remove.textContent='Clear temporary image';remove.onclick=()=>clearTemporary().then(()=>{status.textContent='Temporary image cleared. Your saved download is unchanged.';}).catch(()=>{status.textContent='Temporary file cleanup could not finish. Close this tab and revisit provisioning to retry cleanup.';});
 root.querySelector('#download').replaceChildren(a,document.createTextNode(' '),remove);a.click();expiry=setTimeout(()=>{clearTemporary().then(()=>{status.textContent='Temporary image expired and was cleared. Your saved download is unchanged.';}).catch(()=>{status.textContent='Temporary cleanup could not finish. Close this tab and revisit provisioning to retry cleanup.';});},1800000);
 root.querySelector('#secret').textContent='Setup complete';status.textContent=node?'Ready. Flash with OS customization off, connect the Node USB-C data port to a Main, then open USB nodes in the Main UI to adopt it. There is no standalone node owner login. Hardware USB/power acceptance is still required.': 'Ready. Flash using Raspberry Pi Imager → Use custom, with OS customization off. Boot and open https://pidock.local. Keep the authenticator entry; delete the private download after flashing.';
 }catch(error){await clearTemporary().catch(()=>{});if(!controller.signal.aborted){progress.stop('Image preparation stopped',error.message);status.textContent=error.message;form.querySelector('fieldset').disabled=false;}}finally{progress.dispose();payload?.fill(0);active=false;}};
}

// Factory packages use an ordinary streaming HTTP upload, never the Blazor circuit.
export function publicationFile(input) {
    const file = input.files?.[0];
    if (!file || !/\.(pidockfactory|zip)$/i.test(file.name) || file.size <= 0 || file.size > 2147483648) return null;
    return file.name;
}
export function publishFactory(input, ui, ticket) {
    const file = input.files?.[0];
    if (!publicationFile(input)) return Promise.resolve("Choose a signed factory package of at most 2 GiB.");
    ui.panel.hidden = false;
    ui.button.setAttribute("aria-busy", "true");
    ui.panel.dataset.state = "uploading";
    ui.heading.textContent = "Uploading factory image";
    ui.status.textContent = "0% uploaded";
    ui.progress.value = 0;
    ui.button.textContent = "Uploading…";
    ui.explanation.textContent = "Other setup controls are temporarily disabled and hidden while this package uploads and verifies. Keep this tab open and do not refresh it.";
    ui.panel.scrollIntoView({behavior: "smooth", block: "center"});
    return new Promise(resolve => {
        const request = new XMLHttpRequest();
        request.open("POST", "/api/factory-images");
        request.timeout = 2 * 60 * 60 * 1000;
        request.setRequestHeader("Content-Type", "application/octet-stream");
        request.setRequestHeader("X-Requested-With", "XMLHttpRequest");
        request.setRequestHeader("X-PiDock-Publish", ticket);
        const finish = (message, success = false) => {
            ui.button.setAttribute("aria-busy", "false");
            ui.panel.dataset.state = success ? "complete" : "failed";
            ui.heading.textContent = success ? "Factory image published" : "Publication was not confirmed";
            ui.status.textContent = message;
            ui.progress.value = success ? 100 : 0;
            ui.progress.hidden = !success;
            ui.button.textContent = "Publish factory image";
            ui.explanation.textContent = success
                ? "The buttons are available again. Select the published image to review all settings and prepare your download."
                : "The buttons are available again. Check the image list before starting another upload.";
            resolve(message);
        };
        ui.progress.hidden = false;
        let lastPercent = -1;
        request.upload.onprogress = event => {
            if (!event.lengthComputable) { ui.progress.removeAttribute("value"); return; }
            const percent = Math.floor(event.loaded * 100 / event.total);
            ui.progress.value = percent;
            if (event.loaded < event.total) {
                if (percent !== lastPercent) ui.status.textContent = `${percent}% uploaded · ${(event.loaded / 1048576).toFixed(1)} of ${(event.total / 1048576).toFixed(1)} MiB`;
                lastPercent = percent;
            } else {
                ui.panel.dataset.state = "verifying";
                ui.heading.textContent = "Verifying factory image";
                ui.status.textContent = "Upload complete. Checking the signature and image contents; this can take several minutes.";
                ui.progress.removeAttribute("value");
                ui.button.textContent = "Verifying…";
            }
        };
        request.onload = () => {
            if (request.status === 200) { input.value = ""; finish("Signed factory image published.", true); }
            else if ([401, 403].includes(request.status)) finish("Publication authorization expired. Sign in if needed, then publish again with fresh authentication.");
            else finish("Publication failed. Check the signed package, trust key, immutable version and available storage, then try again.");
        };
        request.onerror = request.ontimeout = request.onabort = () => finish("Publication interrupted. Refresh to check whether the image was published before trying again.");
        try { request.send(file); } catch { finish("Publication could not start. Choose the package again and retry."); }
    });
}

export function verifyPreparedFactory(ui) {
    ui.panel.hidden = false; ui.panel.dataset.state = "verifying";
    ui.button.setAttribute("aria-busy", "true"); ui.button.textContent = "Verifying…";
    ui.heading.textContent = "Verifying prepared factory image";
    ui.status.textContent = "Checking the signature and image contents; this can take several minutes.";
    ui.progress.hidden = false; ui.progress.removeAttribute("value");
    ui.explanation.textContent = "Other setup controls are temporarily disabled and hidden during verification. Keep this tab open.";
    ui.panel.scrollIntoView({behavior: "smooth", block: "center"});
}
export function preparedFactoryResult(ui, success, message) {
    ui.button.setAttribute("aria-busy", "false");ui.button.textContent = "Publish prepared factory image";
    ui.panel.dataset.state = success ? "complete" : "failed";
    ui.heading.textContent = success ? "Factory image published" : "Publication was not confirmed";
    ui.status.textContent = message; ui.progress.value = success ? 100 : 0; ui.progress.hidden = !success;
    ui.explanation.textContent = "The buttons are available again. Check the image list to continue.";
}
