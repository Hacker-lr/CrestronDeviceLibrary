using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using CrestronDeviceLibrary.Common;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// 迷你 MQTT broker（MQTT 3.1.1 服务端子集）：让 3 代/4 代中控自己当 broker。
    /// Tasmota 传感器直连中控的 1883 端口 publish 数据，无需外置 MQTT 服务器。
    ///
    /// 支持：CONNECT(账号密码校验)/SUBSCRIBE/PUBLISH(QoS0，QoS1 回 PUBACK)/PINGREQ/DISCONNECT；
    /// keepalive 超时踢线并注入 LWT 离线消息；单例，全程序共享一个端口。
    ///
    /// 3 代注意：TCPServer 必须显式给接收缓冲（默认缓冲太小会接不住大报文）；
    /// 所有 Crestron 线程入口（socket/定时器回调）全部 try/catch 兜底——
    /// 回调里逃出的异常会把整个 SIMPL 运行时打死（AppWatchdog 崩溃重启）。
    ///
    /// 用法：
    ///   var broker = MqttMiniBroker.GetInstance();
    ///   broker.OnMessage += (topic, payload) => ...;
    ///   broker.Configure(1883, "frank", "crestron");
    ///   broker.Start();
    /// </summary>
    public class MqttMiniBroker
    {
        // ---------------- 单例 ----------------
        private static MqttMiniBroker _instance;
        private static readonly object _instanceLock = new object();

        public static MqttMiniBroker GetInstance()
        {
            lock (_instanceLock)
            {
                if (_instance == null) _instance = new MqttMiniBroker();
                return _instance;
            }
        }

        // ---------------- 事件 ----------------
        /// <summary>收到设备 publish（topic, payload）。在 socket 线程上回调，注意线程安全。</summary>
        public delegate void MqttMessageHandler(string topic, string payload);
        public event MqttMessageHandler OnMessage;

        /// <summary>设备连接/断开通知（deviceTopic 取自 will 或订阅主题，可能为 ""）。</summary>
        public delegate void MqttClientStateHandler(string deviceTopic, bool connected);
        public event MqttClientStateHandler OnClientState;

        /// <summary>调试日志开关（CrestronConsole.PrintLine，console/SSH 里可见）。</summary>
        public bool VerboseLog { get; set; }

        // ---------------- 会话 ----------------
        private class Session
        {
            public uint ClientIndex;
            public string ClientId = "";
            public string DeviceTopic = "";      // 设备 topic（如 huang）
            public string WillTopic = "";
            public string WillMessage = "";
            public ushort KeepAliveSec;
            public bool HandshakeDone;
            public long LastSeenTick;            // Environment.TickCount
            public readonly List<string> Subscriptions = new List<string>();
            public readonly byte[] Acc = new byte[8192];  // 粘包缓存
            public int AccLen;
        }

        private TCPServer _server;
        private int _port = 1883;
        private string _user = "";
        private string _pass = "";
        private bool _started;
        private readonly Dictionary<uint, Session> _sessions = new Dictionary<uint, Session>();
        private readonly object _lock = new object();
        private CTimer _watchdog;

        private MqttMiniBroker() { }

        // ---------------- 对外接口 ----------------

        /// <summary>配置监听端口与账号校验（user/pass 传 "" 表示不校验）。幂等，Start 前调用。</summary>
        public void Configure(int port, string user, string pass)
        {
            _port = port > 0 ? port : 1883;
            _user = user ?? "";
            _pass = pass ?? "";
        }

        /// <summary>启动监听。重复调用安全。</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }
            try
            {
                // 显式接收缓冲 4096：Tasmota 的 discovery/STATE 报文近 1KB，
                // 3 代默认缓冲太小，native 层溢出会直接打死程序（无托管异常日志）。
                _server = new TCPServer("0.0.0.0", _port, 4096,
                                        EthernetAdapterType.EthernetUnknownAdapter, 10);
                _server.WaitForConnectionsAlways(OnClientConnect);
                LogAlways("[MQTT] broker listening on port " + _port);

                if (_watchdog == null)
                    _watchdog = new CTimer(CheckKeepAlive, null, 5000, 5000);
            }
            catch (Exception ex)
            {
                ErrorLog.Error("[MQTT] broker start failed: " + ex.Message);
                lock (_lock) { _started = false; }
            }
        }

        /// <summary>向已订阅该主题的设备下发 PUBLISH（如 cmnd/huang/TelePeriod）。无订阅者则丢弃。</summary>
        public void Publish(string topic, string payload)
        {
            try
            {
                byte[] pkt = MqttProtocol.BuildPublish(topic, payload);
                Session[] snapshot;
                lock (_lock) { snapshot = new List<Session>(_sessions.Values).ToArray(); }
                foreach (Session s in snapshot)
                {
                    bool match = false;
                    lock (_lock)
                    {
                        foreach (string f in s.Subscriptions)
                            if (MqttProtocol.MatchTopic(f, topic)) { match = true; break; }
                    }
                    if (match)
                    {
                        try { _server.SendData(s.ClientIndex, pkt, 0, pkt.Length); }
                        catch (Exception ex) { Log("[MQTT] publish to " + s.ClientId + " failed: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { ErrorLog.Error("[MQTT] Publish error: " + ex.Message); }
        }

        // ---------------- TCPServer 回调（全部 try/catch 兜底） ----------------

        private void OnClientConnect(TCPServer server, uint clientIndex)
        {
            try
            {
                if (clientIndex == 0) return;
                lock (_lock)
                {
                    Session s = new Session();
                    s.ClientIndex = clientIndex;
                    s.LastSeenTick = Environment.TickCount;
                    _sessions[clientIndex] = s;
                }
                LogAlways("[MQTT] client connected idx=" + clientIndex);
                ReceiveNext(clientIndex);
            }
            catch (Exception ex) { ErrorLog.Error("[MQTT] OnClientConnect error: " + ex.Message); }
        }

        private void ReceiveNext(uint clientIndex)
        {
            try { _server.ReceiveDataAsync(clientIndex, OnReceive); }
            catch (Exception ex) { Log("[MQTT] ReceiveDataAsync failed: " + ex.Message); }
        }

        private void OnReceive(TCPServer server, uint clientIndex, int numBytes)
        {
            try
            {
                Session s;
                lock (_lock) { if (!_sessions.TryGetValue(clientIndex, out s)) return; }

                if (numBytes <= 0)            // 对端关闭/出错 → 异常断开
                {
                    DropSession(clientIndex, true);
                    return;
                }

                s.LastSeenTick = Environment.TickCount;
                byte[] data = server.GetIncomingDataBufferForSpecificClient(clientIndex);
                if (data == null) { ReceiveNext(clientIndex); return; }
                if (numBytes > data.Length) numBytes = data.Length;   // 防御：缓冲比报告的小

                // 粘包缓存
                if (s.AccLen + numBytes > s.Acc.Length)
                {
                    Log("[MQTT] buffer overflow idx=" + clientIndex + ", reset");
                    s.AccLen = 0;
                }
                if (numBytes > s.Acc.Length) numBytes = s.Acc.Length;
                Array.Copy(data, 0, s.Acc, s.AccLen, numBytes);
                s.AccLen += numBytes;

                // 逐包解析
                int offset = 0;
                while (offset < s.AccLen)
                {
                    MqttProtocol.Packet pkt;
                    int used = 0;
                    try
                    {
                        byte[] window = new byte[s.AccLen - offset];
                        Array.Copy(s.Acc, offset, window, 0, window.Length);
                        used = MqttProtocol.TryReadPacket(window, window.Length, out pkt);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Error("[MQTT] packet parse error: " + ex.Message);
                        s.AccLen = 0;         // 解析炸了：清空缓存防死循环
                        break;
                    }
                    if (used == 0) break;                 // 半包，等下一批数据
                    offset += used;
                    HandlePacket(s, pkt);
                }
                if (offset > 0 && offset <= s.AccLen)     // 压缩缓存
                {
                    Array.Copy(s.Acc, offset, s.Acc, 0, s.AccLen - offset);
                    s.AccLen -= offset;
                }

                ReceiveNext(clientIndex);
            }
            catch (Exception ex) { ErrorLog.Error("[MQTT] OnReceive error: " + ex.Message); }
        }

        // ---------------- 报文分发 ----------------

        private void HandlePacket(Session s, MqttProtocol.Packet pkt)
        {
            try
            {
                switch (pkt.Type)
                {
                    case MqttProtocol.TypeConnect:
                    {
                        MqttProtocol.ConnectInfo info = MqttProtocol.ParseConnect(pkt);
                        bool ok = (_user.Length == 0) || (info.UserName == _user && info.Password == _pass);
                        s.ClientId = info.ClientId;
                        s.KeepAliveSec = info.KeepAliveSec;
                        s.WillTopic = info.WillTopic;
                        s.WillMessage = info.WillMessage;
                        if (info.WillTopic.Length > 0)
                            s.DeviceTopic = MqttProtocol.DeviceTopicOf(info.WillTopic);
                        s.HandshakeDone = ok;
                        Send(s, MqttProtocol.BuildConnAck(ok));
                        LogAlways("[MQTT] CONNECT id=" + info.ClientId + " topic=" + s.DeviceTopic + (ok ? " ok" : " AUTH FAIL"));
                        if (!ok) { DropSession(s.ClientIndex, false); return; }
                        FireClientState(s.DeviceTopic, true);
                        break;
                    }
                    case MqttProtocol.TypeSubscribe:
                    {
                        string[] topics;
                        ushort pid = MqttProtocol.ParseSubscribe(pkt, out topics);
                        foreach (string t in topics)
                        {
                            s.Subscriptions.Add(t);
                            if (s.DeviceTopic.Length == 0)
                                s.DeviceTopic = MqttProtocol.DeviceTopicOf(t);
                            Log("[MQTT] SUB " + s.ClientId + " -> " + t);
                        }
                        Send(s, MqttProtocol.BuildSubAck(pid, topics.Length));
                        break;
                    }
                    case MqttProtocol.TypePublish:
                    {
                        string topic, payload; ushort pid;
                        MqttProtocol.ParsePublish(pkt, out topic, out payload, out pid);
                        if (((pkt.Flags >> 1) & 0x03) == 1) Send(s, MqttProtocol.BuildPubAck(pid));
                        if (s.DeviceTopic.Length == 0)
                            s.DeviceTopic = MqttProtocol.DeviceTopicOf(topic);
                        Log("[MQTT] " + topic + " => " + payload);
                        FireMessage(topic, payload);
                        break;
                    }
                    case MqttProtocol.TypePingReq:
                        Send(s, MqttProtocol.BuildPingResp());
                        break;
                    case MqttProtocol.TypeDisconnect:
                        DropSession(s.ClientIndex, false);   // 正常断开，不发遗嘱
                        break;
                    default:
                        Log("[MQTT] ignore packet type=" + pkt.Type);
                        break;
                }
            }
            catch (Exception ex) { ErrorLog.Error("[MQTT] HandlePacket type=" + pkt.Type + " error: " + ex.Message); }
        }

        // ---------------- 会话生命周期 ----------------

        private void CheckKeepAlive(object unused)
        {
            try
            {
                List<Session> dead = new List<Session>();
                lock (_lock)
                {
                    foreach (Session s in _sessions.Values)
                    {
                        if (s.KeepAliveSec > 0 &&
                            Environment.TickCount - s.LastSeenTick > (long)s.KeepAliveSec * 1500)
                            dead.Add(s);
                    }
                }
                foreach (Session s in dead)
                {
                    Log("[MQTT] keepalive timeout id=" + s.ClientId);
                    DropSession(s.ClientIndex, true);
                }
            }
            catch (Exception ex) { ErrorLog.Error("[MQTT] CheckKeepAlive error: " + ex.Message); }
        }

        /// <summary>移除会话。abnormal=true 时注入遗嘱消息（LWT Offline）。</summary>
        private void DropSession(uint clientIndex, bool abnormal)
        {
            Session s;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(clientIndex, out s)) return;
                _sessions.Remove(clientIndex);
            }
            try { _server.Disconnect(clientIndex); } catch { }

            LogAlways("[MQTT] client gone idx=" + clientIndex + " id=" + s.ClientId + (abnormal ? " (abnormal)" : ""));
            if (abnormal && s.WillTopic.Length > 0)
                FireMessage(s.WillTopic, s.WillMessage);      // 遗嘱：tele/xxx/LWT Offline
            if (s.DeviceTopic.Length > 0)
                FireClientState(s.DeviceTopic, false);
        }

        private void Send(Session s, byte[] pkt)
        {
            try { _server.SendData(s.ClientIndex, pkt, 0, pkt.Length); }
            catch (Exception ex) { Log("[MQTT] send failed: " + ex.Message); }
        }

        private void FireMessage(string topic, string payload)
        {
            MqttMessageHandler h = OnMessage;
            if (h != null)
            {
                try { h(topic, payload); }
                catch (Exception ex) { ErrorLog.Error("[MQTT] OnMessage handler error: " + ex.Message); }
            }
        }

        private void FireClientState(string deviceTopic, bool connected)
        {
            MqttClientStateHandler h = OnClientState;
            if (h != null)
            {
                try { h(deviceTopic, connected); }
                catch (Exception ex) { ErrorLog.Error("[MQTT] OnClientState handler error: " + ex.Message); }
            }
        }

        private void Log(string msg)
        {
            if (VerboseLog) CrestronConsole.PrintLine(msg);
        }

        /// <summary>关键里程碑日志：不受 VerboseLog 控制，排查"模块到底跑没跑"用。</summary>
        private void LogAlways(string msg)
        {
            CrestronConsole.PrintLine(msg);
        }
    }
}
