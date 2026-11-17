// Framework-neutral classic SDK for an opaque-origin application iframe.
(() => {
  const script = document.currentScript;
  const hostOrigin = new URL(script.src,document.baseURI).origin;
  let port = null, capabilities = null, subscription = null, stopped = false;
  const pending = new Map(); const readyWaiters = [];
  const emitStatus = status => { if (subscription) { subscription.onStatus?.(status); subscription.last = Date.now(); } };
  function close(reason) {
    for (const item of pending.values()) { clearTimeout(item.timeout); item.reject(new Error('outcome_unknown')); }
    pending.clear(); capabilities = null; emitStatus(reason); port?.close(); port = null;
  }
  function send(data) { if (!port || !capabilities) throw new Error('not_connected'); port.postMessage({...data,version:'1.0'}); }
  function connectStream() {
    if (!subscription || !capabilities?.streaming || stopped) return;
    subscription.id = crypto.randomUUID(); subscription.last = Date.now();
    subscription.onStatus?.('connecting');
    send({type:'subscribe',id:subscription.id,cursor:subscription.cursor});
  }
  window.addEventListener('message',event => {
    const data = event.data;
    if (event.source !== window.parent || event.origin !== hostOrigin || data?.type !== 'pidock.connect' || data.version !== '1.0' || typeof data.nonce !== 'string' || data.nonce.length > 64 || event.ports.length !== 1) return;
    close('reconnecting'); stopped = false; port = event.ports[0];
    port.onmessage = async event => {
      const data = event.data; if (!data || typeof data.type !== 'string') return;
      if (data.type === 'connected') {
        if (data.version !== '1.0') return;
        capabilities = data.capabilities;
        while (readyWaiters.length) readyWaiters.shift().resolve(capabilities);
        if (subscription) connectStream();
      } else if (data.type === 'authorizing') {
        const item = pending.get(data.id); if (!item || item.authorizing) return;
        item.authorizing = true; clearTimeout(item.timeout);
        // The trusted host bounds its credential prompt to five minutes, then sends or denies once.
        item.timeout = setTimeout(()=>{pending.delete(data.id);item.reject(new Error('outcome_unknown'));},315000);
      } else if (data.type === 'result') {
        const item = pending.get(data.id); if (!item) return;
        pending.delete(data.id); clearTimeout(item.timeout);
        if (data.ok) item.resolve(data.data); else item.reject(new Error(data.errorCode || 'outcome_unknown'));
      } else if (data.type === 'stream' && subscription?.id === data.id) {
        const message = data.message;
        subscription.last = Date.now();
        if (message.type === 'event') {
          const item = message.event;
          subscription.cursor = {epoch:item.epoch,sequence:item.sequence};
          subscription.attempts = 0;
          if (item.kind !== 'heartbeat') {
            let callbackTimer;
            try {
              await Promise.race([Promise.resolve(subscription.onEvent(item)),new Promise((_,reject)=>{
                callbackTimer=setTimeout(()=>reject(new Error('handler_timeout')),2500);
              })]);
            } catch { stopped=true; emitStatus('handler_error'); try{send({type:'unsubscribe',id:data.id});}catch{} }
            finally { clearTimeout(callbackTimer); }
          }
        } else if (message.type === 'status') {
          try{send({type:'ack',id:data.id,deliveryId:data.deliveryId});}catch{}
          emitStatus(message.status);
          if (message.status === 'connected') return;
          if (message.status === 'revoked' || message.status === 'not_authorized') { stopped = true; return; }
          if (message.status === 'gap' || message.status === 'protocol_error') subscription.cursor = null;
          if (++subscription.attempts > 8) { stopped = true; emitStatus('unavailable'); return; }
          clearTimeout(subscription.retry);
          subscription.retry = setTimeout(connectStream,Math.min(15000,1000*2**Math.min(subscription.attempts,4)));
        }
        if(message.type==='event')try{send({type:'ack',id:data.id,deliveryId:data.deliveryId});}catch{}
      } else if (data.type === 'closed') { stopped = true; close(data.reason || 'closed'); }
    };
    port.start(); port.postMessage({type:'ready',nonce:data.nonce,version:'1.0',authorizationWait:true});
  });
  const ready = () => capabilities ? Promise.resolve(capabilities) : new Promise((resolve,reject) => {
    const waiter = {resolve(value){clearTimeout(timer);resolve(value);},reject};
    const timer = setTimeout(()=>{const i=readyWaiters.indexOf(waiter);if(i>=0)readyWaiters.splice(i,1);reject(new Error('not_connected'));},10000);
    readyWaiters.push(waiter);
  });
  window.PiDockUi = Object.freeze({
    ready,
    async command(name,args={}) {
      const caps = await ready();
      if (!caps.commands.includes(name)) throw new Error('command_denied');
      if(pending.size>=4)throw new Error('busy');
      const id = crypto.randomUUID();
      if (new TextEncoder().encode(JSON.stringify({schemaVersion:1,commandId:id,name,arguments:args})).length > 8192) throw new Error('payload_too_large');
      return new Promise((resolve,reject)=>{
        const timeout=setTimeout(()=>{pending.delete(id);reject(new Error('outcome_unknown'));},15000);
        pending.set(id,{resolve,reject,timeout});
        try{send({type:'command',id,name,arguments:args});}catch(e){clearTimeout(timeout);pending.delete(id);reject(e);}
      });
    },
    async subscribe(onEvent,onStatus=()=>{}) {
      const caps=await ready(); if(!caps.streaming)throw new Error('stream_denied');
      if(subscription)throw new Error('already_subscribed');
      stopped=false; subscription={id:null,cursor:null,onEvent,onStatus,last:Date.now(),attempts:0,retry:null};
      connectStream();
      return ()=>{const old=subscription;subscription=null;stopped=true;if(old){clearTimeout(old.retry);try{send({type:'unsubscribe',id:old.id});}catch{}}};
    }
  });
  setInterval(()=>{if(subscription&&!stopped&&Date.now()-subscription.last>7000){
    emitStatus('stale');const id=subscription.id;try{send({type:'unsubscribe',id});}catch{}
    if(++subscription.attempts>8){stopped=true;emitStatus('unavailable');return;}
    clearTimeout(subscription.retry);subscription.retry=setTimeout(connectStream,2000);
  }},1000);
  window.addEventListener('pagehide',()=>{stopped=true;if(subscription)clearTimeout(subscription.retry);close('closed');});
})();
