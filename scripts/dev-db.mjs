import {randomBytes} from 'node:crypto';
import {mkdirSync,existsSync,writeFileSync,readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
mkdirSync('.local',{recursive:true});
const envPath='.local/postgres.env';
if(!existsSync(envPath))writeFileSync(envPath,`POSTGRES_USER=carwise\nPOSTGRES_DB=carwise\nPOSTGRES_PASSWORD=${randomBytes(24).toString('hex')}\n`,{mode:0o600});
const command=(args,allowFailure=false)=>{const r=spawnSync('docker',args,{stdio:'inherit'});if(r.status&&!allowFailure)process.exit(r.status??1);return r.status};
const found=spawnSync('docker',['inspect','carwise-postgres'],{stdio:'ignore'}).status===0;
if(!found)command(['run','--detach','--name','carwise-postgres','--env-file',envPath,'--publish','127.0.0.1:55432:5432','--volume','carwise-postgres-data:/var/lib/postgresql/data','postgres:17.6']);
else command(['start','carwise-postgres']);
const password=readFileSync(envPath,'utf8').split('\n').find(l=>l.startsWith('POSTGRES_PASSWORD=')).split('=')[1];
const configPath='.local/appsettings.Development.json';
if(!existsSync(configPath))writeFileSync(configPath,JSON.stringify({ConnectionStrings:{CarWise:`Host=127.0.0.1;Port=55432;Database=carwise;Username=carwise;Password=${password}`}},null,2),{mode:0o600});
for(let i=0;i<60;i++){if(spawnSync('docker',['exec','carwise-postgres','pg_isready','-U','carwise','-d','carwise'],{stdio:'ignore'}).status===0){console.log('PostgreSQL ready on loopback port 55432. Local credentials remain in ignored .local files.');process.exit(0)}await new Promise(r=>setTimeout(r,500));}
throw Error('PostgreSQL did not become ready');
