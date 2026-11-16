import {patchStream} from './provisioning.js';
import {downloadVerifiedImage,writeImageStream} from './provisioning-image-io.js';
self.onmessage=async({data:{url,ticket,setup,manifest,attempt}})=>{
 const payload=new Uint8Array(setup);let directory,idleTimer;
 try{
  if(!/^pidock-image-[0-9]+-[0-9a-f-]+$/.test(attempt))throw Error('Invalid temporary image directory.');
  const storage=await navigator.storage.getDirectory();
  const estimate=await navigator.storage.estimate(),required=manifest.compressedBytes+manifest.imageBytes+67108864;
  if(estimate.quota!==undefined&&estimate.usage!==undefined&&estimate.quota-estimate.usage<required)throw Error('Not enough browser disk storage. Free at least '+Math.ceil(required/1048576)+' MiB and retry.');
  directory=await storage.getDirectoryHandle(attempt,{create:true});
  const base=await directory.getFileHandle('base.gz',{create:true});let last=0;
  if(typeof base.createSyncAccessHandle!=='function')throw Error('This browser cannot prepare large images on disk. Update your browser or enable its private disk storage.');
  const network=new AbortController(),resetIdle=()=>{clearTimeout(idleTimer);idleTimer=setTimeout(()=>network.abort(),120000);};resetIdle();
  const report=(type,loaded,total)=>{if(loaded===0||loaded===total||performance.now()-last>=250){last=performance.now();self.postMessage({type,loaded,total});}};
  const response=await fetch(url,{headers:{'X-PiDock-Download':ticket},cache:'no-store',credentials:'same-origin',signal:network.signal});ticket=null;
  await downloadVerifiedImage(response,base,manifest,(loaded,total)=>{resetIdle();report('download',loaded,total);});clearTimeout(idleTimer);
  self.postMessage({type:'verified'});last=0;
  const input=await base.getFile(),output=await directory.getFileHandle('configured.img.gz',{create:true});
  const stream=input.stream().pipeThrough(new DecompressionStream('gzip')).pipeThrough(patchStream(manifest.provisioning,payload,manifest.imageBytes,(loaded,total)=>{report('progress',loaded,total);if(loaded===total)self.postMessage({type:'finishing'});})).pipeThrough(new CompressionStream('gzip'));
  // Browser gzip settings differ from the factory compressor. Bound by raw size plus gzip block overhead.
  await writeImageStream(stream,await output.createSyncAccessHandle(),{limit:manifest.imageBytes+Math.ceil(manifest.imageBytes/16384)*5+32});
  await directory.removeEntry('base.gz');
  // Structured cloning a disk-backed File avoids copying its contents into JavaScript memory.
  self.postMessage({type:'complete',file:await output.getFile()});
 }catch(error){
  if(directory){try{await (await navigator.storage.getDirectory()).removeEntry(attempt,{recursive:true});}catch{}}
  self.postMessage({type:'error',message:error.name==='AbortError'?'The image transfer stopped receiving data for two minutes. Check your connection and retry.':error.name==='QuotaExceededError'?'Browser disk storage is full. Free some space and retry.':error.message});
 }finally{clearTimeout(idleTimer);payload.fill(0);}
};
