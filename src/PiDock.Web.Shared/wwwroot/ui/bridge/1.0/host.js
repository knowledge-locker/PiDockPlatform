// Trusted host module. Never loaded from an application service.
const bindings = new WeakMap();
const maxBytes = 12288;
const guid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
export function attach(frame, dotnet) {
  dispose(frame);
  const binding = { port: null, ready: false, closed: false, nonce: '', subscription: null, established: false, delivery: null, pending: new Set(), last: [] };
  bindings.set(frame, binding);
  const reply = (port, data) => { if (!binding.closed && binding.port === port) port.postMessage(data); };
  async function connect() {
    if (binding.closed || binding.established) return;
    binding.port?.close(); binding.ready = false; binding.pending.clear(); binding.subscription = null;
    try { await dotnet.invokeMethodAsync('StopStream'); } catch { return; }
    if (binding.closed) return;
    const channel = new MessageChannel(), nonce = crypto.randomUUID();
    binding.port = channel.port1; binding.nonce = nonce;
    const port = channel.port1;
    port.onmessage = async event => {
      const data = event.data;
      try { if (!data || new TextEncoder().encode(JSON.stringify(data)).length > maxBytes) return; } catch { return; }
      if (!binding.ready) {
        if (data.type !== 'ready' || data.nonce !== nonce || data.version !== '1.0') return;
        try {
          const capabilities = await dotnet.invokeMethodAsync('GetCapabilities');
          if (binding.port !== port) return;
          binding.ready = true; binding.established = true; reply(port, {type:'connected',version:'1.0',capabilities});
        } catch { reply(port, {type:'closed',reason:'not_authorized'}); }
        return;
      }
      if (data.version !== '1.0' || !guid(data.id)) return;
      if(data.type==='ack' && binding.delivery?.id===data.deliveryId && binding.delivery.subscription===data.id){binding.delivery.resolve();return;}
      const now = Date.now(); binding.last = binding.last.filter(t => now - t < 1000);
      if (binding.last.length >= 20) { reply(port,{type:'result',id:data.id,ok:false,errorCode:'rate_limited'}); return; }
      binding.last.push(now);
      if (data.type === 'command') {
        if(binding.pending.has(data.id))return;
        if (binding.pending.size >= 4) { reply(port,{type:'result',id:data.id,ok:false,errorCode:'busy'}); return; }
        binding.pending.add(data.id);
        try {
          const result = await dotnet.invokeMethodAsync('ExecuteCommand', {schemaVersion:1,commandId:data.id,name:data.name,arguments:data.arguments});
          reply(port,{type:'result',id:data.id,ok:result.succeeded,data:result.data,errorCode:result.errorCode});
        } catch { reply(port,{type:'result',id:data.id,ok:false,errorCode:'outcome_unknown'}); }
        finally { binding.pending.delete(data.id); }
      } else if (data.type === 'subscribe') {
        if (binding.subscription) return;
        binding.subscription = data.id;
        try { await dotnet.invokeMethodAsync('StartStream',data.id,data.cursor ?? null); }
        catch { binding.subscription = null; reply(port,{type:'stream',id:data.id,message:{type:'status',status:'unavailable'}}); }
      } else if (data.type === 'unsubscribe' && binding.subscription === data.id) {
        binding.subscription = null;
        try { await dotnet.invokeMethodAsync('StopStream'); } catch { }
      }
    };
    port.start();
    // Opaque frames have no targetable origin. Transfer only to this exact iframe WindowProxy.
    // No null-origin window messages are accepted by the host; replies travel on the transferred port.
    frame.contentWindow.postMessage({type:'pidock.connect',version:'1.0',nonce},'*',[channel.port2]);
  }
  binding.load = connect; frame.addEventListener('load', connect); connect();
}
export async function publish(frame, id, message) {
  const binding = bindings.get(frame);
  if (!binding?.ready || binding.closed || binding.subscription !== id) return;
  const deliveryId=crypto.randomUUID();
  await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{binding.delivery=null;binding.subscription=null;reject(new Error('stream_delivery_timeout'));},3000);
    binding.delivery={id:deliveryId,subscription:id,resolve(){clearTimeout(timer);binding.delivery=null;resolve();},reject(){clearTimeout(timer);binding.delivery=null;reject(new Error('closed'));}};
    binding.port.postMessage({type:'stream',id,deliveryId,message});
  });
  if (message.type === 'status' && message.status !== 'connected') binding.subscription = null;
}

export function dispose(frame) {
  const binding = bindings.get(frame); if (!binding) return;
  binding.closed = true; binding.delivery?.reject(); frame.removeEventListener('load',binding.load);
  try { binding.port?.postMessage({type:'closed',reason:'closed'}); } catch { }
  binding.port?.close(); bindings.delete(frame);
}
