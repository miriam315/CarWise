/** Build a new image-only PDF. No original PDF objects, text, filenames or metadata are copied. */
export function rasterPdf(pages:{bytes:Uint8Array;width:number;height:number}[]):Uint8Array{
 if(!pages.length||pages.length>6)throw Error('אפשר לעבד 1–6 עמודים.');
 const encoder=new TextEncoder(),parts:Uint8Array[]=[],offsets=[0];let size=0;
 const append=(part:string|Uint8Array)=>{const bytes=typeof part==='string'?encoder.encode(part):part;parts.push(bytes);size+=bytes.length};
 const object=(id:number,body:string)=>{offsets[id]=size;append(`${id} 0 obj\n${body}\nendobj\n`)};
 append('%PDF-1.4\n%Raster document\n');object(1,'<< /Type /Catalog /Pages 2 0 R >>');
 object(2,`<< /Type /Pages /Count ${pages.length} /Kids [${pages.map((_,i)=>`${3+i*3} 0 R`).join(' ')}] >>`);
 pages.forEach((page,i)=>{
  if(!Number.isInteger(page.width)||!Number.isInteger(page.height)||page.width<1||page.height<1||page.width*page.height>20_000_000)throw Error('ממדי עמוד לא תקינים.');
  const id=3+i*3,width=612,height=Math.round(width*page.height/page.width*100)/100;
  object(id,`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${width} ${height}] /Resources << /XObject << /Image ${id+1} 0 R >> >> /Contents ${id+2} 0 R >>`);
  offsets[id+1]=size;append(`${id+1} 0 obj\n<< /Type /XObject /Subtype /Image /Width ${page.width} /Height ${page.height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${page.bytes.length} >>\nstream\n`);append(page.bytes);append('\nendstream\nendobj\n');
  const commands=`q ${width} 0 0 ${height} 0 0 cm /Image Do Q\n`;
  object(id+2,`<< /Length ${encoder.encode(commands).length} >>\nstream\n${commands}endstream`);
 });
 const xref=size,count=3+pages.length*3;append(`xref\n0 ${count}\n0000000000 65535 f \n`);
 for(let i=1;i<count;i++)append(String(offsets[i]).padStart(10,'0')+' 00000 n \n');
 append(`trailer\n<< /Size ${count} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
 const result=new Uint8Array(size);let position=0;for(const part of parts){result.set(part,position);position+=part.length}return result;
}
