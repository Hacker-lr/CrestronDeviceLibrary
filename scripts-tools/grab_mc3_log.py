# 抓取 MC3 Console 日志（兼容 Crestron 旧式 SSH 主机键算法）
import paramiko, sys, time

HOST = '192.168.0.100'
USER = 'CRESTRON'
PASS = 'CRESTRON'
PORTS = [41795, 22, 41794, 23, 61794]
DURATION = int(sys.argv[1]) if len(sys.argv) > 1 else 15

def grab(port):
    t = paramiko.Transport((HOST, port))
    t.banner_timeout = 10
    t.handshake_timeout = 10
    try:
        t.start_client()
    except (EOFError, paramiko.SSHException) as e:
        print('[-]', port, 'start_client fail:', str(e)[:80], flush=True)
        t.close()
        return None
    # Crestron 老 SSHD 可能只认 ssh-rsa / ssh-dss
    for alg in [paramiko.DSSKey, paramiko.Ed25519Key, paramiko.RSAKey, paramiko.ECDSAKey]:
        try:
            k = t.get_remote_server_key()
            print('[+]', port, 'remote key type:', k.get_name(), flush=True)
            break
        except Exception:
            pass
    try:
        t.auth_password(USER, PASS)
    except paramiko.AuthenticationException as e:
        print('[-]', port, 'auth fail:', str(e)[:80], flush=True)
        t.close()
        return None
    ch = t.open_session()
    ch.get_pty()
    ch.invoke_shell()
    time.sleep(1.0)
    if ch.recv_ready():
        ch.recv(65535)
    print('[+]', port, 'auth OK, shell up', flush=True)
    return t, ch

def run(ch, dur):
    ch.send('\r\n'); time.sleep(0.5)
    if ch.recv_ready(): ch.recv(65535)
    ch.send('progstat\r\n'); time.sleep(0.8)
    while ch.recv_ready(): ch.recv(65535)
    print('[*] streaming', dur, 's ...', flush=True)
    buf = ''
    t0 = time.time()
    while time.time() - t0 < dur:
        if ch.recv_ready():
            data = ch.recv(65535).decode('utf-8', 'replace')
            buf += data
            for line in data.splitlines():
                l = line.strip()
                if l and ('[Redundant]' in l or '[Tesira]' in l
                          or 'ONLINE' in l.upper() or 'Resync' in l
                          or ('Status' in l and 'online' in l)
                          or 'TCP' in l or 'subscribe' in l
                          or 'progstat' in l.lower() or 'program' in l.lower()):
                    print(l, flush=True)
        time.sleep(0.2)
    print('\n[*] raw tail:', flush=True)
    print(buf[-2000:])

for p in PORTS:
    r = grab(p)
    if r:
        t, ch = r
        try:
            run(ch, DURATION)
        finally:
            try: t.close()
            except Exception: pass
        break

print('\n[done]')