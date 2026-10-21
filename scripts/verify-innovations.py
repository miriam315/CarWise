"""Isolated PostgreSQL + actual browser acceptance. Never touches an existing user's DB."""
from pathlib import Path
import socket,sys,os,subprocess,json,re,uuid,time,signal,shutil,urllib.request,urllib.error,importlib.machinery
from playwright.sync_api import sync_playwright
ROOT=Path(__file__).resolve().parent.parent
with socket.socket() as binding:
 binding.bind(('127.0.0.1',0));test_port=binding.getsockname()[1]
BASE=f'http://127.0.0.1:{test_port}'
def query(path,body=None):
 payload=None if body is None else json.dumps(body).encode()
 with urllib.request.urlopen(urllib.request.Request(BASE+'/api'+path,data=payload,headers={'Content-Type':'application/json'}),timeout=130) as response:return json.load(response)
def stop(process):
 if process.poll() is None:os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=15)
process=None;dbname='carwise_innovation_'+uuid.uuid4().hex[:12]
subprocess.run(['docker','exec','carwise-postgres','createdb','-U','carwise',dbname],check=True)
try:
 config=json.loads((ROOT/'.local/appsettings.Development.json').read_text());connection=re.sub(r'Database=[^;]+','Database='+dbname,config['ConnectionStrings']['CarWise'])
 process=subprocess.Popen(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build'],cwd=ROOT,stdout=(ROOT/'.local/innovation-server.log').open('w'),stderr=subprocess.STDOUT,start_new_session=True,
  env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Development','CarWise__DemoMode':'true','CARWISE_IGNORE_SAVED_CONNECTION':'true','ASPNETCORE_URLS':BASE,'ConnectionStrings__CarWise':connection,'SUPABASE_URL':'','CARWISE_SUPABASE_KEY':'','CARWISE_AI_API_KEY':''})
 for _ in range(600):
  try:query('/health');break
  except Exception:
   if process.poll() is not None:raise RuntimeError('Test server stopped; see .local/innovation-server.log')
   time.sleep(.1)
 else:raise RuntimeError('Test server did not become ready')
 with sync_playwright() as p:
  browser=p.chromium.launch(headless=True,args=['--no-sandbox'],executable_path=shutil.which('chromium'))
  page=browser.new_page(viewport={'width':1440,'height':1100});page.goto(BASE);page.wait_for_selector('.vehicle-card')
  def nav(label):page.locator('.sidebar nav').get_by_role('button',name=label,exact=False).click()
  screenshots=ROOT/'.local/screenshots';screenshots.mkdir(exist_ok=True)
  module=importlib.machinery.SourceFileLoader('innovation_checks',str(ROOT/'scripts/innovation-checks.py')).load_module()
  module.check(page,query,nav,screenshots,all_browser_files='--quick' not in sys.argv)
  browser.close()
finally:
 if process:stop(process)
 if not re.fullmatch('carwise_innovation_[a-f0-9]{12}',dbname):raise RuntimeError('Unexpected test database name')
 subprocess.run(['docker','exec','carwise-postgres','dropdb','-U','carwise',dbname],check=True)
