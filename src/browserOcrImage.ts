/** Encode before calling Tesseract. Its recognize(canvas) performs asynchronous
 * image preparation internally and can post to a terminated worker after abort. */
export function prepareOcrImage(canvas:HTMLCanvasElement,signal:AbortSignal):Promise<Uint8Array>{
 return new Promise((resolve,reject)=>{
  let settled=false;
  const cleanup=()=>signal.removeEventListener('abort',abort);
  const fail=(error:unknown)=>{if(settled)return;settled=true;cleanup();reject(error instanceof Error?error:Error('לא ניתן להכין את התמונה לחילוץ.'))};
  const abort=()=>fail(new DOMException('החילוץ בוטל.','AbortError'));
  signal.addEventListener('abort',abort,{once:true});
  if(signal.aborted){abort();return}
  try{
   canvas.toBlob(blob=>{
    if(settled)return;
    if(!blob){fail(Error('לא ניתן להכין את התמונה לחילוץ.'));return}
    // Observe every continuation, including read errors that arrive after abort.
    Promise.resolve().then(()=>settled?null:blob.arrayBuffer()).then(buffer=>{
     if(settled||buffer===null)return;
     settled=true;cleanup();resolve(new Uint8Array(buffer));
    },fail);
   },'image/png');
  }catch(error){fail(error)}
 });
}
