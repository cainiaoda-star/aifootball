import urllib.request, gzip, re, time

def fetch(url):
    req = urllib.request.Request(url, headers={
        "Accept-Language": "zh-CN,zh;q=0.9",
        "Referer": "https://trade.500.com/jczq/",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept-Encoding": "gzip"
    })
    resp = urllib.request.urlopen(req, timeout=30)
    raw = resp.read()
    if resp.headers.get('Content-Encoding') == 'gzip':
        raw = gzip.decompress(raw)
    return raw.decode('gbk', errors='ignore')

# 模拟连续4个请求
urls = [
    ("main", "https://trade.500.com/jczq/"),
    ("bf", "https://trade.500.com/jczq/?playid=271&g=2"),
    ("jq", "https://trade.500.com/jczq/?playid=270&g=2"),
    ("bq", "https://trade.500.com/jczq/?playid=272&g=2"),
]
for name, url in urls:
    try:
        html = fetch(url)
        trs = re.findall(r'<tr[^>]*data-matchnum[^>]*>', html)
        # 数 bf/jqs/bqc
        bf = len(re.findall(r'data-type=\"bf\"', html))
        jq = len(re.findall(r'data-type=\"jqs\"', html))
        bq = len(re.findall(r'data-type=\"bqc\"', html))
        nspf = len(re.findall(r'data-type=\"nspf\"', html))
        print(f"{name}: bytes={len(html)} 场次={len(trs)} bf={bf} jqs={jq} bqc={bq} nspf={nspf}")
    except Exception as e:
        print(f"{name}: ERR {e}")
    time.sleep(1)
