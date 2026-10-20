import {spawnSync} from 'node:child_process';

// Fail before downloads or tests, and preserve the Docker error that explains
// whether the CLI, engine, Linux mode or locally built worker is missing.
export function checkOcrDocker({requireImage=false,spawn=spawnSync}={}) {
 const invoke=args=>spawn('docker',args,{encoding:'utf8',timeout:30000,maxBuffer:1024*1024});
 const result=invoke(['info','--format','{{.OSType}}|{{.Architecture}}']);
 if(result.error?.code==='ENOENT')throw Error('Docker was not found. Install Docker Desktop, open it, then open a new CMD window. On Windows: winget install --id Docker.DockerDesktop --exact --source winget');
 if(result.error||result.status!==0)throw Error([
  'The Docker engine is not available. Open Docker Desktop and wait until the engine is running. On Windows, use Linux containers and complete any requested WSL 2 setup.',
  'Check in CMD: docker info',
  String(result.stderr||result.error?.message||`Docker exited with code ${result.status}`).trim()
 ].join('\n'));
 const [os,architecture]=result.stdout.trim().split('|');
 if(os!=='linux')throw Error('Docker is running Windows containers. In Docker Desktop choose "Switch to Linux containers", wait for the engine, then retry.');
 if(requireImage){
  const image=invoke(['image','inspect','carwise-ocr:1','--format','{{.Id}}']);
  if(image.error||image.status!==0)throw Error('The local OCR worker image is not available. Run npm run ocr:setup successfully before npm run ocr:verify.\n'+String(image.stderr||image.error?.message||'').trim());
 }
 return architecture;
}
