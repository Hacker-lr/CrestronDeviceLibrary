# 抓取 MC3 Console 日志（Telnet 端口 23，Crestron console 免密）
import socket, sys, time

HOST = '192.168.0.100'
PORT = 23
DURATION = int(sys.argv[1]) if len(sys.argv) > 1 else 15

def send(s, cmd):
    s.sendall((cmd + '\r\n').encode('ascii'))
    time.sleep(0.6)
    drain(s)

def drain(s):
    total = b''
    s.setblocking(False)
    try:
        while True:
            d = s.recv(4096)
            if not d:
                break
            total += d
            if len(d) < 4096:
                break
    except BlockingIOError:
        pass
    except Exception:
        pass
    s.setblocking(True)
    return total.decode('utf-8', 'replace')

s = socket.create_connection((HOST, PORT), timeout=8)
s.settimeout(0.5)
time.sleep(1.0)
print('[+] connected, banner:', drain(s).strip() or '(empty)', flush=True)
send(s, '')          # 回车激活
send(s, 'progstat')  # 程序状态
send(s, 'progcomments')  # SIMPL+ print 输出（如有）
send(s, '')          # 回到空闲提示符

print('[*] streaming', DURATION, 's ...', flush=True)
buf = ''
t0 = time.time()
while time.time() - t0 < DURATION:
    d = drain(s)
    buf += d
    if d:
        for line in d.splitlines():
            l = line.strip()
            if l and ('[Redundant]' in l or '[Tesira]' in l
                      or 'ONLINE' in l.upper() or 'Resync' in l
                      or ('Status' in l and 'online' in l)
                      or 'TCP' in l or 'subscribe' in l
                      or 'progstat' in l.lower() or 'program' in l.lower()
                      or 'MC3>' in l):
                print(l, flush=True)
    time.sleep(0.2)
print('\n[*] raw tail:', flush=True)
print(buf[-2500:])
s.close()