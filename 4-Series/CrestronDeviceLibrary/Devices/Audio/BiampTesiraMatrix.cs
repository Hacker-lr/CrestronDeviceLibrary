using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// Biamp Tesira 音频处理器（Tesira Text Protocol，TCP 23 / Telnet）。
    ///
    /// 协议为纯 ASCII 文本（行尾 CR/LF），命令格式：
    ///   InstanceTag &lt;command&gt; &lt;attribute&gt; [index] [value]
    ///   例：Level1 set mute 1 true / Level1 increment level 1 1 / Mixer1 set crosspointLevelState 1 1 true
    ///   响应：+OK "value":X  /  +OK  /  -ERR 消息
    ///
    /// 【订阅模式】连接登录后就绪后，对电平(levels)/静音(mutes)/信号表(levels)/交叉点
    ///   (crosspointLevelState) 做 subscribe，设备状态变化会主动推送，不再轮询 get。
    ///   推送格式（verbose）：! "publishToken":"&lt;标签&gt;" "value":&lt;值&gt;
    ///   - 整块订阅返回数组：level/mute/meter 用 subscribe levels / subscribe mutes
    ///   - 交叉点只能逐点订阅（Matrix Mixer 的 crosspointLevelStateAll 不支持 subscribe）
    ///
    /// 与 StageCraftMatrix 架构一致：C# 库直连设备 TCP，SIMPL+ 薄壳负责引脚接线。
    /// 区别：StageCraft 是"二进制 0x82 帧 + ASCII '#' 命令"混合协议；Tesira 是纯文本 telnet，
    /// 需要处理 Telnet IAC 协商（拒绝所有选项）和登录（默认 default/default）。
    ///
    /// 通道数可配置：ConfigureChannels(N) 运行时设置（对应 .usp 的 #DEFINE_CONSTANT CH），
    /// 反馈 id 基址随之计算（N=16 时与 StageCraft 完全一致的映射）。
    ///
    /// 实例标签（Instance Tag）与 Tesira 设计文件(.tmf)相关，做成可配置参数：
    ///   输入/输出电平块、矩阵混音块、输入/输出信号表块、预设(DEVICE recallPreset)。
    /// </summary>
    public class BiampTesiraMatrix : IMatrixControl
    {
        // ---------- 常量 ----------
        public const ushort AnalogMid = 53928;       // 模拟量 0dB 中点（增益推子）
        public const ushort DbPerStep = 963;         // 每 dB 的模拟量刻度
        public const int MeterDbMin = -60;           // 音量表(VU)下限 dB
        public const int MeterDbMax = 12;            // 音量表(VU)上限 dB

        // 订阅节流（毫秒）：订阅命令最后一个参数，限制推送频率
        private const int ThrottleLevel = 200;       // 增益推子推送限频
        private const int ThrottleMeter = 100;       // 信号表 100ms（10Hz，VU 平滑）
        private const int ThrottleMute = 0;          // 静音立即推送
        private const int ThrottleXp = 0;            // 交叉点立即推送

        // 订阅标签（publishToken），固定，用于识别推送来源
        private const string LblInLevel = "inLv";
        private const string LblOutLevel = "outLv";
        private const string LblInMute = "inMt";
        private const string LblOutMute = "outMt";
        private const string LblInMeter = "inMeter";
        private const string LblOutMeter = "outMeter";
        private const string LblXp = "xp";           // xp<in>_<out>

        // 调试开关（static readonly 避免 CS0162 不可达代码警告）
        private static readonly bool VerboseLog = false;

        // ---------- 输出委托（SIMPL+ 用 RegisterDelegate 订阅） ----------
        // 委托类型统一为 RedundantAudioMatrix.cs 的 Matrix*Fb，供 IMatrixControl 双机镜像。
        public MatrixDigitalFb DigitalFb { get; set; }
        public MatrixAnalogFb AnalogFb { get; set; }
        public MatrixSerialFb SerialFb { get; set; }

        /// <summary>连接状态变化：true=已连，false=断开（冗余控制器据此做 leader 选举与重同步）。</summary>
        public event DeviceConnectionHandler ConnectionStateChanged;

        // ---------- 状态 ----------
        public ushort SelectedOut { get; private set; }
        public ushort SelectedIn { get; private set; }

        private int _channels = 16;                  // 运行时通道数（ConfigureChannels 设置）
        // 默认按 16 通道初始化（兼容旧 .usp 不调 ConfigureChannels 的情况，避免空引用）
        private bool[] _inMute = new bool[17];
        private bool[] _outMute = new bool[17];
        private bool[,] _route = new bool[17, 17];   // [输出, 输入]

        // ---- dirty check：值没变不 Raise（3 系 VTP 卡顿优化；4 系行为不变）----
        // 用 int.MaxValue 作"无值"哨兵（合法模拟值 0~65535 不会碰撞）
        private readonly int[] _lastLevelAnalog = InitNeg(17);
        private readonly bool[] _lastMuted = new bool[17];       // level 的 mute 状态（mute 推 0+"Mute"文本，需与 analog 一起判断）
        private readonly int[] _lastMeterAnalog = InitNeg(17);
        private static int[] InitNeg(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = int.MaxValue; return a; }

        // 反馈 id 基址（随 _channels 计算；_channels=16 时与旧版/StageCraft 完全一致）
        private ushort FbInMute { get { return 1; } }
        private ushort FbOutMute { get { return (ushort)(_channels + 1); } }
        private ushort FbMixIn { get { return (ushort)(2 * _channels + 1); } }
        private ushort FbMixOut { get { return (ushort)(3 * _channels + 1); } }
        private ushort FbMode1 { get { return (ushort)(4 * _channels + 1); } }
        private ushort FbAllMute { get { return (ushort)(4 * _channels + 6); } }
        private ushort AfbInLevel { get { return 1; } }
        private ushort AfbOutLevel { get { return (ushort)(_channels + 1); } }
        private ushort AfbInMeter { get { return (ushort)(2 * _channels + 1); } }
        private ushort AfbOutMeter { get { return (ushort)(3 * _channels + 1); } }
        private ushort SfbInLevelText { get { return 1; } }
        private ushort SfbOutLevelText { get { return (ushort)(_channels + 1); } }

        // 连接状态机
        private enum ConnState { Disconnected, Negotiating, Login, Ready }
        private ConnState _state = ConnState.Disconnected;
        private bool _sentUser;
        private bool _sentPass;

        // TCP 直连
        private TCPClient _client;
        private string _ip = "192.168.0.222";
        private int _port = 23;
        private bool _connected;
        private readonly object _sendLock = new object();
        private CTimer _reconnectTimer;
        private CTimer _loginTimer;
        private CTimer _resubTimer;

        // 实例标签（Tesira 设计文件相关，可配置）
        private string _inLevelTag = "Level1";
        private string _outLevelTag = "Level2";
        private string _mixerTag = "Mixer1";
        private string _inMeterTag = "";   // 留空则跳过输入信号表订阅
        private string _outMeterTag = "";  // 留空则跳过输出信号表订阅
        private string _username = "default";
        private string _password = "default";

        // 接收缓冲：原始字节（含 Telnet IAC）先过 IAC 处理器，干净文本进 _textRx
        private readonly List<byte> _telnetBuf = new List<byte>(4096);
        private readonly List<byte> _textRx = new List<byte>(8192);

        // 正则（预编译）
        private static readonly Regex RxPublish = new Regex(@"!\s*""publishToken""\s*:\s*""([^""]*)""\s*""value""\s*:\s*(.+?)\s*$");
        private static readonly Regex RxArray = new Regex(@"\[(.*?)\]");
        private static readonly Regex RxXp = new Regex(@"^xp(\d+)_(\d+)$");

        // =====================================================================
        //  连接配置（SIMPL+ 在 Main 里调用）
        // =====================================================================
        /// <summary>设置设备 TCP 地址（IP 字符串 + 端口）。变化时自动重连。</summary>
        public void Configure(SimplSharpString ip, ushort port)
        {
            string s = (ip != null) ? ip.ToString() : "";
            string newIp = !string.IsNullOrEmpty(s) ? s.Trim() : _ip;
            int newPort = port > 0 ? port : _port;
            bool changed = (newIp != _ip) || (newPort != _port);
            _ip = newIp;
            _port = newPort;
            CrestronConsole.PrintLine("[Tesira] Configure ip={0} port={1} (changed={2})", _ip, _port, changed);
            if (changed && _client != null)
            {
                CrestronConsole.PrintLine("[Tesira] IP/port changed, reconnecting...");
                Stop();
                ConnectAsync();
            }
        }

        /// <summary>设置通道数（对应 .usp 的 #DEFINE_CONSTANT CH）。1~64。</summary>
        public void ConfigureChannels(ushort channels)
        {
            if (channels < 1) channels = 1;
            if (channels > 64) channels = 64;
            if (channels != _channels)
            {
                _channels = channels;
                _inMute = new bool[_channels + 1];
                _outMute = new bool[_channels + 1];
                _route = new bool[_channels + 1, _channels + 1];
            }
            CrestronConsole.PrintLine("[Tesira] Channels = {0}", _channels);
        }

        /// <summary>设置实例标签（Tesira 设计文件里各 DSP 块的 Instance Tag）。"none" 则跳过对应项。</summary>
        public void ConfigureTags(SimplSharpString inLevelTag, SimplSharpString outLevelTag,
            SimplSharpString mixerTag, SimplSharpString inMeterTag, SimplSharpString outMeterTag)
        {
            _inLevelTag = TrimTag(inLevelTag);
            _outLevelTag = TrimTag(outLevelTag);
            _mixerTag = TrimTag(mixerTag);
            _inMeterTag = TrimTag(inMeterTag);
            _outMeterTag = TrimTag(outMeterTag);
            CrestronConsole.PrintLine("[Tesira] Tags inLevel={0} outLevel={1} mixer={2} inMeter={3} outMeter={4}",
                _inLevelTag, _outLevelTag, _mixerTag, _inMeterTag, _outMeterTag);
        }

        /// <summary>设置登录凭据（默认 default/default）。用户名为空则跳过登录。</summary>
        public void ConfigureCredentials(SimplSharpString username, SimplSharpString password)
        {
            _username = (username != null) ? username.ToString().Trim() : "";
            _password = (password != null) ? password.ToString() : "";
            CrestronConsole.PrintLine("[Tesira] Credentials user={0} pass={1}", _username, (_password.Length > 0 ? "***" : "(empty)"));
        }

        private static string TrimTag(SimplSharpString s)
        {
            if (s == null) return "";
            string t = s.ToString().Trim();
            // "none" 哨兵：.usp 里无法用空字符串常量，用 "none" 表示"不启用该块"
            if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return "";
            return t;
        }

        /// <summary>开始连接设备（异步，成功后自动 Telnet 协商 + 登录 + 订阅）。</summary>
        public void Start()
        {
            ConnectAsync();
        }

        /// <summary>断开连接并停止重连。</summary>
        public void Stop()
        {
            CancelReconnect();
            CancelLoginTimer();
            CancelResubTimer();
            try
            {
                if (_client != null)
                {
                    _client.SocketStatusChange -= OnSocketStatusChange;
                    _client.DisconnectFromServer();
                    _client = null;
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Tesira] Stop EXCEPTION: {0}", ex.Message);
            }
            SetConnected(false);
            _state = ConnState.Disconnected;
        }

        private void ConnectAsync()
        {
            CancelReconnect();
            try
            {
                _state = ConnState.Negotiating;
                _sentUser = false;
                _sentPass = false;
                _telnetBuf.Clear();
                _textRx.Clear();
                CrestronConsole.PrintLine("[Tesira] TCP connecting {0}:{1} ...", _ip, _port);
                _client = new TCPClient(_ip, _port, 4096);
                _client.SocketStatusChange += OnSocketStatusChange;
                SocketErrorCodes err = _client.ConnectToServerAsync(OnConnectComplete);
                if (err != SocketErrorCodes.SOCKET_OPERATION_PENDING && err != SocketErrorCodes.SOCKET_OK)
                {
                    CrestronConsole.PrintLine("[Tesira] TCP connect async err={0}", err);
                    ScheduleReconnect();
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Tesira] TCP connect EXCEPTION: {0}", ex.Message);
                ScheduleReconnect();
            }
        }

        private void OnConnectComplete(TCPClient client)
        {
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                SetConnected(true);
                CrestronConsole.PrintLine("[Tesira] TCP connected {0}:{1}, waiting Telnet negotiation...", _ip, _port);
                client.ReceiveDataAsync(OnReceiveData);
                // 关键：本设备 Telnet 协商后【不主动发欢迎语】，直接接受命令，所以不能靠
                // "welcome" 文本触发就绪。改为延迟 1.5s 让协商完成即 MarkReady（幂等）。
                // 期间若收到 login/password 提示，HandleLine 会先走凭据登录再 MarkReady。
                CancelLoginTimer();
                _loginTimer = new CTimer(o => { if (_state != ConnState.Ready) MarkReady(); }, null, 1500, 0);
            }
            else
            {
                CrestronConsole.PrintLine("[Tesira] TCP connect failed status={0}", client.ClientStatus);
                ScheduleReconnect();
            }
        }

        private void OnSocketStatusChange(TCPClient client, SocketStatus status)
        {
            CrestronConsole.PrintLine("[Tesira] TCP status -> {0}", status);
            switch (status)
            {
                case SocketStatus.SOCKET_STATUS_CONNECTED:
                    SetConnected(true);
                    break;
                case SocketStatus.SOCKET_STATUS_LINK_LOST:
                case SocketStatus.SOCKET_STATUS_BROKEN_LOCALLY:
                case SocketStatus.SOCKET_STATUS_BROKEN_REMOTELY:
                case SocketStatus.SOCKET_STATUS_NO_CONNECT:
                case SocketStatus.SOCKET_STATUS_CONNECT_FAILED:
                    SetConnected(false);
                    _state = ConnState.Disconnected;
                    ScheduleReconnect();
                    break;
            }
        }

        private void OnReceiveData(TCPClient client, int numberOfBytes)
        {
            if (numberOfBytes <= 0)
            {
                CrestronConsole.PrintLine("[Tesira] TCP closed by remote (len={0})", numberOfBytes);
                SetConnected(false);
                _state = ConnState.Disconnected;
                ScheduleReconnect();
                return;
            }
            try
            {
                byte[] src = client.IncomingDataBuffer;
                byte[] data = new byte[numberOfBytes];
                Array.Copy(src, data, numberOfBytes);
                _telnetBuf.AddRange(data);
                PumpTelnet();
                ProcessLines();
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Tesira] OnReceiveData EXCEPTION: {0}", ex.Message);
            }
            client.ReceiveDataAsync(OnReceiveData);
        }

        private void ScheduleReconnect()
        {
            if (_reconnectTimer != null) return;
            CrestronConsole.PrintLine("[Tesira] schedule reconnect in 5s");
            _reconnectTimer = new CTimer(o =>
            {
                _reconnectTimer = null;
                ConnectAsync();
            }, null, 5000, 0);
        }

        private void CancelReconnect()
        {
            if (_reconnectTimer == null) return;
            _reconnectTimer.Stop();
            _reconnectTimer.Dispose();
            _reconnectTimer = null;
        }

        private void CancelLoginTimer()
        {
            if (_loginTimer == null) return;
            _loginTimer.Stop();
            _loginTimer.Dispose();
            _loginTimer = null;
        }

        private void CancelResubTimer()
        {
            if (_resubTimer == null) return;
            _resubTimer.Stop();
            _resubTimer.Dispose();
            _resubTimer = null;
        }

        // =====================================================================
        //  Telnet IAC 协商处理：把 0xFF 序列从数据流剥离，并回 WON'T/DON'T
        // =====================================================================
        private void PumpTelnet()
        {
            int i = 0;
            while (i < _telnetBuf.Count)
            {
                byte b = _telnetBuf[i];
                if (b != 0xFF)
                {
                    _textRx.Add(b);
                    i++;
                    continue;
                }
                // IAC
                if (i + 1 >= _telnetBuf.Count) break; // 不完整，等更多数据
                byte cmd = _telnetBuf[i + 1];
                if (cmd == 0xFF)   // 转义的 0xFF
                {
                    _textRx.Add(0xFF);
                    i += 2;
                    continue;
                }
                if (cmd == 0xFD || cmd == 0xFB)   // DO / WILL → 回 WON'T / DON'T
                {
                    if (i + 2 >= _telnetBuf.Count) break; // 不完整
                    byte opt = _telnetBuf[i + 2];
                    byte reply = (cmd == 0xFD) ? (byte)0xFC : (byte)0xFE;
                    byte[] r = { 0xFF, reply, opt };
                    SendRaw(r);
                    i += 3;
                    continue;
                }
                if (cmd == 0xFA)   // SB 子协商：跳过直到 IAC SE(FF F0)
                {
                    int j = i + 2;
                    while (j + 1 < _telnetBuf.Count && !(_telnetBuf[j] == 0xFF && _telnetBuf[j + 1] == 0xF0)) j++;
                    if (j + 1 >= _telnetBuf.Count) { i = _telnetBuf.Count; break; } // 未结束，等更多
                    i = j + 2;
                    continue;
                }
                // DON'T / WON'T / SE / NOP 等两字节 IAC，忽略
                i += 2;
            }
            // 消费已处理的字节
            if (i > 0) _telnetBuf.RemoveRange(0, i);
        }

        // =====================================================================
        //  文本行解析（CR LF / CR NUL 结尾）
        // =====================================================================
        private void ProcessLines()
        {
            int guard = 0;
            while (_textRx.Count > 0 && guard++ < 500)
            {
                int cr = _textRx.IndexOf(0x0D);
                if (cr < 0) break;
                // 取本行（CR 之前的字节），并跳过 CR 后的 LF 或 NUL
                var lineBytes = _textRx.GetRange(0, cr);
                int consume = cr + 1;
                if (consume < _textRx.Count && (_textRx[consume] == 0x0A || _textRx[consume] == 0x00))
                    consume++;
                _textRx.RemoveRange(0, consume);
                if (lineBytes.Count > 0)
#if SERIES3
                    HandleLine(BytesToString(lineBytes.ToArray()));
#else
                    HandleLine(Encoding.ASCII.GetString(lineBytes.ToArray()));
#endif
            }
            // 防累积：正常不应超过 4KB，脏数据卡死时清空自恢复
            if (_textRx.Count > 16384)
            {
                CrestronConsole.PrintLine("[Tesira] WARN text buffer overflow {0}B, clear", _textRx.Count);
                _textRx.Clear();
            }
        }

        private void HandleLine(string line)
        {
            string t = line.Trim();
            if (t.Length == 0) return;

            if (VerboseLog) CrestronConsole.PrintLine("[Tesira] RX line: {0}", t);

            // ---- 登录提示处理（未就绪前，收到 login/password 提示才发凭据；无提示则由 1.5s 兜底就绪）----
            string lower = t.ToLower();
            if (_state != ConnState.Ready && !_sentUser && (lower.Contains("login") || lower.Contains("username") || lower.Contains("user name")))
            {
                SendLoginUser();
                return;
            }
            if (_state != ConnState.Ready && _sentUser && !_sentPass && lower.Contains("password"))
            {
                SendLoginPass();
                return;
            }

            // ---- 订阅推送 ----
            if (t.StartsWith("!"))
            {
                Match m = RxPublish.Match(t);
                if (m.Success)
                    HandlePublish(m.Groups[1].Value, m.Groups[2].Value.Trim());
                else if (VerboseLog)
                    CrestronConsole.PrintLine("[Tesira] unparsed push: {0}", t);
                return;
            }

            // ---- 响应 ----
            if (t.StartsWith("-ERR"))
            {
                CrestronConsole.PrintLine("[Tesira] -ERR: {0}", t);
            }
            // +OK（set/increment/toggle 的应答）和命令回显：忽略
        }

        private void SendLoginUser()
        {
            _sentUser = true;
            CrestronConsole.PrintLine("[Tesira] send username");
            SendRaw(Encoding.ASCII.GetBytes(_username + "\r\n"));
        }

        private void SendLoginPass()
        {
            _sentPass = true;
            CrestronConsole.PrintLine("[Tesira] send password");
            SendRaw(Encoding.ASCII.GetBytes(_password + "\r\n"));
            MarkReady();
        }

        private void MarkReady()
        {
            if (_state == ConnState.Ready) return;
            CancelLoginTimer();
            // 显式设 verbose，确保订阅推送都是 "+OK "value":X" 格式（解析一致，不依赖设备默认）
            SendCommand("SESSION set verbose true");
            _state = ConnState.Ready;
            CrestronConsole.PrintLine("[Tesira] v1.1 ONLINE - subscription mode");
            // 就绪后先推送默认值（VTP 立即有显示，避免初始化空白），订阅推送随后覆盖实际值
            PushDefaultStates();
            SubscribeAll();
            StartResubTimer();
        }

        /// <summary>
        /// 推送默认状态（VTP 立即有显示，避免初始化空白）：
        /// 电平 0dB、静音 off、音量表 0。订阅推送随后覆盖实际值。
        /// </summary>
        private void PushDefaultStates()
        {
            for (ushort ch = 1; ch <= _channels; ch++)
            {
                // 电平推子：0dB（中间位置）
                RaiseAnalog((ushort)(AfbInLevel + ch - 1), DbToAnalog(0));
                RaiseAnalog((ushort)(AfbOutLevel + ch - 1), DbToAnalog(0));
                // 电平文本："1:0dB"
                RaiseSerial((ushort)(SfbInLevelText + ch - 1), new SimplSharpString(ch + ":0dB"));
                RaiseSerial((ushort)(SfbOutLevelText + ch - 1), new SimplSharpString(ch + ":0dB"));
                // 静音：off
                RaiseDigital((ushort)(FbInMute + ch - 1), 0);
                RaiseDigital((ushort)(FbOutMute + ch - 1), 0);
                // 音量表：0（熄灭）
                RaiseAnalog((ushort)(AfbInMeter + ch - 1), 0);
                RaiseAnalog((ushort)(AfbOutMeter + ch - 1), 0);
            }
        }

        // =====================================================================
        //  订阅（替代轮询：设备变化主动推送）
        // =====================================================================
        private void StartResubTimer()
        {
            CancelResubTimer();
            // 周期重订阅（5 分钟）：Tesira 在 reboot / 配置变更后订阅会丢失，用同标签重订阅可重验证（不产生重复）
            _resubTimer = new CTimer(o => { if (_state == ConnState.Ready) SubscribeAll(); }, null, 300000, 300000);
        }

        private void SubscribeAll()
        {
            CrestronConsole.PrintLine("[Tesira] subscribing (channels={0})...", _channels);
            // 电平：整块 subscribe levels（返回数组）
            if (_inLevelTag.Length > 0) SendCommand(_inLevelTag + " subscribe levels " + LblInLevel + " " + ThrottleLevel);
            if (_outLevelTag.Length > 0) SendCommand(_outLevelTag + " subscribe levels " + LblOutLevel + " " + ThrottleLevel);
            // 静音：整块 subscribe mutes（返回数组）
            if (_inLevelTag.Length > 0) SendCommand(_inLevelTag + " subscribe mutes " + LblInMute + " " + ThrottleMute);
            if (_outLevelTag.Length > 0) SendCommand(_outLevelTag + " subscribe mutes " + LblOutMute + " " + ThrottleMute);
            // 信号表：整块 subscribe levels（返回数组）
            if (_inMeterTag.Length > 0) SendCommand(_inMeterTag + " subscribe levels " + LblInMeter + " " + ThrottleMeter);
            if (_outMeterTag.Length > 0) SendCommand(_outMeterTag + " subscribe levels " + LblOutMeter + " " + ThrottleMeter);
            // 交叉点：逐点订阅（Matrix Mixer 的 crosspointLevelStateAll 不支持 subscribe/get）
            for (int o = 1; o <= _channels; o++)
                for (int i = 1; i <= _channels; i++)
                    SendCommand(_mixerTag + " subscribe crosspointLevelState " + i + " " + o + " "
                        + LblXp + i + "_" + o + " " + ThrottleXp);
            CrestronConsole.PrintLine("[Tesira] subscribe done");
        }

        // =====================================================================
        //  命令接口（SIMPL+ 调用，与 StageCraft 对齐）
        // =====================================================================
        public void SetInputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > _channels) return;
            _inMute[ch] = mute != 0;
            SendCommand(_inLevelTag + " set mute " + ch + " " + (mute != 0 ? "true" : "false"));
            RaiseDigital((ushort)(FbInMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
            // mute 时立即熄灭音量表（不等订阅推送，避免时序问题）
            if (mute != 0)
            {
                _lastMeterAnalog[ch] = 0;
                RaiseAnalog((ushort)(AfbInMeter + ch - 1), 0);
            }
        }

        public void ToggleInputMute(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SetInputMute(ch, (ushort)(_inMute[ch] ? 0 : 1));
        }

        public void SetOutputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > _channels) return;
            _outMute[ch] = mute != 0;
            SendCommand(_outLevelTag + " set mute " + ch + " " + (mute != 0 ? "true" : "false"));
            RaiseDigital((ushort)(FbOutMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
            // mute 时立即熄灭音量表
            if (mute != 0)
            {
                _lastMeterAnalog[ch] = 0;
                RaiseAnalog((ushort)(AfbOutMeter + ch - 1), 0);
            }
        }

        public void ToggleOutputMute(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SetOutputMute(ch, (ushort)(_outMute[ch] ? 0 : 1));
        }

        public void AllMute(ushort mute)
        {
            for (ushort ch = 1; ch <= _channels; ch++)
                SetOutputMute(ch, mute);
            RaiseDigital(FbAllMute, mute);
        }

        // ---- 电平调节 ----
        public void InputLevelAdd(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_inLevelTag + " increment level " + ch + " 1");
        }
        public void InputLevelSub(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_inLevelTag + " decrement level " + ch + " 1");
        }
        public void OutputLevelAdd(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_outLevelTag + " increment level " + ch + " 1");
        }
        public void OutputLevelSub(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_outLevelTag + " decrement level " + ch + " 1");
        }

        /// <summary>设置输入电平（dB，可负）。</summary>
        public void SetInputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_inLevelTag + " set level " + ch + " " + db);
        }

        /// <summary>设置输出电平（dB，可负）。</summary>
        public void SetOutputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > _channels) return;
            SendCommand(_outLevelTag + " set level " + ch + " " + db);
        }

        public void SetInputLevelAnalog(ushort ch, ushort analog) { SetInputLevel(ch, AnalogToDb(analog)); }
        public void SetOutputLevelAnalog(ushort ch, ushort analog) { SetOutputLevel(ch, AnalogToDb(analog)); }

        // ---- 混音路由 ----
        /// <summary>选中输出通道并刷新其路由高亮（路由状态已由订阅实时维护）。</summary>
        public void SelectOutput(ushort outCh)
        {
            if (outCh < 1 || outCh > _channels) return;
            SelectedOut = outCh;
            for (int i = 1; i <= _channels; i++)
            {
                RaiseDigital((ushort)(FbMixOut + i - 1), (ushort)(i == outCh ? 1 : 0));
                RaiseDigital((ushort)(FbMixIn + i - 1), (ushort)(_route[outCh, i] ? 1 : 0));
            }
        }

        /// <summary>按下输入通道：切换该交叉点路由。</summary>
        public void ToggleRoute(ushort inCh)
        {
            if (inCh < 1 || inCh > _channels || SelectedOut < 1) return;
            SelectedIn = inCh;
            SetRoute(SelectedOut, inCh, (ushort)(_route[SelectedOut, inCh] ? 0 : 1));
        }

        /// <summary>直接设置交叉点路由：1=连接，0=断开。</summary>
        public void SetRoute(ushort outCh, ushort inCh, ushort on)
        {
            if (outCh < 1 || outCh > _channels || inCh < 1 || inCh > _channels) return;
            _route[outCh, inCh] = on != 0;
            // Tesira 矩阵混音：crosspointLevelState <输入(行)> <输出(列)> true/false
            SendCommand(_mixerTag + " set crosspointLevelState " + inCh + " " + outCh + " " + (on != 0 ? "true" : "false"));
            if (outCh == SelectedOut)
                RaiseDigital((ushort)(FbMixIn + inCh - 1), on);
        }

        /// <summary>刷新某输出通道的路由高亮（订阅已实时维护路由，此处仅从缓存重推）。</summary>
        public void ReadMixRoute(ushort outCh)
        {
            if (outCh < 1 || outCh > _channels) return;
            SelectedOut = outCh;
            for (int i = 1; i <= _channels; i++)
                RaiseDigital((ushort)(FbMixIn + i - 1), (ushort)(_route[outCh, i] ? 1 : 0));
        }

        // ---- 预设 ----
        public void LoadPreset(ushort n) { SendCommand("DEVICE recallPreset " + n); }
        public void LoadPresetByName(SimplSharpString name)
        {
            if (name == null) return;
            SendCommand("DEVICE recallPresetByName \"" + name.ToString() + "\"");
        }

        // ---- 兼容旧 .usp（订阅模式已替代轮询；保留空实现避免旧薄壳调用时报方法未找到）----
        public void StartLevelPolling(ushort intervalMs)
        {
            CrestronConsole.PrintLine("[Tesira] StartLevelPolling ignored (subscription mode active)");
        }
        public void StopLevelPolling() { }
        /// <summary>订阅模式无需轮询，空实现（与 StageCraft 的页面驱动轮询接口保持一致）。</summary>
        public void SetPollMode(ushort mode) { }

        // =====================================================================
        //  内部：发送 / 订阅推送解析 / 回馈
        // =====================================================================
        private void SendCommand(string cmd)
        {
            SendRaw(Encoding.ASCII.GetBytes(cmd + "\r\n"));
        }

        private void SendRaw(byte[] data)
        {
            try
            {
                lock (_sendLock)
                {
                    if (_client == null || !_connected)
                    {
                        if (VerboseLog) CrestronConsole.PrintLine("[Tesira] WARN send skipped (not connected)");
                        return;
                    }
                    SocketErrorCodes err = _client.SendData(data, data.Length);
                    if (err != SocketErrorCodes.SOCKET_OK)
                        CrestronConsole.PrintLine("[Tesira] WARN send err={0}", err);
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Tesira] send EXCEPTION: {0}", ex.Message);
            }
        }

        /// <summary>解析订阅推送：! "publishToken":"&lt;标签&gt;" "value":&lt;值&gt;。</summary>
        private void HandlePublish(string label, string value)
        {
            // 交叉点：xp<in>_<out>
            Match xm = RxXp.Match(label);
            if (xm.Success)
            {
                int inCh, outCh;
#if SERIES3
                if (!TryParseInt(xm.Groups[1].Value, out inCh) || !TryParseInt(xm.Groups[2].Value, out outCh))
#else
                if (!int.TryParse(xm.Groups[1].Value, out inCh) || !int.TryParse(xm.Groups[2].Value, out outCh))
#endif
                    return;
                if (outCh < 1 || outCh > _channels || inCh < 1 || inCh > _channels) return;
                bool on = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                _route[outCh, inCh] = on;
                if (outCh == SelectedOut)
                    RaiseDigital((ushort)(FbMixIn + inCh - 1), (ushort)(on ? 1 : 0));
                return;
            }

            // 整块订阅：value 是数组（或单值兜底）
            Match arr = RxArray.Match(value);
#if SERIES3
            string[] parts = arr.Success ? SplitTokens(arr.Groups[1].Value) : new string[] { value };
#else
            string[] parts = arr.Success
                ? arr.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { value };
#endif

            if (label == LblInLevel) ParseLevelArray(parts, AfbInLevel, SfbInLevelText, true);
            else if (label == LblOutLevel) ParseLevelArray(parts, AfbOutLevel, SfbOutLevelText, false);
            else if (label == LblInMute) ParseMuteArray(parts, _inMute, FbInMute);
            else if (label == LblOutMute) ParseMuteArray(parts, _outMute, FbOutMute);
            else if (label == LblInMeter) ParseMeterArray(parts, AfbInMeter, true);
            else if (label == LblOutMeter) ParseMeterArray(parts, AfbOutMeter, false);
            // 未知标签忽略
        }

        private void ParseLevelArray(string[] parts, ushort analogBase, ushort textBase, bool isInput)
        {
            for (int i = 0; i < parts.Length && i < _channels; i++)
            {
                double d;
#if SERIES3
                if (!TryParseDouble(parts[i], out d)) continue;
#else
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) continue;
#endif
                int db = (int)Math.Round(d);
                ushort ch = (ushort)(i + 1);
                UpdateLevelFb(ch, db, analogBase, textBase, isInput);
            }
        }

        private void ParseMeterArray(string[] parts, ushort fbBase, bool isInput)
        {
            for (int i = 0; i < parts.Length && i < _channels; i++)
            {
                double d;
#if SERIES3
                if (!TryParseDouble(parts[i], out d)) continue;
#else
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) continue;
#endif
                ushort ch = (ushort)(i + 1);
                if ((isInput && _inMute[ch]) || (!isInput && _outMute[ch]))
                {
                    // mute 时归零（无条件推，确保熄灭）
                    _lastMeterAnalog[ch] = 0;
                    RaiseAnalog((ushort)(fbBase + i), 0);
                }
                else
                {
                    // meter 不做 dirty check：实时信号强度显示，信号停了（值稳定不变）也必须推——
                    // 否则 VTP 电平条会卡在最后的值不动（dirty check 误杀"无信号"的固定值）
                    int analog = MeterDbToAnalog(d);
                    _lastMeterAnalog[ch] = analog;
                    RaiseAnalog((ushort)(fbBase + i), (ushort)analog);
                }
            }
        }

        private void ParseMuteArray(string[] parts, bool[] states, ushort fbBase)
        {
            for (int i = 0; i < parts.Length && i < _channels; i++)
            {
                bool on = parts[i].Equals("true", StringComparison.OrdinalIgnoreCase);
                // dirty check：状态没变不 Raise
                if (states[i + 1] == on) continue;
                states[i + 1] = on;
                RaiseDigital((ushort)(fbBase + i), (ushort)(on ? 1 : 0));
            }
        }

        private void UpdateLevelFb(ushort ch, int db, ushort analogBase, ushort textBase, bool isInput)
        {
            if (ch < 1 || ch > _channels) return;
            // 推子始终反映增益 dB 位置：静音不把推子拉到底（与 StageCraft 一致）。
            // 静音由「数字反馈灯 + 音量表熄灭(SetInputMute/ParseMeterArray)」体现。
            ushort analog = DbToAnalog(db);
            if (_lastLevelAnalog[ch] == analog) return;   // dirty check
            _lastLevelAnalog[ch] = analog;
            RaiseAnalog((ushort)(analogBase + ch - 1), analog);
            RaiseSerial((ushort)(textBase + ch - 1),
                new SimplSharpString(ch + ":" + (db < 0 ? "-" : "") + Math.Abs(db) + "dB"));
        }

        // ---------------- 换算与回报 ----------------
        /// <summary>增益 dB → 模拟量（53928 = 0dB）。</summary>
        public static ushort DbToAnalog(int db)
        {
            int v = AnalogMid + db * DbPerStep;
            if (v < 0) v = 0;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

        /// <summary>模拟量 → 增益 dB（四舍五入）。</summary>
        public static int AnalogToDb(ushort analog)
        {
            return (int)Math.Round((analog - AnalogMid) / (double)DbPerStep);
        }

        /// <summary>音量表(VU) dB → 模拟量：-60dB→0，+12dB→65535 线性。internal：仅类内部换算用，不对外暴露（double 参数不属于 SIMPL+ 类型系统）。</summary>
        internal static ushort MeterDbToAnalog(double db)
        {
            double v = (db - MeterDbMin) * 65535.0 / (MeterDbMax - MeterDbMin);
            if (v < 0) v = 0;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

        /// <summary>设置连接状态并（仅在变化时）触发 ConnectionStateChanged，供冗余控制器做 leader 选举。</summary>
        private void SetConnected(bool online)
        {
            if (_connected == online) return;
            _connected = online;
            var h = ConnectionStateChanged;
            if (h != null) h(online);
        }

        private void RaiseDigital(ushort id, ushort value)
        {
            if (DigitalFb != null) DigitalFb(id, value);
        }

        private void RaiseAnalog(ushort id, ushort value)
        {
            if (AnalogFb != null)
            {
                if (VerboseLog) CrestronConsole.PrintLine("[Tesira] RaiseAnalog id={0} v={1}", id, value);
                AnalogFb(id, value);
            }
            else if (VerboseLog)
                CrestronConsole.PrintLine("[Tesira] RaiseAnalog SKIPPED (AnalogFb==null) id={0} v={1}", id, value);
        }

        private void RaiseSerial(ushort id, SimplSharpString text)
        {
            if (SerialFb != null) SerialFb(id, text);
        }

#if SERIES3
        // ---- .NET CF 3.5 兼容辅助（3代无 Encoding.GetString(byte[]) 单参 / int.TryParse / double.TryParse / StringSplitOptions）----
        private static string BytesToString(byte[] b) { return Encoding.ASCII.GetString(b, 0, b.Length); }
        private static bool TryParseInt(string s, out int r) { try { r = int.Parse(s); return true; } catch { r = 0; return false; } }
        private static bool TryParseDouble(string s, out double r)
        {
            try { r = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); return true; }
            catch { r = 0; return false; }
        }
        private static string[] SplitTokens(string s)
        {
            string[] raw = s.Split(new char[] { ' ', '\t' });
            int n = 0; for (int i = 0; i < raw.Length; i++) if (raw[i].Length > 0) n++;
            string[] o = new string[n]; int j = 0;
            for (int i = 0; i < raw.Length; i++) if (raw[i].Length > 0) o[j++] = raw[i];
            return o;
        }
#endif
    }
}
