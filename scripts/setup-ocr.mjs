// Build a local isolated worker. No document is uploaded and no image is pulled during OCR.
import {spawnSync} from 'node:child_process';
import {mkdir,readFile,writeFile,rm} from 'node:fs/promises';
import {createReadStream} from 'node:fs';
import {resolve,join} from 'node:path';
import {createHash} from 'node:crypto';
import {checkOcrDocker} from './ocr-docker.mjs';
const root=resolve(import.meta.dirname,'..'),context=join(root,'.local','ocr-worker'),cache=join(root,'.local','ocr-downloads');
function run(command,args,capture=false){const result=spawnSync(command,args,{cwd:root,encoding:'utf8',stdio:capture?'pipe':'inherit'});if(result.error||result.status!==0){if(capture&&result.stderr)process.stderr.write(result.stderr);throw Error(`${command} failed (exit ${result.status??'unavailable'}). Check the command output above, installation and network access, then retry npm run ocr:setup.`);}return capture?result.stdout.trim():'';}
let architecture;
try{architecture=checkOcrDocker();}catch(error){console.error(error.message);process.exit(1);}
const rid=architecture==='x86_64'?'linux-x64':architecture==='aarch64'?'linux-arm64':null;
if(!rid)throw Error('Supported OCR architectures: Linux x64 and ARM64.');
await mkdir(context,{recursive:true,mode:0o700});await mkdir(cache,{recursive:true,mode:0o700});
const metadataPath=join(cache,'releases-10.json');
run('curl',['--fail','--location','--silent','--show-error','--max-time','60','https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json','--output',metadataPath]);
const metadata=JSON.parse(await readFile(metadataPath,'utf8'));
const release=metadata.releases.find(release=>release['aspnetcore-runtime']?.files?.some(file=>file.name===`aspnetcore-runtime-${rid}.tar.gz`));
const artifact=release?.['aspnetcore-runtime'].files.find(file=>file.name===`aspnetcore-runtime-${rid}.tar.gz`);
if(!artifact?.url?.startsWith('https://builds.dotnet.microsoft.com/')||!/^[a-fA-F0-9]{128}$/.test(artifact.hash))throw Error('Unexpected official runtime artifact.');
const archive=join(cache,`aspnetcore-${release['release-version']}-${rid}.tar.gz`);
async function valid(){try{const hash=createHash('sha512');for await(const chunk of createReadStream(archive))hash.update(chunk);return hash.digest('hex').toLowerCase()===artifact.hash.toLowerCase()}catch{return false}}
if(!await valid()){
 console.log('Downloading the official Linux .NET runtime...');
 run('curl',['--fail','--location','--silent','--show-error','--max-time','60',artifact.url,'--output',archive]);
 if(!await valid()){await rm(archive,{force:true});throw Error('Official runtime SHA-512 mismatch; refusing to build.');}
}
await rm(join(context,'runtime'),{recursive:true,force:true});await mkdir(join(context,'runtime'),{recursive:true});
run('tar',['-xzf',archive,'-C',join(context,'runtime')]);
run(process.execPath,['scripts/dotnet.mjs','publish','backend/CarWise.Ocr.Worker','-c','Release','-o',join(context,'publish'),'--nologo']);
await writeFile(join(context,'.dockerignore'),'*\n!publish/\n!publish/**\n!runtime/\n!runtime/**\n');
run('docker',['pull','ubuntu:24.04']);
const base=JSON.parse(run('docker',['image','inspect','ubuntu:24.04','--format','{{json .RepoDigests}}'],true)).find(value=>/^ubuntu@sha256:[a-f0-9]{64}$/.test(value));
if(!base)throw Error('Cannot pin the approved base image to a digest.');
run('docker',['build','--no-cache','--file',join(root,'deploy','ocr','Dockerfile'),'--build-arg',`OCR_BASE_IMAGE=${base}`,'--tag','carwise-ocr:1',context]);
const image=run('docker',['image','inspect','carwise-ocr:1','--format','{{.Id}}'],true);
await writeFile(join(context,'build-info.json'),JSON.stringify({image,base,runtime:release['release-version'],runtimeSha512:artifact.hash,rid},null,2)+'\n',{mode:0o600});
console.log('Isolated OCR image ready:',image);console.log('Restart CarWise. No host Tesseract installation is required. See docs/local-payslip-ocr.md.');
