export class ApiError extends Error{
 constructor(message:string,public code?:string,public details?:string){super(message);this.name='ApiError'}
}
export async function api(path:string,body?:unknown){
 const headers:Record<string,string>={'Content-Type':'application/json'};
 const token=sessionStorage.getItem('carwise-access-token');if(token)headers.Authorization='Bearer '+token;
 const response=await fetch('/api'+path,{method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined});
 if(response.status===429)throw Error('בוצעו יותר מדי בקשות בזמן קצר. המתינו כדקה ונסו שוב.');
 const data=response.status===204?null:await response.json();
 if(!response.ok){if(response.status===401)window.dispatchEvent(new Event('carwise-auth-required'));throw Error(data?.error||'אירעה שגיאה')}
 return data;
}
export async function upload(path:string,file:File,signal?:AbortSignal){
 const headers:Record<string,string>={'Content-Type':'application/octet-stream'};
 const token=sessionStorage.getItem('carwise-access-token');if(token)headers.Authorization='Bearer '+token;
 const response=await fetch('/api'+path,{method:'POST',headers,body:file,signal});
 if(response.status===429)throw Error('בוצעו יותר מדי העלאות בזמן קצר. המתינו כדקה ונסו שוב.');
 const data=await response.json();if(!response.ok){if(response.status===401)window.dispatchEvent(new Event('carwise-auth-required'));throw new ApiError(data?.error||'העלאת הקובץ נכשלה',data?.code,data?.details)}return data;
}
export async function download(path:string,signal?:AbortSignal){
 const headers:Record<string,string>={};const token=sessionStorage.getItem('carwise-access-token');if(token)headers.Authorization='Bearer '+token;
 const response=await fetch('/api'+path,{headers,signal});
 if(!response.ok){if(response.status===401)window.dispatchEvent(new Event('carwise-auth-required'));const error=await response.json().catch(()=>null);throw Error(error?.error||'לא ניתן לטעון את התמונה כרגע.')}
 return response.blob();
}
