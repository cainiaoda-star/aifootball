import urllib.request, gzip, re

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
        trs = re.findall(r'<tr[^>]*data-matchnum[^>]*>.*?</tr>', html, re.S)
        if trs:
            btns = re.findall(r'data-type=\"([^\"]+)\"[^>]*data-value=\"([^\"]+)\"[^>]*data-sp=\"([^\"]+)\"', trs[0])
            print(name, "OK bytes=", len(html), "首场玩法:", btns[:15])
        else:
            print(name, "OK 无场次行")
    except Exception as e:
        print(name, "ERR", e)

fetch("https://trade.500.com/jczq/?playid=312&g=2", "jc_p312")  # 猜测:比分
fetch("https://trade.500.com/jczq/?playid=271&g=2", "jc_p271")  # 猜测:总进球
