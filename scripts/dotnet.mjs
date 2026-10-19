import {spawnSync} from 'node:child_process';
import {existsSync,mkdirSync} from 'node:fs';
import {resolve,join} from 'node:path';
const root=resolve(import.meta.dirname,'..');
const executable=process.env.CARWISE_DOTNET || (existsSync('/workspace/.dotnet/dotnet')?'/workspace/.dotnet/dotnet':'dotnet');
const env={...process.env,ASPNETCORE_ENVIRONMENT:process.env.ASPNETCORE_ENVIRONMENT||'Development',DOTNET_CLI_TELEMETRY_OPTOUT:'1'};
if(executable.startsWith('/workspace/')){
 env.DOTNET_CLI_HOME=env.DOTNET_CLI_HOME||'/workspace/.dotnet-home';env.NUGET_PACKAGES=env.NUGET_PACKAGES||'/workspace/.nuget/packages';
 mkdirSync(env.DOTNET_CLI_HOME,{recursive:true});
}
const result=spawnSync(executable,process.argv.slice(2),{cwd:root,env,stdio:'inherit'});
if(result.error){console.error('Install the .NET 10 SDK, or set CARWISE_DOTNET to the dotnet executable.');process.exit(1)}
process.exit(result.status??1);
