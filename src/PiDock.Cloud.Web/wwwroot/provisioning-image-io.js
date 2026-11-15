import {sha256} from './vendor/noble-hashes/sha2.js';

// One stream chunk at a time. Neither the compressed nor raw image is buffered in memory.
export async function writeImageStream(stream,handle,{limit,expected,hash,onProgress=()=>{}}={}){
 const reader=stream.getReader();let position=0;
 try{
  for(;;){
   const {done,value}=await reader.read();if(done)break;
   if(position+value.byteLength>limit)throw Error('The image exceeded its allowed size.');
   hash?.update(value);let offset=0;
   while(offset<value.byteLength){const written=await handle.write(value.subarray(offset),{at:position+offset});if(!Number.isInteger(written)||written<=0||written>value.byteLength-offset)throw Error('Temporary disk storage could not complete the image write.');offset+=written;}
   position+=value.byteLength;onProgress(position);
  }
  if(expected!==undefined&&position!==expected)throw Error('The image download ended early. Check your connection and retry.');
  await handle.truncate(position);await handle.flush();return position;
 }catch(error){await reader.cancel().catch(()=>{});throw error;}
 finally{reader.releaseLock();await handle.close();}
}
export async function downloadVerifiedImage(response,file,manifest,onProgress){
 const total=manifest.compressedBytes;
 if(!Number.isSafeInteger(total)||total<=0||total>2147483648||!Number.isSafeInteger(manifest.imageBytes)||manifest.imageBytes<1048576||! /^[0-9a-f]{64}$/.test(manifest.compressedSha256))throw Error('Invalid signed image manifest.');
 if(!response.ok)throw Error('Download authorization expired or the image is unavailable. Try again.');
 if(!response.body)throw Error('This browser does not support streaming image downloads.');
 const length=response.headers.get('Content-Length');if(length!==null&&Number(length)!==total)throw Error('The image download size does not match its manifest.');
 const hash=sha256.create();
 try{
  onProgress(0,total);
  await writeImageStream(response.body,await file.createSyncAccessHandle(),{limit:total,expected:total,hash,onProgress:count=>onProgress(count,total)});
  const digest=Array.from(hash.digest(),x=>x.toString(16).padStart(2,'0')).join('');
  if(digest!==manifest.compressedSha256)throw Error('Image verification failed. Downloaded bytes do not match the signed manifest.');
 }finally{hash.destroy();}
}
