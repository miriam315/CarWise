"""Reproduce an official-data HTTP timeout using an isolated local server; no hosted project access."""
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from threading import Thread
import os,json,subprocess,tempfile,time,urllib.request,signal
ROOT=Path(__file__).resolve().parent.parent
calls=0
class Gateway(BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def do_GET(self):
  global calls
  if '/carwise_official?' in self.path:
   calls+=1;time.sleep(2);body=b'[]'
  elif '/carwise_rules?' in self.path:body=b'[{"id":"active","document":{}}]'
  else:body=b'[]'
  try:
   self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
  except (BrokenPipeError,ConnectionResetError):pass
proxy=ThreadingHTTPServer(('127.0.0.1',0),Gateway);Thread(target=proxy.serve_forever,daemon=True).start()
process=None
try:
 with tempfile.TemporaryDirectory(prefix='startup-check-') as temp:
  settings=Path(temp)/'connection.json';key='sb_secret_TEST_ONLY_STARTUP_TIMEOUT'
  env={**os.environ,'SUPABASE_URL':f'http://127.0.0.1:{proxy.server_port}','CARWISE_SUPABASE_KEY':key,'ConnectionStrings__CarWise':'','CARWISE_IGNORE_SAVED_CONNECTION':'true',
       'CARWISE_SERVER_SETTINGS_PATH':str(settings),'CarWise__DemoMode':'false','ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':'http://127.0.0.1:8015','CarWise__SupabaseRequestTimeoutSeconds':'1','CarWise__StartupTimeoutSeconds':'30'}
  with (Path(temp)/'startup.log').open('w+') as log:
   process=subprocess.Popen(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build'],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   for _ in range(200):
    try:
     with urllib.request.urlopen('http://127.0.0.1:8015/api/runtime',timeout=1) as response:runtime=json.load(response)
     break
    except Exception:
     if process.poll() is not None:raise AssertionError('Startup crashed instead of providing recovery')
     time.sleep(.1)
   else:raise AssertionError('Startup recovery did not become available')
   assert runtime['requiresSetup'] and runtime['setupReason'];assert calls==2,calls
   with urllib.request.urlopen('http://127.0.0.1:8015/api/setup/connection',timeout=2) as response:view=json.load(response)
   assert view['hasKey'] and view['source']=='environment';assert key not in json.dumps(view) and not settings.exists()
   log.flush();log.seek(0);output=log.read();assert 'timed out' in output and 'Unhandled exception' not in output and key not in output
   print('PASS: official-record HTTP timeout retries once, returns a local recovery UI, retains the configured key and never prints it or crashes.')
finally:
 if process and process.poll() is None:os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)
 proxy.shutdown();proxy.server_close()
