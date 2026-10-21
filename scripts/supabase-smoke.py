"""Exercise the real Supabase REST adapter against isolated PostgreSQL + PostgREST.
This does not replace a live hosted Supabase/Auth check, which needs project credentials.
"""
from pathlib import Path
import json,os,subprocess,tempfile,uuid,time,urllib.request,urllib.error,re,signal,hmac,hashlib,base64
ROOT=Path(__file__).resolve().parent.parent
name='carwise_rest_'+uuid.uuid4().hex[:10]
container='carwise-postgrest-'+uuid.uuid4().hex[:10]
base='http://127.0.0.1:8003'
process=None
proxy=None
run=lambda args,**kw:subprocess.run(args,cwd=ROOT,check=True,**kw)
def query(path,body=None):
 data=None if body is None else json.dumps(body).encode()
 with urllib.request.urlopen(urllib.request.Request(base+'/api'+path,data=data,headers={'Content-Type':'application/json'}),timeout=10) as response:return json.load(response)
def upload_api(path,content):
 with urllib.request.urlopen(urllib.request.Request(base+'/api'+path,data=content,headers={'Content-Type':'application/octet-stream'}),timeout=20) as response:return json.load(response)
def token(role,key):
 enc=lambda value:base64.urlsafe_b64encode(json.dumps(value,separators=(',',':')).encode()).decode().rstrip('=')
 unsigned=enc({'alg':'HS256','typ':'JWT'})+'.'+enc({'role':role,'exp':int(time.time())+1800})
 return unsigned+'.'+base64.urlsafe_b64encode(hmac.new(key.encode(),unsigned.encode(),hashlib.sha256).digest()).decode().rstrip('=')
