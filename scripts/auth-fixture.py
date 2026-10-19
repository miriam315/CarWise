"""In-memory GoTrue API fixture. Never used by the application or real Supabase."""
import json,uuid,datetime,threading,urllib.parse
class AuthFixture:
 def __init__(self,service_key):self.service_key=service_key;self.users={};self.tokens={};self.lock=threading.Lock()
 def handle(self,handler,body):
  if not handler.path.startswith('/auth/v1/'):return False
  def send(status,value):
   raw=json.dumps(value).encode();handler.send_response(status);handler.send_header('Content-Type','application/json');handler.send_header('Content-Length',str(len(raw)));handler.end_headers();handler.wfile.write(raw)
  def public(user):return {k:v for k,v in user.items() if not k.startswith('_')}
  data=json.loads(body or b'{}');path=urllib.parse.urlparse(handler.path).path
  if handler.headers.get('apikey')!=self.service_key:send(401,{'error':'invalid_api_key'});return True
  with self.lock:
   if path=='/auth/v1/token':
    user=next((u for u in self.users.values() if u['email']==data.get('email') and u['_password']==data.get('password')),None)
    if not user:send(400,{'error':'invalid_credentials'});return True
    token=uuid.uuid4().hex+uuid.uuid4().hex;self.tokens[token]=user['id'];send(200,{'access_token':token,'expires_in':3600});return True
   if path=='/auth/v1/user':
    token=handler.headers.get('Authorization','').removeprefix('Bearer ');user=self.users.get(self.tokens.get(token))
    send(200 if user else 401,public(user) if user else {'error':'invalid_user'});return True
   if not handler.headers.get('Authorization')=='Bearer '+self.service_key:send(403,{'error':'admin_only'});return True
   if path=='/auth/v1/admin/users' and handler.command=='GET':send(200,{'users':[public(u) for u in self.users.values()]});return True
   if path=='/auth/v1/admin/users' and handler.command=='POST':
    user={'id':str(uuid.uuid4()),'email':data['email'],'_password':data['password'],'app_metadata':data.get('app_metadata',{}),'created_at':datetime.datetime.now(datetime.timezone.utc).isoformat()};self.users[user['id']]=user;send(200,public(user));return True
   if path.startswith('/auth/v1/admin/users/') and handler.command=='PUT':
    user=self.users.get(path.rsplit('/',1)[1])
    if not user:send(404,{'error':'not_found'});return True
    user['_password']=data['password'];send(200,public(user));return True
   send(404,{'error':'not_found'});return True
