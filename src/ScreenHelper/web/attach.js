// Прикрепляет картинку к сообщению в веб-чате: через input[type=file] или событие вставки.
window.__shAttach=async function(b64,name){
 const bin=atob(b64),u=new Uint8Array(bin.length);for(let i=0;i<bin.length;i++)u[i]=bin.charCodeAt(i);
 const f=new File([u],name||'screenshot.png',{type:'image/png'});const dt=new DataTransfer();dt.items.add(f);
 const inp=[...document.querySelectorAll('input[type=file]')].find(i=>!i.accept||/image|png|\*/.test(i.accept))||document.querySelector('input[type=file]');
 if(inp){try{inp.files=dt.files;inp.dispatchEvent(new Event('change',{bubbles:true}));return 'file-input'}catch(e){}}
 const ed=[...document.querySelectorAll('textarea,[contenteditable=true],div[role=textbox]')].pop();
 if(!ed)return 'noinput';ed.focus();
 const ev=new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:dt});ed.dispatchEvent(ev);return 'paste'};
