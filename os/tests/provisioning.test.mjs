import {readFileSync} from 'node:fs';
import {webcrypto} from 'node:crypto';
import assert from 'node:assert/strict';
if(!globalThis.crypto)globalThis.crypto=webcrypto;
const source=readFileSync(new URL('../../src/PiDock.Cloud.Web/wwwroot/provisioning.js',import.meta.url));
const {totp,passwordHash,passwordRequirement,setupCodeControls,patchStream,base32,interfaceSetup,cloudSetup,publicationFile,publishFactory,verifyPreparedFactory,preparedFactoryResult}=await import('data:text/javascript;base64,'+source.toString('base64'));
assert.equal(await totp(new TextEncoder().encode('12345678901234567890'),1),'287082');
assert.equal(base32(new TextEncoder().encode('12345678901234567890')),'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ');
const hash=Buffer.from(await passwordHash('a long private password',new Uint8Array(16)),'base64');assert.equal(hash.length,61);assert.equal(hash.readUInt32BE(5),210000);
const size=1049600,input=new Uint8Array(size).fill(77),payload=new TextEncoder().encode('{"schema":1}');
const output=new Uint8Array(await new Response(new Blob([input]).stream().pipeThrough(patchStream([{offset:128,length:1048576}],payload,size))).arrayBuffer());
assert.deepEqual(output.subarray(0,128),input.subarray(0,128));assert.deepEqual(output.subarray(128,128+payload.length),payload);assert.equal(output[128+payload.length],32);assert.equal(output.at(-1),77);
assert.throws(()=>patchStream([{offset:10,length:1048576},{offset:1,length:10}],payload,size));
console.log('Browser password, authenticator and bounded streaming personalization checks passed.');

assert.deepEqual(interfaceSetup(true,'dhcp'),{enabled:true,method:'dhcp'});
assert.deepEqual(interfaceSetup(false,'static','bad'),{enabled:false,method:'dhcp'});
assert.deepEqual(interfaceSetup(true,'static','192.168.5.20/24','192.168.5.1','1.1.1.1, 8.8.8.8'),{enabled:true,method:'static',address:'192.168.5.20/24',gateway:'192.168.5.1',dns:['1.1.1.1','8.8.8.8']});
for(const address of ['192.168.1.0/24','192.168.1.255/24','127.0.0.2/24','192.168.1.4/24\nDNS=evil'])assert.throws(()=>interfaceSetup(true,'static',address));
assert.throws(()=>interfaceSetup(true,'static','192.168.1.4/24','192.168.2.1'));
assert.throws(()=>interfaceSetup(true,'static','192.168.1.4/24','','evil\nDNS=1.1.1.1'));
assert.equal(cloudSetup(false,'',false,'',true,true),null);
assert.deepEqual(cloudSetup(true,'https://cloud.example',true,'aaaa-aaaa-aaaa-aaaa-aaaa-aaaa-aaaa-aaaa',true,false),{url:'https://cloud.example/',enrollmentCode:'A'.repeat(32),remoteControl:true,remoteUi:false});
for(const url of ['http://cloud.example','https://owner@cloud.example','https://cloud.example/path','https://cloud.example/?secret=1'])assert.throws(()=>cloudSetup(true,url,false,''));
assert.throws(()=>cloudSetup(true,'https://cloud.example',false,'',true,false));
console.log('Independent interface addressing and explicit cloud consent checks passed.');

