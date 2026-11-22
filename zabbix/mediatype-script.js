// WiseSphere media type (Zabbix webhook script) - INT-01, INT-05.
// Sends ws.event.v1 to the WiseSphere integration service with the shared-secret header.
// A non-2xx answer throws, so Zabbix retries (maxattempts 3, every 10 s) and the
// action log shows "Failed" with the reason. The service is idempotent (dedupe by event id).
var p = JSON.parse(value);
var req = new HttpRequest();
if (typeof p.HTTPProxy === 'string' && p.HTTPProxy.trim() !== '') {
    req.setProxy(p.HTTPProxy);
}
var url = p.url;
var secret = p.secret;
delete p.url;
delete p.secret;
delete p.HTTPProxy;
p.schema = 'ws.event.v1';
req.addHeader('Content-Type: application/json');
req.addHeader('X-WS-Secret: ' + secret);
var resp = req.post(url, JSON.stringify(p));
var code = req.getStatus();
Zabbix.log(4, '[WiseSphere] event ' + p.event_id + ' -> HTTP ' + code);
if (code < 200 || code >= 300) {
    throw 'integration service returned HTTP ' + code + ': ' + String(resp).substring(0, 300);
}
return resp;
