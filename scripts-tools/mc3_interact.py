# MC3 Console 交互单命令（Telnet 23），完整回显
import socket, sys, time

HOST = '192.168.0.100'
PORT = 23

def drain(s, wait=0.8):
    time.sleep(wait)
    s.settimeout(0.3)
    out = b''
    while True:
        try:
            d = s.recv(4096)
            if not d:
                break
            out += d
        except socket.timeout:
            break
        except Exception:
            break
    s.settimeout(0.5)
    return out.decode('utf-8', 'replace')

def cmd(s, c, wait=0.9):
    s.sendall((c + '\r\n').encode('ascii'))
    r = drain(s, wait)
    print('=== CMD: ' + c + ' ===')
    print(r)
    print('')

s = socket.create_connection((HOST, PORT), timeout=8)
s.settimeout(0.5)
time.sleep(1.0)
drain(s)
print('banner drained:', repr(drain(s, 0.5)))

for c in ['', 'progstat', 'help', 'progcomments']:
    cmd(s, c)

print('--- waiting 6s for device logs ---')
time.sleep(6)
print(drain(s, 3))
s.close()