try:
 run(['docker','exec','carwise-postgres','createdb','-U','carwise',name])
 run(['docker','exec','carwise-postgres','psql','-U','carwise','-d',name,'-v','ON_ERROR_STOP=1','-c',"DO $$ BEGIN IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='service_role') THEN CREATE ROLE service_role NOLOGIN BYPASSRLS; END IF; IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='anon') THEN CREATE ROLE anon NOLOGIN NOBYPASSRLS; END IF; END $$;"],stdout=subprocess.DEVNULL)
 config=json.loads((ROOT/'.local/appsettings.Development.json').read_text())
 connection=re.sub(r'Database=[^;]+',f'Database={name}',config['ConnectionStrings']['CarWise'])
 env={**os.environ,'ConnectionStrings__CarWise':connection,'ASPNETCORE_ENVIRONMENT':'Development','CarWise__DemoMode':'true','CARWISE_IGNORE_SAVED_CONNECTION':'true','SUPABASE_URL':'','CARWISE_SUPABASE_KEY':'','CARWISE_AI_API_KEY':''}
 run(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build','--','--import-data'],env=env,stdout=subprocess.DEVNULL)
 if subprocess.run(['docker','network','inspect','carwise-dev'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode:run(['docker','network','create','carwise-dev'],stdout=subprocess.DEVNULL)
 networks=json.loads(subprocess.check_output(['docker','inspect','--format','{{json .NetworkSettings.Networks}}','carwise-postgres']))
 if 'carwise-dev' not in networks:run(['docker','network','connect','carwise-dev','carwise-postgres'])
 password=(ROOT/'.local/postgres.env').read_text().split('POSTGRES_PASSWORD=')[1].splitlines()[0]
 secret=uuid.uuid4().hex+uuid.uuid4().hex
 with tempfile.TemporaryDirectory(prefix='rest-test-',dir=ROOT/'.local') as temp:
  file=Path(temp)/'postgrest.env';file.write_text(f'PGRST_DB_URI=postgres://carwise:{password}@carwise-postgres:5432/{name}\nPGRST_DB_SCHEMAS=public\nPGRST_JWT_SECRET={secret}\n');file.chmod(0o600)
  run(['docker','run','--detach','--rm','--name',container,'--network','carwise-dev','--env-file',str(file),'--publish','127.0.0.1:55433:3000','postgrest/postgrest:v12.2.3'],stdout=subprocess.DEVNULL)
  service_token=token('service_role',secret)
  for _ in range(100):
   try:
    with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:55433/carwise_rules',headers={'Authorization':'Bearer '+service_token}),timeout=2) as r:assert r.status==200
    break
   except Exception:time.sleep(.2)
  else:raise RuntimeError('PostgREST readiness failed')
  # Anonymous browser role has no direct access to payroll/reference tables.
  try:
   urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:55433/carwise_employees',headers={'Authorization':'Bearer '+token('anon',secret)}))
   raise AssertionError('Anonymous access must be denied')
  except urllib.error.HTTPError as error:assert error.code in (401,403)
  # Supabase's gateway mounts PostgREST under /rest/v1; emulate that prefix locally.
  from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
  from threading import Thread
  from importlib.machinery import SourceFileLoader
  auth_fixture=SourceFileLoader('auth_fixture',str(ROOT/'scripts/auth-fixture.py')).load_module().AuthFixture(service_token)
  class Gateway(BaseHTTPRequestHandler):
   def end_headers(self):
    # This test gateway serves one request per connection. Tell pooled HTTP
    # clients explicitly; never replay writes after an interrupted response.
    self.send_header('Connection','close');self.close_connection=True
    super().end_headers()
   def log_message(self,*args):pass
   def do_GET(self):self.forward()
   def do_POST(self):self.forward()
   def do_PATCH(self):self.forward()
   def do_PUT(self):self.forward()
   def forward(self):
    size=int(self.headers.get('Content-Length',0));body=self.rfile.read(size) if size else None
    if self.headers.get('Transfer-Encoding','').lower()=='chunked':
     chunks=[]
     while True:
      length=int(self.rfile.readline().strip().split(b';')[0],16)
      if length==0:self.rfile.readline();break
      chunks.append(self.rfile.read(length));self.rfile.read(2)
     body=b''.join(chunks)
    if auth_fixture.handle(self,body):return
    if not self.path.startswith('/rest/v1/'):
     self.send_error(404);return
    headers={k:v for k,v in self.headers.items() if k.lower() in ('authorization','apikey','content-type','prefer')}
    request=urllib.request.Request('http://127.0.0.1:55433/'+self.path[len('/rest/v1/'):],data=body,headers=headers,method=self.command)
    try:response=urllib.request.urlopen(request)
    except urllib.error.HTTPError as error:response=error
    with response:
     content=response.read();
     if response.code>=400:
      failure=json.loads(content);print('PostgREST diagnostic:',response.code,failure.get('code'),failure.get('message'))
     self.send_response(response.code);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(content)));
     if response.headers.get('Content-Range'):self.send_header('Content-Range',response.headers['Content-Range'])
     self.end_headers();self.wfile.write(content)
  class FixtureServer(ThreadingHTTPServer):
   request_queue_size=128
  proxy=FixtureServer(('127.0.0.1',55434),Gateway);Thread(target=proxy.serve_forever,daemon=True).start()
  env.update({'SUPABASE_URL':'http://127.0.0.1:55434' ,'CARWISE_SUPABASE_KEY':service_token,'ASPNETCORE_URLS':base})
  with (ROOT/'.local/rest-smoke-server.log').open('w') as log:
   process=subprocess.Popen(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build'],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   for _ in range(600):
    try:query('/health');break
    except Exception:
     if process.poll() is not None:raise RuntimeError('Supabase adapter server exited; inspect .local/rest-smoke-server.log')
     time.sleep(.1)
   else:raise RuntimeError('Supabase adapter startup timed out')
   assert query('/runtime')['database']=='Supabase/PostgreSQL'
   from importlib.machinery import SourceFileLoader
   SourceFileLoader('admin_checks',str(ROOT/'scripts/admin-checks.py')).load_module().check_admin(query,name,upload_api)
   SourceFileLoader('challenge_checks_rest',str(ROOT/'scripts/challenge-flow-checks.py')).load_module().check(query,name)
   SourceFileLoader('decision_api_checks_rest',str(ROOT/'scripts/decision-api-checks.py')).load_module().check(query)
   data=query('/data');assert data['officialCount']==77988
   e=data['employees'][0];r=query('/simulate',{'employee':e});a=next(r for r in r['results'] if r['id']=='CAR-10');assert round(a['net'])==11693
   ticket=query('/tickets',{'employee':e,'target':'CAR-10','question':'בדיקת ההשתתפות דרך Supabase adapter'})
   assert query('/conversation/E-001')['status']=='PendingAgentResponse'
   query('/tickets/'+ticket['id'],{'answer':'השתתפות מאושרת דרך REST','participation':350})
   r=query('/simulate',{'employee':e});assert next(r for r in r['results'] if r['id']=='CAR-10')['participation']==350
   assert query('/conversation/E-001')['status']=='Active'
   assert any('הנציג ענה' in m['text'] for m in query('/history/E-001'))
   SourceFileLoader('fleet_checks',str(ROOT/'scripts/fleet-checks.py')).load_module().check(query)
   print('PASS: real Supabase/PostgREST adapter, 77,988 records, reference data, simulation, transactional agent RPC, conversation resume, anonymous RLS denial.')
   SourceFileLoader('ai_registry_checks',str(ROOT/'scripts/ai-model-checks.py')).load_module().check_api(query)
   os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)
   settings=Path(temp)/'server-settings'/'connection.json'
   env.update({'SUPABASE_URL':'','CARWISE_SUPABASE_KEY':'','ConnectionStrings__CarWise':'','CarWise__DemoMode':'false','CARWISE_IGNORE_SAVED_CONNECTION':'false','CARWISE_SERVER_SETTINGS_PATH':str(settings)})
   process=subprocess.Popen(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build'],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   for _ in range(600):
    try:
     if query('/runtime').get('requiresSetup'):break
    except Exception:pass
    time.sleep(.1)
   else:raise RuntimeError('First-run setup server did not start')
   SourceFileLoader('auth_connection_checks',str(ROOT/'scripts/auth-connection-checks.py')).load_module().check(base,query,settings,service_token)
   assert 'SUPABASE_URL' in env and not env['SUPABASE_URL'] and not env['CARWISE_SUPABASE_KEY']
   assert service_token not in (ROOT/'.local/rest-smoke-server.log').read_text()

finally:
 if proxy:proxy.shutdown()
 if process and process.poll() is None:os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)
 subprocess.run(['docker','rm','--force',container],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 subprocess.run(['docker','exec','carwise-postgres','dropdb','-U','carwise','--force',name],stdout=subprocess.DEVNULL)
