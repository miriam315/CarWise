"""Real browser/API regression against disposable databases, never the configured DB.

Requires Linux, Docker (carwise-postgres), Python Playwright and a built application.
CARWISE_TEST_CONNECTION overrides the ignored local development connection.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / '.local' / 'test-results'
OUTPUT.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--suite', action='append', choices=['fleet', 'intent', 'innovations', 'car-media', 'judge-demo', 'payslip', 'workspace', 'ai-model', 'employee-setup', 'participation'])
parser.add_argument('--quick', action='store_true', help='OCR: test 2 documents instead of all 12')
parser.add_argument('--video', action='store_true', help='Record judge rehearsal; requires Playwright ffmpeg')
args = parser.parse_args()
connection = os.environ.get('CARWISE_TEST_CONNECTION')
if not connection:
    settings = ROOT / '.local' / 'appsettings.Development.json'
    if settings.exists():
        connection = json.loads(settings.read_text()).get('ConnectionStrings', {}).get('CarWise')
if not connection or not re.search(r'(?i)(?:^|;)\s*Database\s*=\s*[^;]+', connection):
    raise SystemExit('Set CARWISE_TEST_CONNECTION to a local test PostgreSQL connection with Database=... .')
container = os.environ.get('CARWISE_TEST_DB_CONTAINER', 'carwise-postgres')
db_user = os.environ.get('CARWISE_TEST_DB_USER', 'carwise')
results = []


def load_suite(name):
    filename = 'innovation-checks.py' if name == 'innovations' else 'ai-model-checks.py' if name == 'ai-model' else name + '-checks.py'
    spec = importlib.util.spec_from_file_location(name + '_checks', ROOT / 'scripts' / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


for suite in args.suite or ['fleet', 'intent', 'innovations', 'car-media', 'judge-demo', 'payslip', 'workspace', 'ai-model', 'employee-setup', 'participation']:
    dbname = 'carwise_e2e_' + uuid.uuid4().hex
    test_connection = re.sub(r'(?i)((?:^|;)\s*Database\s*=\s*)[^;]+', lambda m: m[1] + dbname, connection)
    subprocess.run(['docker', 'exec', container, 'createdb', '-U', db_user, dbname], check=True)
    process = None
    log = (OUTPUT / (suite + '-server.log')).open('w')
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='carwise-e2e-') as private:
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        base = f'http://127.0.0.1:{port}'

        def query(path, body=None):
            payload = None if body is None else json.dumps(body).encode()
            request = urllib.request.Request(base + '/api' + path, data=payload, headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=130) as response:
                return json.load(response)

        def stop():
            if process and process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                try:
                    with socket.create_connection(('127.0.0.1', port), timeout=.2):
                        pass
                except OSError:
                    return
                time.sleep(.1)
            raise RuntimeError('Test server did not release its port')

        def launch():
            env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'CarWise__DemoMode': 'true',
                   'CARWISE_IGNORE_SAVED_CONNECTION': 'true', 'ASPNETCORE_URLS': base,
                   'ConnectionStrings__CarWise': test_connection, 'SUPABASE_URL': '',
                   'CARWISE_SUPABASE_KEY': '', 'CARWISE_AI_API_KEY': '',
                   'CARWISE_KEYRING_DIR': str(Path(private) / 'keys'),
                   'CARWISE_SERVER_SETTINGS_PATH': str(Path(private) / 'connection.json')}
            child = subprocess.Popen(['node', 'scripts/dotnet.mjs', 'run', '--project', 'backend/CarWise.Api',
                                      '--no-launch-profile', '--no-build'], cwd=ROOT, env=env,
                                     stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                deadline = time.monotonic() + 90
                while time.monotonic() < deadline:
                    if child.poll() is not None:
                        raise RuntimeError('Server stopped; inspect ' + str(log.name))
                    try:
                        query('/health')
                        return child
                    except (urllib.error.URLError, TimeoutError, ConnectionError):
                        time.sleep(.15)
                raise RuntimeError('Test server startup timed out')
            except BaseException:
                if child.poll() is None:
                    os.killpg(child.pid, signal.SIGKILL)
                    child.wait(timeout=5)
                raise

        try:
            process = launch()
            with sync_playwright() as playwright:
                executable = shutil.which('chromium')
                browser = playwright.chromium.launch(headless=True, args=['--no-sandbox'],
                                                      **({'executable_path': executable} if executable else {}))
                video={'record_video_dir':str(OUTPUT/'judge-video')} if suite=='judge-demo' and args.video else {}
                page = browser.new_page(viewport={'width': 1440, 'height': 1100},**video)
                page.goto(base)
                page.wait_for_selector('.vehicle-card')
                logo = page.get_by_role('img', name='שטראוס', exact=True)
                expect(logo).to_have_count(1)
                page.wait_for_function('document.querySelector(".strauss-logo img")?.naturalWidth===220')

                def nav(label):
                    page.locator('.sidebar nav').get_by_role('button', name=label, exact=False).click()

                module = load_suite(suite)
                try:
                    if suite == 'ai-model':
                        module.check(page, query, nav, OUTPUT)
                    elif suite == 'innovations':
                        module.check(page, query, nav, OUTPUT, all_browser_files=not args.quick)
                    else:
                        module.check(query, page, nav, OUTPUT)
                except BaseException:
                    page.screenshot(path=str(OUTPUT / (suite + '-failure.png')), full_page=True)
                    raise
                browser.close()
            if suite in ['fleet', 'intent', 'car-media']:
                path = '/fleet/officer' if suite == 'fleet' else '/data' if suite == 'car-media' else '/admin/intent/report'
                before = query(path)
                stop()
                process = launch()
                after = query(path)
                keys = ['revision', 'assignments', 'requests', 'proposals', 'audit'] if suite == 'fleet' else ['cars','employees'] if suite == 'car-media' else ['cohorts']
                assert all(before[key] == after[key] for key in keys), 'Saved state changed after restart'
                if suite == 'car-media':
                    photo=next(c for c in after['cars'] if c['id']=='CAR-01')['photos'][0]
                    with urllib.request.urlopen(base+'/media/cars/'+photo['id']+'.jpg') as response:
                        assert response.read().startswith(b'\xff\xd8')
            results.append({'suite': suite, 'passed': True, 'seconds': round(time.monotonic() - started, 2)})
            print('PASS:', suite, 'browser/API regression and persistence', flush=True)
        except BaseException as error:
            results.append({'suite': suite, 'passed': False, 'errorType': type(error).__name__})
            raise
        finally:
            try:
                stop()
            finally:
                log.close()
                assert re.fullmatch(r'carwise_e2e_[a-f0-9]{32}', dbname)
                subprocess.run(['docker', 'exec', container, 'dropdb', '-U', db_user, dbname], check=True)
                (OUTPUT / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
