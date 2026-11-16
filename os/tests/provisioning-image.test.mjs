import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {readFileSync} from 'node:fs';
import {downloadVerifiedImage,writeImageStream} from '../../src/PiDock.Cloud.Web/wwwroot/provisioning-image-io.js';
function disk({partial=Infinity,fail=false}={}){
 const hash=createHash('sha256');let position=0,maxChunk=0,closed=false;
 const handle={write(bytes,{at}){assert.equal(at,position);if(fail)throw Error('disk full');const length=Math.min(partial,bytes.length);hash.update(bytes.subarray(0,length));position+=length;maxChunk=Math.max(maxChunk,bytes.length);return length;},truncate(n){assert.equal(n,position);},flush(){},close(){closed=true;}};
 return {createSyncAccessHandle:async()=>handle,handle,get position(){return position;},get closed(){return closed;},get maxChunk(){return maxChunk;},digest:()=>hash.digest('hex')};
}
const size=128*1024*1024,chunk=new Uint8Array(65536).fill(83),expected=createHash('sha256');for(let n=0;n<size;n+=chunk.length)expected.update(chunk);
const manifest={compressedBytes:size,imageBytes:size,compressedSha256:expected.digest('hex')};
function response(length=size,corrupt=false){let emitted=0;return new Response(new ReadableStream({pull(c){if(emitted>=length){c.close();return;}const next=chunk.slice(0,Math.min(chunk.length,length-emitted));if(corrupt&&emitted===0)next[0]^=1;c.enqueue(next);emitted+=next.length;}}));}
const target=disk({partial:10000});let progress=0;
await downloadVerifiedImage(response(),target,manifest,(n,total)=>{assert.equal(total,size);assert.ok(n>=progress);progress=n;});
assert.equal(target.position,size);assert.equal(progress,size);assert.ok(target.closed);assert.ok(target.maxChunk<=65536);assert.equal(target.digest(),manifest.compressedSha256);
for(const [length,corrupt,message] of [[size-1,false,/ended early/],[size+1,false,/allowed size/],[size,true,/verification failed/]]){
 const bad=disk();await assert.rejects(downloadVerifiedImage(response(length,corrupt),bad,manifest,()=>{}),message);assert.ok(bad.closed);
}
const failed=disk({fail:true});await assert.rejects(writeImageStream(response(65536).body,failed.handle,{limit:size}),/disk full/);assert.ok(failed.closed);
await assert.rejects(downloadVerifiedImage(new Response('short',{headers:{'Content-Length':'5'}}),disk(),manifest,()=>{}),/size does not match/);
// Guard against reintroducing whole-image allocations in the production path.
for(const name of ['provisioning-image-worker.js','provisioning-image-io.js']){
 const source=readFileSync(new URL('../../src/PiDock.Cloud.Web/wwwroot/'+name,import.meta.url),'utf8');assert.doesNotMatch(source,/\.arrayBuffer\(|\.blob\(|subtle\.digest\(/);
}
console.log('128 MiB bounded download, incremental digest, partial disk writes, corruption, truncation, overflow and disk-failure checks passed.');
