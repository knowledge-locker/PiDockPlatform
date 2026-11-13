// Device credentials stay in this DOM, never in Blazor state or requests.
const enc=new TextEncoder();
const hex=b=>[...new Uint8Array(b)].map(x=>x.toString(16).padStart(2,'0')).join('');
export function clear(host){host.shadowRoot?.replaceChildren();}
export async function passwordHash(password,salt=crypto.getRandomValues(new Uint8Array(16))){
 const key=await crypto.subtle.importKey('raw',enc.encode(password),'PBKDF2',false,['deriveBits']);
 const hash=new Uint8Array(await crypto.subtle.deriveBits({name:'PBKDF2',salt,iterations:210000,hash:'SHA-512'},key,256));
 const out=new Uint8Array(61),v=new DataView(out.buffer);out[0]=1;v.setUint32(1,2);v.setUint32(5,210000);v.setUint32(9,16);out.set(salt,13);out.set(hash,29);return btoa(String.fromCharCode(...out));
}
export function base32(bytes){let text='',bits=0,buf=0;for(const b of bytes){buf=(buf<<8)|b;bits+=8;while(bits>=5){bits-=5;text+='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'[(buf>>>bits)&31];}}return text;}
export async function totp(bytes,step){const b=new Uint8Array(8);new DataView(b.buffer).setBigUint64(0,BigInt(step));const key=await crypto.subtle.importKey('raw',bytes,{name:'HMAC',hash:'SHA-1'},false,['sign']);const h=new Uint8Array(await crypto.subtle.sign('HMAC',key,b));return ((new DataView(h.buffer).getUint32(h[19]&15)&0x7fffffff)%1000000).toString().padStart(6,'0');}
export function patchStream(extents,payload,size){
 let end=0,total=0;for(const p of extents){if(!Number.isSafeInteger(p.offset)||!Number.isSafeInteger(p.length)||p.offset<end||p.length<=0||p.offset+p.length>size)throw Error('Invalid setup area');end=p.offset+p.length;total+=p.length;}
 if(total!==1048576||payload.length>total)throw Error('Invalid setup area');
 const data=new Uint8Array(total).fill(32);data.set(payload);let position=0;
 return new TransformStream({transform(chunk,c){const b=new Uint8Array(chunk).slice();let source=0;for(const p of extents){const lo=Math.max(position,p.offset),hi=Math.min(position+b.length,p.offset+p.length);if(lo<hi)b.set(data.subarray(source+lo-p.offset,source+hi-p.offset),lo-position);source+=p.length;}position+=b.length;if(position>size)throw Error('Image size mismatch');c.enqueue(b);},flush(){if(position!==size)throw Error('Image size mismatch');data.fill(0);}});
}
export function configure(host,image,bridge){
 clear(host);const root=host.shadowRoot||host.attachShadow({mode:'open'});
 root.innerHTML=`<style>:host{font:inherit}label{display:block;margin:1em 0}input,select{display:block;box-sizing:border-box;width:100%;padding:.7em;margin-top:.4em;font:inherit;border:1px solid #bcb5b8;border-radius:6px}button{font:inherit;cursor:pointer;padding:.8em;border-radius:7px;border:0;background:#c4144e;color:white}fieldset{border:0;padding:0}output{display:block;margin:1em 0}code{user-select:all;overflow-wrap:anywhere}</style><form><fieldset><legend>Local appliance owner</legend>
 <label>Username<input name="username" pattern="[A-Za-z0-9-]{1,64}" maxlength="64" autocomplete="off" required></label>
 <label>Appliance password<input name="password" type="password" minlength="14" maxlength="256" autocomplete="new-password" required></label>
 <label>Confirm password<input name="confirm" type="password" autocomplete="new-password" required></label>
 <p>Add a new PiDockOS entry to your authenticator with this setup key:</p><code id="secret"></code>
 <label>Current authenticator code<input name="code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off"></label><button id="verify" type="button">Verify first code</button><output id="verification" role="status"></output>
 <label>Next authenticator code<input name="next" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="off" required></label>
 <label>Network<select name="network"><option value="wifi">Wi-Fi</option><option value="ethernet">Ethernet</option></select></label><div id="wifi">
 <label>Wi-Fi network name<input name="ssid" maxlength="32" autocomplete="off"></label><label>Wi-Fi password<input name="wifiPassword" type="password" autocomplete="off"></label><label>Country code<input name="country" value="US" maxlength="2" pattern="[A-Z]{2}"></label></div>
 <p>Cloud enrollment and remote permissions are approved separately after first boot.</p><button type="submit">Create and download image</button></fieldset><output id="status" role="status"></output><div id="download"></div></form>`;
 const form=root.querySelector('form'),f=form.elements,status=root.querySelector('#status'),bytes=crypto.getRandomValues(new Uint8Array(20)),secret=base32(bytes);let first=-1,active=false,objectUrl;
 root.querySelector('#secret').textContent=secret;
 root.querySelector('#verify').onclick=async()=>{const s=Math.floor(Date.now()/30000);if(f.code.value===await totp(bytes,s)){first=s;f.code.value='';root.querySelector('#verification').textContent='Verified. Wait for the next code.';}else root.querySelector('#verification').textContent='Enter the current code from the new authenticator entry.';};
 f.network.onchange=()=>root.querySelector('#wifi').hidden=f.network.value!=='wifi';
 form.onsubmit=async e=>{e.preventDefault();if(active)return;active=true;
 try{
 if(!isSecureContext||!globalThis.CompressionStream||!globalThis.DecompressionStream)throw Error('Use a current browser over HTTPS.');
 if(f.password.value!==f.confirm.value)throw Error('Passwords do not match.');
 const step=Math.floor(Date.now()/30000);if(first<0||step!==first+1||f.next.value!==await totp(bytes,step))throw Error('Verify two consecutive codes. Start again with a current first code if needed.');
 let wifi=null;if(f.network.value==='wifi'){const ssid=enc.encode(f.ssid.value),pass=f.wifiPassword.value;if(ssid.length<1||ssid.length>32||!/^[\x20-\x7e]{8,63}$/.test(pass)||!/^[A-Z]{2}$/.test(f.country.value))throw Error('Enter a Wi-Fi name (1–32 bytes), password (8–63 printable characters) and country code.');const key=await crypto.subtle.importKey('raw',enc.encode(pass),'PBKDF2',false,['deriveBits']);wifi={ssid:hex(ssid),psk:hex(await crypto.subtle.deriveBits({name:'PBKDF2',salt:ssid,iterations:4096,hash:'SHA-1'},key,256)),country:f.country.value};}
 const payload=enc.encode(JSON.stringify({schema:1,username:f.username.value,passwordHash:await passwordHash(f.password.value),secret,lastStep:step,wifi}));
 status.textContent='Confirm this download with your cloud credentials in the popup.';
 const ticket=await bridge.invokeMethodAsync('ApproveDownload',image.digest);if(!ticket){status.textContent='Download cancelled.';return;}
 form.querySelector('fieldset').disabled=true;status.textContent='Downloading and verifying signed image…';
 const response=await fetch('/api/factory-images/'+image.digest,{headers:{'X-PiDock-Download':ticket},cache:'no-store',credentials:'same-origin'});if(!response.ok)throw Error('Download authorization expired. Try again.');
 const compressed=await response.arrayBuffer();if(compressed.byteLength!==image.manifest.compressedBytes||hex(await crypto.subtle.digest('SHA-256',compressed))!==image.manifest.compressedSha256)throw Error('Image verification failed.');
 status.textContent='Adding private setup in your browser…';
 const stream=new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip')).pipeThrough(patchStream(image.manifest.provisioning,payload,image.manifest.imageBytes)).pipeThrough(new CompressionStream('gzip'));
 const blob=await new Response(stream).blob();payload.fill(0);bytes.fill(0);f.password.value='';f.confirm.value='';f.wifiPassword.value='';f.next.value='';
 const a=document.createElement('a');objectUrl=URL.createObjectURL(blob);a.href=objectUrl;a.download='pidockos-'+image.manifest.version+'-private.img.gz';a.textContent='Save configured image';root.querySelector('#download').replaceChildren(a);a.click();setTimeout(()=>{URL.revokeObjectURL(objectUrl);a.removeAttribute('href');},300000);
 root.querySelector('#secret').textContent='Setup complete';status.textContent='Ready. Flash using Raspberry Pi Imager → Use custom, with OS customization off. Boot and open https://pidock.local. Keep the authenticator entry; delete the private download after flashing.';
 }catch(error){status.textContent=error.message;form.querySelector('fieldset').disabled=false;}finally{active=false;}};
}
