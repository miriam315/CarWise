// Cloud onboarding only. Desktop users install the .NET 10 SDK normally.
import {spawnSync} from 'node:child_process';
import {existsSync,mkdirSync,mkdtempSync,readFileSync,createReadStream,rmSync} from 'node:fs';
import {tmpdir,homedir} from 'node:os';
import {join} from 'node:path';
import {createHash} from 'node:crypto';
for(const directory of ['.dotnet','.nuget','.local/share/NuGet'])mkdirSync(join(homedir(),directory),{recursive:true});
const installed=existsSync('/workspace/.dotnet/dotnet')?'/workspace/.dotnet/dotnet':'dotnet';
const check=spawnSync(installed,['--version'],{encoding:'utf8'});
if(check.status===0 && check.stdout.trim().startsWith('10.')){console.log('.NET 10 SDK already available.');process.exit(0)}
if(process.platform!=='linux'||process.arch!=='x64')throw Error('Install the .NET 10 SDK for this platform.');
const temp=mkdtempSync(join(tmpdir(),'carwise-sdk-'));
const run=(command,args)=>{const r=spawnSync(command,args,{stdio:'inherit'});if(r.status!==0)throw Error(command+' failed');};
try{
 const metadata=join(temp,'releases.json');
 run('curl',['--fail','--location','--silent','--show-error','https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json','--output',metadata]);
 const releases=JSON.parse(readFileSync(metadata,'utf8'));const sdk=releases.releases[0].sdk;
 const artifact=sdk.files.find(f=>f.rid==='linux-x64'&&f.name.endsWith('.tar.gz'));
 if(!artifact||!artifact.url.startsWith('https://builds.dotnet.microsoft.com/'))throw Error('Unexpected SDK source');
 const archive=join(temp,'sdk.tar.gz');
 run('curl',['--fail','--location','--silent','--show-error',artifact.url,'--output',archive]);
 const hash=createHash('sha512');for await(const chunk of createReadStream(archive))hash.update(chunk);
 if(hash.digest('hex').toLowerCase()!==artifact.hash.toLowerCase())throw Error('Official SDK SHA-512 verification failed');
 mkdirSync('/workspace/.dotnet',{recursive:true});
 run('tar',['-xzf',archive,'-C','/workspace/.dotnet']);
 console.log('Installed SHA-512 verified .NET SDK',sdk.version);
}finally{rmSync(temp,{recursive:true,force:true})}
