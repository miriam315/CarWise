export async function api(path:string,body?:unknown){
 const headers:Record<string,string>={'Content-Type':'application/json'};
 const token=sessionStorage.getItem('carwise-access-token');if(token)headers.Authorization='Bearer '+token;
 const response=await fetch('/api'+path,{method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined});
 const data=response.status===204?null:await response.json();
 if(!response.ok){if(response.status===401)window.dispatchEvent(new Event('carwise-auth-required'));throw Error(data?.error||'אירעה שגיאה')}
 return data;
}
