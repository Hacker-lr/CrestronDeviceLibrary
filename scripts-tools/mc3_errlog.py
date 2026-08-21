# MC3 Console: 查错误日志 + SIMPL# 状态
import socket, time

HOST='192.168.0.100'; PORT=23

def drain(s, wait=0.9):
    time.sleep(wait)
    s.settimeout(0.3)
    out=b''
    while True:
        try:
            d=s.recv(8192)
            if not d: break
            out+=d
        except socket.timeout: break
        except Exception: break
    s.settimeout(0.5)
    return out.decode('latin-1','replace')

def cmd(s,c,wait=1.0):
    s.sendall((c+'\r\n').encode('ascii'))
    r=drain(s,wait)
    print('=== '+c+' ===')
    print(r)
    print('')
    return r

s=socket.create_connection((HOST,PORT),timeout=8); s.settimeout(0.5)
time.sleep(1.0); drain(s)

# 看错误日志（可能含库加载/连接异常）
cmd(s,'errlog')
cmd(s,'errlogs')
# SIMPL# / 程序相关命令
cmd(s,'progdump')
# 看运行状态
cmd(s,'progstat')
# 是否开了 SIMPL 调试输出
print('--- streaming 8s ---')
time.sleep(8)
print(drain(s,3))
s.close()