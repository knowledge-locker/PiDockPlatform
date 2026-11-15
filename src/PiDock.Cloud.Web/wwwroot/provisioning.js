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
export function base32(bytes){let text='',bits=0,buf=0;for(const b of bytes){buf=(buf<<8)|b;bits+=8;while(bits>=5){bits-=5;text+='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'[(buf>>>bits)&31];}}return text;}
export async function totp(bytes,step){const b=new Uint8Array(8);new DataView(b.buffer).setBigUint64(0,BigInt(step));const key=await crypto.subtle.importKey('raw',bytes,{name:'HMAC',hash:'SHA-1'},false,['sign']);const h=new Uint8Array(await crypto.subtle.sign('HMAC',key,b));return ((new DataView(h.buffer).getUint32(h[19]&15)&0x7fffffff)%1000000).toString().padStart(6,'0');}
export function patchStream(extents,payload,size){
 let end=0,total=0;for(const p of extents){if(!Number.isSafeInteger(p.offset)||!Number.isSafeInteger(p.length)||p.offset<end||p.length<=0||p.offset+p.length>size)throw Error('Invalid setup area');end=p.offset+p.length;total+=p.length;}
 if(total!==1048576||payload.length>total)throw Error('Invalid setup area');
 const data=new Uint8Array(total).fill(32);data.set(payload);let position=0;
 return new TransformStream({transform(chunk,c){const b=new Uint8Array(chunk).slice();let source=0;for(const p of extents){const lo=Math.max(position,p.offset),hi=Math.min(position+b.length,p.offset+p.length);if(lo<hi)b.set(data.subarray(source+lo-p.offset,source+hi-p.offset),lo-position);source+=p.length;}position+=b.length;if(position>size)throw Error('Image size mismatch');c.enqueue(b);},flush(){if(position!==size)throw Error('Image size mismatch');data.fill(0);}});
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
function interfaceForm(name,title,enabled){return `<fieldset class="interface"><legend>${title}</legend><label class="check"><input name="${name}Enabled" type="checkbox" ${enabled?'checked':''}>Enable ${title}</label><div id="${name}Settings"><label>IPv4 addressing<select name="${name}Method"><option value="dhcp">Automatic (DHCP)</option><option value="static">Static</option></select></label><div id="${name}Static" hidden><label>IPv4 address / prefix<input name="${name}Address" placeholder="192.168.1.50/24" autocomplete="off"></label><label>Gateway (optional)<input name="${name}Gateway" placeholder="192.168.1.1" autocomplete="off"></label><label>DNS servers (optional, separated by commas)<input name="${name}Dns" placeholder="192.168.1.1, 1.1.1.1" autocomplete="off"></label></div></div></fieldset>`;}
export function configure(host,image,bridge){
 clear(host);const controller=new AbortController();preparations.set(host,controller);const root=host.shadowRoot||host.attachShadow({mode:'open'}),expanded=image.manifest.setupSchema===2;
 root.innerHTML=`<style>:host{font:inherit}label{display:block;margin:1em 0}input,select{display:block;box-sizing:border-box;width:100%;padding:.7em;margin-top:.4em;font:inherit;border:1px solid #bcb5b8;border-radius:6px}button{font:inherit;cursor:pointer;padding:.8em;border-radius:7px;border:0;background:#c4144e;color:white}fieldset{border:0;padding:0}fieldset.interface{border:1px solid #ded8dc;padding:1em;margin:1em 0}input[type=checkbox]{width:auto;display:inline;margin-right:.5em}.check{display:flex;align-items:center;gap:.4em}[hidden]{display:none!important}output{display:block;margin:1em 0}code{user-select:all;overflow-wrap:anywhere}.credential-pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.2em}.credential-pair>div{min-width:0}.password-control{display:flex;align-items:stretch;gap:.4em}.password-control input{min-width:0;margin:0}.password-control button{flex-shrink:0;background:#eee6e9;color:#522333;padding:.5em .8em}.help{font-size:.92em;color:#5d5558}.credential-pair label{margin-bottom:.5em}button:disabled{opacity:.5;cursor:default}input:disabled{background:#f3f0f1}@media(max-width:640px){.credential-pair{grid-template-columns:1fr;gap:0}}</style><form><fieldset id="all">${expanded?`<label>Mode<select name="mode" aria-describedby="modeDescription"><option value="Main">Main — Primary or standalone Pi</option><option value="Node">Node — USB expansion for a Main</option></select></label><p id="modeDescription"></p><p id="nodeNote" hidden>Prepare an unpaired USB expansion node. No human owner, external network, cloud registration or remote permissions are configured. Connect it to a Main device over a data-capable USB link, then adopt and configure it from Main → USB nodes. The Main owner must confirm adoption.</p>`:'<p>This image supports basic Main setup. Choose an expanded-setup image for mode, static addressing and cloud options.</p>'}<fieldset id="main"><legend>Local appliance owner</legend>
 <label>Username<input name="username" pattern="[A-Za-z0-9-]{1,64}" maxlength="64" autocomplete="off" required></label>
 <p id="passwordRequirements" class="help">Use 14–256 characters. Spaces and passphrases are allowed. Confirmation must match exactly.</p>
 <div class="credential-pair"><div><label for="appliancePassword">Appliance password</label><div class="password-control"><input id="appliancePassword" name="password" type="password" minlength="14" maxlength="256" autocomplete="new-password" aria-describedby="passwordRequirements passwordFeedback" required><button type="button" data-reveal="password" aria-controls="appliancePassword" aria-label="Show appliance password" aria-pressed="false">Show</button></div><p id="passwordFeedback" class="help" role="status"></p></div>
 <div><label for="confirmPassword">Confirm password</label><div class="password-control"><input id="confirmPassword" name="confirm" type="password" maxlength="256" autocomplete="new-password" aria-describedby="confirmationHelp confirmationFeedback" disabled required><button type="button" data-reveal="confirm" aria-controls="confirmPassword" aria-label="Show password confirmation" aria-pressed="false" disabled>Show</button></div><p id="confirmationHelp" class="help">Enter a valid appliance password first.</p><p id="confirmationFeedback" class="help" role="status"></p></div></div>
 <p class="help">Your appliance password and confirmation are processed only in this browser. They are not sent to or stored by PiDockCloud. The appliance password is represented in the downloaded image by a salted PBKDF2-SHA512 hash (210,000 iterations with a random 128-bit salt). Its plaintext is never written to the image.</p>
 <p class="help">The image also contains your authenticator secret and, if configured, a Wi-Fi network key derived in this browser instead of the plaintext Wi-Fi password. The setup area in the download is not encrypted. Keep the configured image private and delete it after flashing.</p>
 <p>Add a new PiDockOS entry to your authenticator with this setup key:</p><code id="secret"></code>
 <div class="credential-pair"><div><label>Current authenticator code<input name="code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off"></label><button id="verify" type="button" disabled>Verify first code</button><output id="verification" role="status"></output></div>
 <div><label>Next authenticator code<input name="next" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off" aria-describedby="nextCodeHelp" disabled required></label><p id="nextCodeHelp" class="help">Verify the first code, then wait for the next 30-second code before entering it here.</p></div></div>
 ${expanded?interfaceForm('ethernet','Ethernet',true)+interfaceForm('wireless','Wi-Fi',true):''}
 <div ${expanded?'hidden':''}><label>Network<select name="network"><option value="wifi">Wi-Fi</option><option value="ethernet">Ethernet</option></select></label></div><div id="wifi">
 <label>Wi-Fi network name<input name="ssid" maxlength="32" autocomplete="off"></label><label>Wi-Fi password<input name="wifiPassword" type="password" autocomplete="off"></label><label>Country code<input name="country" value="US" maxlength="2" pattern="[A-Z]{2}"></label></div>
 ${expanded?`<fieldset class="interface"><legend>PiDockCloud (optional)</legend><label class="check"><input name="cloudEnabled" type="checkbox" aria-describedby="cloudDefault">Configure PiDockCloud</label><p id="cloudDefault"></p><p id="cloudSuggestion"></p><fieldset id="cloudSettings" disabled hidden><label>Cloud URL<input name="cloudUrl" type="url" autocomplete="off" maxlength="512"></label><label class="check"><input name="register" type="checkbox">Register on first boot</label><fieldset id="registrationSettings" disabled hidden><label>Single-use enrollment code<input name="enrollmentCode" autocomplete="off" maxlength="64" spellcheck="false"></label><p>Generate an invitation in the chosen cloud shortly before creating the image. It must still be valid when the Pi first connects. An expired invitation can be replaced locally after boot.</p><label class="check"><input name="remoteControl" type="checkbox">Allow remote container control</label><label class="check"><input name="remoteUi" type="checkbox">Allow remote PiDockOS interface access</label><p>These are separate local permissions, off by default. They take effect only after this registration succeeds. Cloud actions still require fresh MFA; remote UI sessions also require PiDockOS sign-in. Cloud-managed OS updates remain off.</p></fieldset></fieldset></fieldset>`:'<p>Cloud enrollment and remote permissions are approved separately after first boot.</p>'}</fieldset><button type="submit">Create and download image</button></fieldset><output id="status" role="status"></output><div id="download"></div></form>`;
 const form=root.querySelector('form'),f=form.elements,status=root.querySelector('#status'),bytes=crypto.getRandomValues(new Uint8Array(20)),secret=base32(bytes);let first=-1,active=false,objectUrl,payload;
 controller.signal.addEventListener('abort',()=>{bytes.fill(0);payload?.fill(0);if(objectUrl)URL.revokeObjectURL(objectUrl);f.password.value='';f.confirm.value='';f.wifiPassword.value='';if(expanded)f.enrollmentCode.value='';},{once:true});
 root.querySelector('#secret').textContent=secret;
 const verify=root.querySelector('#verify'),confirmationHelp=root.querySelector('#confirmationHelp');
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
 const resetCodes=()=>{first=-1;f.next.value='';f.next.disabled=true;verify.disabled=!/^[0-9]{6}$/.test(f.code.value);root.querySelector('#verification').textContent='';};
 f.code.addEventListener('input',resetCodes);f.code.addEventListener('change',resetCodes);
 verify.onclick=async()=>{
  const candidate=f.code.value,s=Math.floor(Date.now()/30000);
  if(!/^[0-9]{6}$/.test(candidate)){resetCodes();return;}
  const expected=await totp(bytes,s);if(controller.signal.aborted||f.code.value!==candidate)return;
  if(candidate===expected){first=s;f.code.value='';verify.disabled=true;f.next.disabled=false;root.querySelector('#verification').textContent='First code verified. Wait for the next code.';f.next.focus();}
  else{first=-1;f.next.value='';f.next.disabled=true;root.querySelector('#verification').textContent='Enter the current code from the new authenticator entry.';}
 };
 passwordState();

 const sync=()=>{
  if(!expanded){root.querySelector('#wifi').hidden=f.network.value!=='wifi';return;}
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
 if(!isSecureContext||!globalThis.CompressionStream||!globalThis.DecompressionStream)throw Error('Use a current browser over HTTPS.');
 const node=expanded&&f.mode.value==='Node';
 if(!node&&passwordRequirement(f.password.value))throw Error(passwordRequirement(f.password.value));
 if(!node&&f.password.value!==f.confirm.value)throw Error('Passwords do not match.');
 const step=Math.floor(Date.now()/30000);if(!node&&(first<0||step!==first+1||f.next.value!==await totp(bytes,step)))throw Error('Verify two consecutive codes. Start again with a current first code if needed.');
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
 status.textContent='Confirm this download with your cloud credentials in the popup.';
 const ticket=await bridge.invokeMethodAsync('ApproveDownload',image.digest);if(controller.signal.aborted)return;if(!ticket){status.textContent='Download cancelled.';root.querySelector('#all').disabled=false;return;}
 form.querySelector('fieldset').disabled=true;status.textContent='Downloading and verifying signed image…';
 const response=await fetch('/api/factory-images/'+image.digest,{headers:{'X-PiDock-Download':ticket},cache:'no-store',credentials:'same-origin',signal:controller.signal});if(!response.ok)throw Error('Download authorization expired. Try again.');
 const compressed=await response.arrayBuffer();if(compressed.byteLength!==image.manifest.compressedBytes||hex(await crypto.subtle.digest('SHA-256',compressed))!==image.manifest.compressedSha256)throw Error('Image verification failed.');
 status.textContent='Adding private setup in your browser…';
 const stream=new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip')).pipeThrough(patchStream(image.manifest.provisioning,payload,image.manifest.imageBytes)).pipeThrough(new CompressionStream('gzip'),{signal:controller.signal});
 const blob=await new Response(stream).blob();if(controller.signal.aborted)return;payload.fill(0);bytes.fill(0);f.password.value='';f.confirm.value='';f.wifiPassword.value='';f.next.value='';if(expanded)f.enrollmentCode.value='';
 const a=document.createElement('a');objectUrl=URL.createObjectURL(blob);a.href=objectUrl;a.download='pidockos-'+image.manifest.version+'-'+(node?'node':'main')+'-private.img.gz';a.textContent='Save configured image';root.querySelector('#download').replaceChildren(a);a.click();setTimeout(()=>{URL.revokeObjectURL(objectUrl);a.removeAttribute('href');},300000);
 root.querySelector('#secret').textContent='Setup complete';status.textContent=node?'Ready. Flash with OS customization off, connect the Node USB-C data port to a Main, then open USB nodes in the Main UI to adopt it. There is no standalone node owner login. Hardware USB/power acceptance is still required.': 'Ready. Flash using Raspberry Pi Imager → Use custom, with OS customization off. Boot and open https://pidock.local. Keep the authenticator entry; delete the private download after flashing.';
 }catch(error){if(!controller.signal.aborted){status.textContent=error.message;form.querySelector('fieldset').disabled=false;}}finally{payload?.fill(0);active=false;}};
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
