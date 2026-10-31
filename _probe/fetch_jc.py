import urllib.request, gzip, io

req = urllib.request.Request("https://trade.500.com/jczq/", headers={
    "Accept-Language": "zh-CN,zh;q=0.9",
    "Referer": "https://trade.500.com/",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
    "Accept-Encoding": "gzip"
})
try:
    resp = urllib.request.urlopen(req, timeout=30)
    raw = resp.read()
    if resp.headers.get('Content-Encoding') == 'gzip':
        raw = gzip.decompress(raw)
    html = raw.decode('gbk', errors='ignore')
    open('_probe/jc.html', 'w', encoding='utf-8').write(html)
    print("OK bytes=", len(html))
except Exception as e:
    print("ERR", e)
