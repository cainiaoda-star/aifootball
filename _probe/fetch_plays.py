import urllib.request, gzip

def fetch(url, name):
    req = urllib.request.Request(url, headers={
        "Accept-Language": "zh-CN,zh;q=0.9",
        "Referer": "https://trade.500.com/jczq/",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept-Encoding": "gzip"
    })
    try:
        resp = urllib.request.urlopen(req, timeout=30)
        raw = resp.read()
        if resp.headers.get('Content-Encoding') == 'gzip':
            raw = gzip.decompress(raw)
        html = raw.decode('gbk', errors='ignore')
        open(f'_probe/{name}.html', 'w', encoding='utf-8').write(html)
        print(name, "OK bytes=", len(html))
    except Exception as e:
        print(name, "ERR", e)

fetch("https://trade.500.com/jczq/?playid=272&g=2", "jc_bf")   # 比分
fetch("https://trade.500.com/jczq/?playid=269&g=2", "jc_jq")   # 总进球
fetch("https://trade.500.com/jczq/?playid=270&g=2", "jc_bq")   # 半全场
