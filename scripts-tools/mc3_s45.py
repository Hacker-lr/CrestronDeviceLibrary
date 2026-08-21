# MC3 Console: 抓取 S-4.5 KeyNotFound 最新错误 + 完整时间线
import socket, time, re

HOST='192.168.0.100'; PORT=23

def drain(s, wait=1.0):
    time.sleep(wait)
    s.settimeout(0.3)
    out=b''
    while True:
        try:
            d=s.recv(65536)
            if not d: break
            out+=d
        except socket.timeout: break
        except Exception: break
    s.settimeout(0.5)
    return out.decode('latin-1','replace')

def cmd(s,c,wait=1.2):
    s.sendall((c+'\r\n').encode('ascii'))
    return drain(s,wait)

s=socket.create_connection((HOST,PORT),timeout=8); s.settimeout(0.5)
time.sleep(1.0); drain(s)

# errlog 全文，只保留关键行
r = cmd(s,'errlog')
print("=== errlog relevant ===")
kept = []
for ln in r.splitlines():
    t = ln.strip()
    if not t: continue
    if ('S-4.5' in t or 'KeyNotFound' in t or 'Tesira' in t or 'Redundant' in t
        or 'Device' in t or 'online' in t.lower() or 'Connection' in t
        or 'Started' in t or 'Stopped' in t or 'shutting' in t.lower()):
        kept.append(t)
print('\n'.join(kept[:600]))
print(f"\n[kept {len(kept)} lines]")
s.close()