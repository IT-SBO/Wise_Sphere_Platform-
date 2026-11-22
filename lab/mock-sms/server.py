"""Tiny SMS provider stand-in for the lab (D-04 not yet decided).

POST /send  {"to": "...", "message": "..."}   -> 200, stored in memory
GET  /messages                                 -> list of received messages
DELETE /messages                               -> clear
Requires header X-SMS-Key to match $SMS_KEY (mirrors a real provider API key).
"""
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY = os.environ.get("SMS_KEY", "lab-sms-key")
MESSAGES: list[dict] = []


class H(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.startswith("/messages"):
            return self._send(200, MESSAGES)
        if self.path == "/health":
            return self._send(200, {"ok": True})
        return self._send(404, {"error": "not found"})

    def do_DELETE(self):
        MESSAGES.clear()
        return self._send(200, {"ok": True})

    def do_POST(self):
        if self.headers.get("X-SMS-Key") != KEY:
            return self._send(401, {"error": "bad key"})
        n = int(self.headers.get("Content-Length") or 0)
        try:
            msg = json.loads(self.rfile.read(n) or b"{}")
        except json.JSONDecodeError:
            return self._send(400, {"error": "invalid json"})
        MESSAGES.append(msg)
        print("SMS", json.dumps(msg), flush=True)
        return self._send(200, {"ok": True, "id": len(MESSAGES)})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8000), H).serve_forever()
