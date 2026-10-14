import socketserver, threading, json, email
from email.header import decode_header, make_header
from http.server import HTTPServer, BaseHTTPRequestHandler
messages=[]
class SMTP(socketserver.StreamRequestHandler):
 def handle(self):
  self.wfile.write(b'220 local.test ESMTP\r\n')
  data=[]; reading=False
  for line in self.rfile:
   if reading:
    if line==b'.\r\n':
     m=email.message_from_bytes(b''.join(data)); body=''
     for part in m.walk():
      if part.get_content_type()=='text/plain': body+=(part.get_payload(decode=True) or b'').decode(part.get_content_charset() or 'utf-8')
     messages.append({'to':m.get('To',''),'from':m.get('From',''),'message_id':m.get('Message-Id',''),'subject':str(make_header(decode_header(m.get('Subject','')))),'body':body});data=[];reading=False;self.wfile.write(b'250 accepted\r\n')
    else:data.append(line)
   elif line.upper().startswith((b'EHLO',b'HELO')):self.wfile.write(b'250-local.test\r\n250 8BITMIME\r\n')
   elif line.upper().startswith(b'DATA'):reading=True;self.wfile.write(b'354 data\r\n')
   elif line.upper().startswith(b'QUIT'):self.wfile.write(b'221 bye\r\n');break
   else:self.wfile.write(b'250 ok\r\n')
class API(BaseHTTPRequestHandler):
 def do_GET(self):
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(json.dumps(messages).encode())
 def log_message(self,*args):pass
class ReusableSMTP(socketserver.ThreadingTCPServer):
 allow_reuse_address=True
server=ReusableSMTP(('0.0.0.0',2525),SMTP)
threading.Thread(target=server.serve_forever,daemon=True).start()
HTTPServer(('127.0.0.1',18025),API).serve_forever()
