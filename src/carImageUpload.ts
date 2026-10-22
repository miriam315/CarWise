export async function prepareCarImage(file:File):Promise<File>{
 if(!['image/jpeg','image/png','image/webp'].includes(file.type)||file.size>12*1024*1024)throw Error('אפשר להעלות JPG, PNG או WebP, עד 12MB לתמונה.');
 const bitmap=await createImageBitmap(file);
 try{
  if(bitmap.width*bitmap.height>40_000_000)throw Error('התמונה גדולה מדי לפענוח. השתמשו בתמונה עד 40 מיליון פיקסלים.');
  const ratio=Math.min(1,1600/Math.max(bitmap.width,bitmap.height),Math.sqrt(1_920_000/(bitmap.width*bitmap.height)));
  const canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.floor(bitmap.width*ratio));canvas.height=Math.max(1,Math.floor(bitmap.height*ratio));
  const context=canvas.getContext('2d');if(!context)throw Error('לא ניתן להכין את התמונה בדפדפן זה.');
  context.fillStyle='#fff';context.fillRect(0,0,canvas.width,canvas.height);context.drawImage(bitmap,0,0,canvas.width,canvas.height);
  let blob:Blob|null=null;
  for(const quality of [.85,.7,.55,.4]){blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',quality));if(blob&&blob.size<=512*1024)break}
  if(!blob||blob.size>512*1024)throw Error('לא ניתן לצמצם את התמונה ל־512KB. נסו תמונה קטנה יותר.');
  return new File([blob],'vehicle.jpg',{type:'image/jpeg'});
 }finally{bitmap.close()}
}