assert.equal(publicationFile({files:[]}),null);
assert.equal(publicationFile({files:[{name:'image.pidockfactory',size:100}]}),'image.pidockfactory');
assert.equal(publicationFile({files:[{name:'image.pidockfactory',size:2147483649}]}),null);
const ui={
    panel:{hidden:true,dataset:{},setAttribute(name,value){this[name]=value;},scrollIntoView(){}},
    heading:{textContent:''},status:{textContent:''},explanation:{textContent:''},button:{textContent:'',setAttribute(name,value){this[name]=value;}},
    progress:{value:0,hidden:false,removeAttribute(name){if(name==='value')this.value=undefined;}}
};
class UploadRequest {
    static response=200;static networkError=false;upload={};
    open(method,url) { assert.equal(method,'POST');assert.equal(url,'/api/factory-images'); }
    setRequestHeader(name,value) { if(name==='X-PiDock-Publish')assert.equal(value,'approved-once'); }
    send(file) {
        assert.equal(file.name,'image.pidockfactory');
        assert.equal(ui.panel.hidden,false);assert.equal(ui.button['aria-busy'],'true');
        assert.match(ui.explanation.textContent,/temporarily disabled and hidden/);
        assert.equal(ui.button.textContent,'Uploading…');
        this.upload.onprogress({lengthComputable:false});assert.equal(ui.progress.value,undefined);
        this.upload.onprogress({lengthComputable:true,loaded:50,total:100});
        assert.match(ui.status.textContent,/50% uploaded/);assert.equal(ui.progress.value,50);
        this.upload.onprogress({lengthComputable:true,loaded:100,total:100});
        assert.equal(ui.heading.textContent,'Verifying factory image');
        assert.equal(ui.button.textContent,'Verifying…');assert.equal(ui.progress.value,undefined);
        this.status=UploadRequest.response;
        if(UploadRequest.networkError)this.onerror();else this.onload();
    }
}
globalThis.XMLHttpRequest=UploadRequest;
const fileInput={files:[{name:'image.pidockfactory',size:100}],value:'selected'};
assert.match(await publishFactory(fileInput,ui,'approved-once'),/published/);
assert.equal(fileInput.value,'');assert.equal(ui.panel.dataset.state,'complete');
assert.equal(ui.button['aria-busy'],'false');assert.equal(ui.progress.value,100);
assert.match(ui.explanation.textContent,/buttons are available again/);
UploadRequest.response=403;
assert.match(await publishFactory(fileInput,ui,'approved-once'),/authorization expired/);
assert.equal(ui.panel.dataset.state,'failed');assert.equal(ui.progress.hidden,true);
UploadRequest.networkError=true;
assert.match(await publishFactory(fileInput,ui,'approved-once'),/interrupted/);
assert.equal(ui.button['aria-busy'],'false');assert.equal(ui.button.textContent,'Publish factory image');
console.log('HTTP publication progress, verification, disabled-control explanation and retry recovery checks passed.');

verifyPreparedFactory(ui);
assert.equal(ui.panel.dataset.state,'verifying');assert.equal(ui.progress.value,undefined);
assert.match(ui.explanation.textContent,/temporarily disabled/);
preparedFactoryResult(ui,true,'Published');assert.equal(ui.progress.value,100);
assert.equal(ui.button.textContent,'Publish prepared factory image');
preparedFactoryResult(ui,false,'Not confirmed');assert.equal(ui.progress.hidden,true);
console.log('Prepared-package verification progress and completion checks passed.');

assert.match(passwordRequirement('x'.repeat(13)),/14–256/);
assert.equal(passwordRequirement('x'.repeat(14)),'');
assert.equal(passwordRequirement('a long passphrase with spaces'),'');
assert.equal(passwordRequirement('x'.repeat(256)),'');
assert.match(passwordRequirement('x'.repeat(257)),/14–256/);
console.log('Appliance password length and passphrase rules passed.');

// Safari may keep an input focused when a verification button is clicked, then
// dispatch change after our handler clears it and moves focus/disables it.
let focused=null;
class CodeInput extends EventTarget {
 value='';required=false;dirty=false;_disabled=false;
 set disabled(value){this._disabled=value;if(value&&focused===this)this.blur();}
 get disabled(){return this._disabled;}
 focus(){if(focused!==this){focused?.blur();focused=this;}}
 blur(){if(this.dirty){this.dirty=false;this.dispatchEvent(new Event('change'));}if(focused===this)focused=null;}
 type(value){this.value=value;this.dirty=true;this.dispatchEvent(new Event('input'));}
}
const controls={firstInput:new CodeInput(),secondInput:new CodeInput(),firstButton:{},secondButton:{},firstStatus:{},secondStatus:{},help:{}};
const fixtureKey=new TextEncoder().encode('12345678901234567890');let setupStep=1000;
const codeProof=setupCodeControls(controls,fixtureKey,new AbortController().signal,()=>setupStep);
controls.firstInput.focus();controls.firstInput.type(await totp(fixtureKey,setupStep));
await controls.firstButton.onclick();
assert.equal(controls.secondInput.disabled,false,'blur after clearing first code must retain verification');
setupStep++;controls.secondInput.type(await totp(fixtureKey,setupStep));
assert.equal(codeProof.step,null,'entering a second code is not verification');
await controls.secondButton.onclick();
assert.equal(codeProof.step,setupStep,'change on clearing/disabling second code must retain proof');
controls.firstInput.dispatchEvent(new Event('change'));controls.secondInput.dispatchEvent(new Event('change'));
setupStep+=10;assert.equal(codeProof.step,setupStep-10,'completed proof survives later blur and time spent on settings');
controls.firstInput.type(await totp(fixtureKey,setupStep));
assert.equal(codeProof.step,null,'actual edits must invalidate proof');
assert.equal(controls.secondInput.disabled,true);
await controls.firstButton.onclick();setupStep+=2;
controls.secondInput.type(await totp(fixtureKey,setupStep));await controls.secondButton.onclick();
assert.equal(codeProof.step,null);assert.match(controls.secondStatus.textContent,/expired/);
console.log('Safari-style deferred change, submission readiness, edit invalidation and expiry regressions passed.');
