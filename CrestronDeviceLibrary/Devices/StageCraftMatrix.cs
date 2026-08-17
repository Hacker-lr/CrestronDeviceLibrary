using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// StageCraft 16x16 音频矩阵处理器（TCP 端口 1698）。
    ///
    /// 协议分两种（同一条 TCP 连接）：
    ///   1. ASCII 文本命令，以 '#' 结尾 —— 电平调节、静音、预设。
    ///      例：L1_Mute 1# / L2_add 3# / SetL1 2:-10# / LOADP 3#
    ///   2. 二进制帧：0x82 0x7d [类型] [数据] 0x7d 0x82 —— 混音路由、音量表。
    ///
    /// 本类职责：命令组包 + 应答解析（C# 正则/位处理，替代 SIMPL+ 吃力且易卡死的字符串解析）。
    /// SIMPL+ 薄壳负责引脚接线；应答经 DigitalFb/AnalogFb/SerialFb 委托推回。
    ///
    /// 【连接方式】C# 直接用 TCPClient 连设备（Configure 设 IP/端口，Start 建立连接）。
    /// 不用 SIMPL+ 的 audio_tx$/audio_rx$ 串口转发，因为二进制命令含 0x00 字节，
    /// SIMPL+ 的 STRING/BUFFER 是 NULL 结尾，遇 0x00 会抛 StringBuilder 异常。
    ///
    /// 已验证功能：音量加减、静音、混音路由、输入/输出电平与音量表实时反馈。
    /// </summary>
    public class StageCraftMatrix
    {
        // ---------- 常量 ----------
        public const ushort Channels = 16;
        public const ushort AnalogMid = 53928;   // 模拟量 0dB 中点
        public const ushort DbPerStep = 963;     // 每 dB 的模拟量刻度

        // 反馈 id 约定（SIMPL+ 端按此路由到引脚；数字/模拟/串口分开编号）
        public const ushort FbInMute = 1;         // 1..16   输入静音（数字）
        public const ushort FbOutMute = 17;       // 17..32  输出静音（数字）
        public const ushort FbMixIn = 33;         // 33..48  混音输入路由（数字）
        public const ushort FbMixOut = 49;        // 49..64  混音输出选择（数字）
        public const ushort FbMode1 = 65;         // 65..69  模式 1..5（数字）
        public const ushort FbAllMute = 70;       // 全部静音（数字）
        public const ushort FbInLevel = 1;        // 1..16   输入电平（模拟，.usp 数组下标）
        public const ushort FbOutLevel = 17;      // 17..32  输出电平（模拟）
        public const ushort FbInMeter = 33;       // 33..48  输入音量表（模拟）
        public const ushort FbOutMeter = 49;      // 49..64  输出音量表（模拟）
        public const ushort FbInLevelText = 1;    // 1..16   输入电平显示串（串口，数组下标）
        public const ushort FbOutLevelText = 17;  // 17..32  输出电平显示串（串口）

        // 调试开关：true 打印详细应答/反馈日志（调试时打开，正式部署关闭避免刷屏）
        // 用 static readonly（而非 const）避免编译器把 if(VerboseLog) 判定为"不可达代码"产生 CS0162 警告
        private static readonly bool VerboseLog = false;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);   // 字节<->字符 保真（0x80-0xFF 不被截断）
        private static readonly Regex RxL1Mute = new Regex(@"L1Mute:([01]{16})");
        private static readonly Regex RxL2Mute = new Regex(@"L2Mute:([01]{16})");
        private static readonly Regex RxPreLevel = new Regex(@"PreLevel\s+(\d+):(-?\d+(?:\.\d+)?)dB");
        private static readonly Regex RxPostLevel = new Regex(@"PostLevel\s+(\d+):(-?\d+(?:\.\d+)?)dB");

        // ---------- 输出委托（SIMPL+ 用 RegisterDelegate 订阅） ----------
        /// <summary>数字反馈：id 见 Fb* 常量。</summary>
        public delegate void DigitalFbDelegate(ushort id, ushort value);
        public DigitalFbDelegate DigitalFb { get; set; }

        /// <summary>模拟反馈：id 见 Fb* 常量。</summary>
        public delegate void AnalogFbDelegate(ushort id, ushort value);
        public AnalogFbDelegate AnalogFb { get; set; }

        /// <summary>串口反馈（显示文本）：id 见 Fb* 常量。</summary>
        public delegate void SerialFbDelegate(ushort id, SimplSharpString text);
        public SerialFbDelegate SerialFb { get; set; }

        // ---------- 状态 ----------
        public ushort SelectedOut { get; private set; }  // 混音当前选中的输出通道
        public ushort SelectedIn { get; private set; }   // 混音当前选中的输入通道

        private readonly bool[] _inMute = new bool[Channels + 1];
        private readonly bool[] _outMute = new bool[Channels + 1];
        private readonly bool[,] _route = new bool[Channels + 1, Channels + 1]; // [输出, 输入]

        // 应答缓冲
        private readonly List<byte> _rxBytes = new List<byte>(1024);

        // TCP 直连（绕过 SIMPL+ 串口转发，因为二进制命令含 0x00）
        private TCPClient _client;
        private string _ip = "192.168.0.222";
        private int _port = 1698;
        private bool _connected;
        private readonly object _sendLock = new object();
        private CTimer _reconnectTimer;

        // 周期轮询（电平/音量表/静音实时刷新）
        private CTimer _pollTimer;
        private int _tick;   // 轮询周期计数（用于每 N 周期刷新一次静音状态）

        // ---------- 连接配置与建立（SIMPL+ 在 Main 里调用） ----------
        /// <summary>
        /// 设置设备 TCP 地址。IP 为字符串（"192.168.0.222"），端口为整数。
        /// SIMPL+ 在 Main 里调一次：IP 用 #DEFINE_CONSTANT DEVICE_IP（字符串常量），
        /// 端口用 #DEFINE_CONSTANT DEVICE_PORT。若运行时再调用（IP/端口变化），
        /// 本方法检测到变化会自动断开重连。
        /// </summary>
        public void Configure(SimplSharpString ip, ushort port)
        {
            string s = (ip != null) ? ip.ToString() : "";
            string newIp = !string.IsNullOrEmpty(s) ? s.Trim() : _ip;
            int newPort = port > 0 ? port : _port;
            bool changed = (newIp != _ip) || (newPort != _port);
            _ip = newIp;
            _port = newPort;
            CrestronConsole.PrintLine("[StageCraft] Configure ip={0} port={1} (changed={2})", _ip, _port, changed);
            if (changed && _client != null)
            {
                CrestronConsole.PrintLine("[StageCraft] IP/port changed, reconnecting...");
                Stop();
                ConnectAsync();
            }
        }

        /// <summary>开始连接设备（异步，成功后自动登录 + 开始接收）。</summary>
        public void Start()
        {
            ConnectAsync();
        }

        /// <summary>断开连接并停止重连。</summary>
        public void Stop()
        {
            CancelReconnect();
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
                CrestronConsole.PrintLine("[StageCraft] Stop EXCEPTION: {0}", ex.Message);
            }
            _connected = false;
        }

        private void ConnectAsync()
        {
            CancelReconnect();
            try
            {
                CrestronConsole.PrintLine("[StageCraft] TCP connecting {0}:{1} ...", _ip, _port);
                _client = new TCPClient(_ip, _port, 4096);
                _client.SocketStatusChange += OnSocketStatusChange;
                SocketErrorCodes err = _client.ConnectToServerAsync(OnConnectComplete);
                if (err != SocketErrorCodes.SOCKET_OPERATION_PENDING && err != SocketErrorCodes.SOCKET_OK)
                {
                    CrestronConsole.PrintLine("[StageCraft] TCP connect async err={0}", err);
                    ScheduleReconnect();
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[StageCraft] TCP connect EXCEPTION: {0}", ex.Message);
                ScheduleReconnect();
            }
        }

        private void OnConnectComplete(TCPClient client)
        {
            if (client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                _connected = true;
                CrestronConsole.PrintLine("[StageCraft] v3.0 ONLINE - TCP connected {0}:{1}", _ip, _port);
                client.ReceiveDataAsync(OnReceiveData);
                // 二进制登录包：功能码01 子功能01 长度05 "admin"
                SendBinary(0x01, 0x01, 0x01, 0x05, 0x00, 0x61, 0x64, 0x6d, 0x69, 0x6e);
            }
            else
            {
                CrestronConsole.PrintLine("[StageCraft] TCP connect failed status={0}", client.ClientStatus);
                ScheduleReconnect();
            }
        }

        private void OnSocketStatusChange(TCPClient client, SocketStatus status)
        {
            CrestronConsole.PrintLine("[StageCraft] TCP status -> {0}", status);
            switch (status)
            {
                case SocketStatus.SOCKET_STATUS_CONNECTED:
                    _connected = true;
                    break;
                case SocketStatus.SOCKET_STATUS_LINK_LOST:
                case SocketStatus.SOCKET_STATUS_BROKEN_LOCALLY:
                case SocketStatus.SOCKET_STATUS_BROKEN_REMOTELY:
                case SocketStatus.SOCKET_STATUS_NO_CONNECT:
                case SocketStatus.SOCKET_STATUS_CONNECT_FAILED:
                    _connected = false;
                    ScheduleReconnect();
                    break;
                // 中间状态（WAITING / DNS_LOOKUP / DNS_RESOLVED 等）忽略，不改变连接态
            }
        }

        private void OnReceiveData(TCPClient client, int numberOfBytes)
        {
            if (numberOfBytes <= 0)
            {
                CrestronConsole.PrintLine("[StageCraft] TCP closed by remote (len={0})", numberOfBytes);
                _connected = false;
                ScheduleReconnect();
                return;
            }
            try
            {
                byte[] src = client.IncomingDataBuffer;
                byte[] data = new byte[numberOfBytes];
                Array.Copy(src, data, numberOfBytes);
                _rxBytes.AddRange(data);
                if (VerboseLog) CrestronConsole.PrintLine("[StageCraft] RX {0} bytes (total={1})", numberOfBytes, _rxBytes.Count);
                ProcessBuffer();
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[StageCraft] OnReceiveData EXCEPTION: {0}", ex.Message);
            }
            client.ReceiveDataAsync(OnReceiveData);
        }

        private void ScheduleReconnect()
        {
            if (_reconnectTimer != null) return;
            CrestronConsole.PrintLine("[StageCraft] schedule reconnect in 5s");
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

        // =====================================================================
        //  静音（已验证）
        // =====================================================================
        public void SetInputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > Channels) return;
            _inMute[ch] = mute != 0;
            string cmd = (mute != 0 ? "L1_Mute " : "L1_UnMute ") + ch + "#";
            CrestronConsole.PrintLine("[StageCraft] Mute IN ch={0} -> {1}", ch, cmd);
            SendAscii(cmd);
            RaiseDigital((ushort)(FbInMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
        }

        public void ToggleInputMute(ushort ch)
        {
            if (ch < 1 || ch > Channels) return;
            SetInputMute(ch, (ushort)(_inMute[ch] ? 0 : 1));
        }

        public void SetOutputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > Channels) return;
            _outMute[ch] = mute != 0;
            SendAscii((mute != 0 ? "L2_Mute " : "L2_UnMute ") + ch + "#");
            RaiseDigital((ushort)(FbOutMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
        }

        public void ToggleOutputMute(ushort ch)
        {
            if (ch < 1 || ch > Channels) return;
            SetOutputMute(ch, (ushort)(_outMute[ch] ? 0 : 1));
        }

        /// <summary>全部输出静音（allmute 按钮）：1=开，0=关。</summary>
        public void AllMute(ushort mute)
        {
            for (ushort ch = 1; ch <= Channels; ch++)
                SetOutputMute(ch, mute);
            RaiseDigital(FbAllMute, mute);
        }

        // =====================================================================
        //  电平调节（已验证）
        // =====================================================================
        public void InputLevelAdd(ushort ch)  { if (ch < 1 || ch > Channels) return; SendAscii("L1_add " + ch + "#"); }
        public void InputLevelSub(ushort ch)  { if (ch < 1 || ch > Channels) return; SendAscii("L1_sub " + ch + "#"); }
        public void OutputLevelAdd(ushort ch) { if (ch < 1 || ch > Channels) return; SendAscii("L2_add " + ch + "#"); }
        public void OutputLevelSub(ushort ch) { if (ch < 1 || ch > Channels) return; SendAscii("L2_sub " + ch + "#"); }

        /// <summary>设置输入电平（dB，可负）。例 SetInputLevel(2, -10)。</summary>
        public void SetInputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > Channels) return;
            SendAscii(db >= 0 ? "SetL1 " + ch + ":" + db + "#" : "SetL1 " + ch + ":-" + (-db) + "#");
        }

        /// <summary>设置输出电平（dB，可负）。</summary>
        public void SetOutputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > Channels) return;
            SendAscii(db >= 0 ? "SetL2 " + ch + ":" + db + "#" : "SetL2 " + ch + ":-" + (-db) + "#");
        }

        /// <summary>模拟量输入 → dB → 发送（触屏推子）。模拟量 53928 = 0dB。</summary>
        public void SetInputLevelAnalog(ushort ch, ushort analog)
        {
            SetInputLevel(ch, AnalogToDb(analog));
        }

        /// <summary>模拟量输入 → dB → 发送（触屏推子）。</summary>
        public void SetOutputLevelAnalog(ushort ch, ushort analog)
        {
            SetOutputLevel(ch, AnalogToDb(analog));
        }

        // =====================================================================
        //  混音路由（已验证）
        // =====================================================================
        /// <summary>选中输出通道并读取其路由状态。</summary>
        public void SelectOutput(ushort outCh)
        {
            if (outCh < 1 || outCh > Channels) return;
            SelectedOut = outCh;
            // 清所有混音选择/路由反馈，点亮当前输出
            for (ushort i = 1; i <= Channels; i++)
            {
                RaiseDigital((ushort)(FbMixOut + i - 1), (ushort)(i == outCh ? 1 : 0));
                RaiseDigital((ushort)(FbMixIn + i - 1), 0);
            }
            ReadMixRoute(outCh);
        }

        /// <summary>按下输入通道：切换该交叉点路由（连接/断开）。</summary>
        public void ToggleRoute(ushort inCh)
        {
            if (inCh < 1 || inCh > Channels || SelectedOut < 1) return;
            SelectedIn = inCh;
            bool on = !_route[SelectedOut, inCh];
            SetRoute(SelectedOut, inCh, (ushort)(on ? 1 : 0));
        }

        /// <summary>直接设置交叉点路由：1=连接，0=断开。</summary>
        public void SetRoute(ushort outCh, ushort inCh, ushort on)
        {
            if (outCh < 1 || outCh > Channels || inCh < 1 || inCh > Channels) return;
            _route[outCh, inCh] = on != 0;
            // 二进制写路由：82 7d 01 00 03 08 01 01 03 00 [out-1] [in-1] [01/00] 7d 82
            SendBinary(0x01, 0x00, 0x03, 0x08, 0x01, 0x01, 0x03, 0x00,
                       (byte)(outCh - 1), (byte)(inCh - 1), (byte)(on != 0 ? 0x01 : 0x00));
            RaiseDigital((ushort)(FbMixIn + inCh - 1), on);
        }

        /// <summary>读取某输出通道的路由状态（应答驱动反馈）。</summary>
        public void ReadMixRoute(ushort outCh)
        {
            if (outCh < 1 || outCh > Channels) return;
            // 二进制读路由：82 7d 01 00 03 08 01 00 01 00 [out-1] 7d 82
            SendBinary(0x01, 0x00, 0x03, 0x08, 0x01, 0x00, 0x01, 0x00, (byte)(outCh - 1));
        }

        // =====================================================================
        //  预设 / 音量表（未上机验证）
        // =====================================================================
        public void LoadPreset(ushort n) { SendAscii("LOADP " + n + "#"); }

        public void ReadInputMeter()
        {
            // 82 7d 00 00 03 07 07 00 00 00 7d 82（功能码07 子功能07 = 输入音量表）
            SendBinary(0x00, 0x00, 0x03, 0x07, 0x07, 0x00, 0x00, 0x00);
        }

        public void ReadOutputMeter()
        {
            // 82 7d 00 00 03 0b 01 00 00 00 7d 82（功能码0b 子功能01 = 输出音量表）
            SendBinary(0x00, 0x00, 0x03, 0x0b, 0x01, 0x00, 0x00, 0x00);
        }

        // =====================================================================
        //  周期轮询：实时电平 / 音量表 / 静音刷新
        //  CTimer 在 C# 定时器线程跑，不占 SIMPL+ 线程 —— 这是替代"SIMPL+
        //  每 250ms 轮询导致卡死"的关键。SIMPL+ 薄壳只需转发命令与回馈。
        // =====================================================================
        /// <summary>
        /// 启动周期轮询。intervalMs 为轮询周期（建议 200~500）。
        /// 每个周期：刷新输入/输出音量表（二进制）+ 全部 16 路输入/输出电平（ASCII ReadL1/ReadL2）
        /// + 每 20 个周期刷新一次静音状态。调用前须已 RegisterDelegate。
        /// </summary>
        public void StartLevelPolling(ushort intervalMs)
        {
            if (_pollTimer != null) return;
            if (intervalMs < 100) intervalMs = 100;
            _tick = 0;
            _pollTimer = new CTimer(PollTick, null, intervalMs, intervalMs);
        }

        /// <summary>停止周期轮询。</summary>
        public void StopLevelPolling()
        {
            if (_pollTimer == null) return;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _pollTimer = null;
        }

        private void PollTick(object userobj)
        {
            _tick++;

            // 1) 音量表：每周期都刷（最实时）
            ReadInputMeter();
            ReadOutputMeter();

            // 2) 电平：每周期查全部 16 路输入 + 16 路输出
            //    之前分片 4 周期（1 秒）才刷完 16 路，电平最坏延迟 1 秒；
            //    现在 250ms 内全部刷完，最坏延迟 250ms。
            //    命令量从 8 个/周期 增到 32 个/周期，仍远在音频矩阵承受范围内。
            var sb = new StringBuilder();
            for (ushort ch = 1; ch <= Channels; ch++)
            {
                sb.Append("ReadL1 ").Append(ch).Append('#');
                sb.Append("ReadL2 ").Append(ch).Append('#');
            }
            SendAscii(sb.ToString());

            // 3) 静音：每 20 周期刷新一次（250ms 周期下约 5s）
            if (_tick % 20 == 0)
            {
                ReadInputMutes();
                ReadOutputMutes();
            }
        }

        // =====================================================================
        //  查询（ASCII，应答解析后回馈）
        // =====================================================================
        public void ReadInputMutes()  { SendAscii("ReadL1 Mute#"); }
        public void ReadOutputMutes() { SendAscii("ReadL2 Mute#"); }

        public void ReadAllInputLevels()
        {
            var sb = new StringBuilder();
            for (ushort i = 1; i <= Channels; i++) sb.Append("ReadL1 ").Append(i).Append('#');
            SendAscii(sb.ToString());
        }

        public void ReadAllOutputLevels()
        {
            var sb = new StringBuilder();
            for (ushort i = 1; i <= Channels; i++) sb.Append("ReadL2 ").Append(i).Append('#');
            SendAscii(sb.ToString());
        }

        // =====================================================================
        //  内部：组包 / 缓冲分帧 / 应答解析
        // =====================================================================
        private void SendAscii(string cmd)
        {
            SendRaw(Latin1.GetBytes(cmd));
        }

        private void SendBinary(params byte[] payload)
        {
            // 帧格式：82 7d [payload] 7d 82
            byte[] frame = new byte[payload.Length + 4];
            frame[0] = 0x82; frame[1] = 0x7d;
            Array.Copy(payload, 0, frame, 2, payload.Length);
            frame[frame.Length - 2] = 0x7d; frame[frame.Length - 1] = 0x82;
            SendRaw(frame);
        }

        /// <summary>直连发送（加锁线程安全）。未连接时打日志跳过，不抛异常。</summary>
        private void SendRaw(byte[] data)
        {
            try
            {
                lock (_sendLock)
                {
                    if (_client == null || !_connected)
                    {
                        CrestronConsole.PrintLine("[StageCraft] WARN send skipped (not connected) len={0}", data.Length);
                        return;
                    }
                    SocketErrorCodes err = _client.SendData(data, data.Length);
                    if (err != SocketErrorCodes.SOCKET_OK)
                        CrestronConsole.PrintLine("[StageCraft] WARN send err={0}", err);
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[StageCraft] send EXCEPTION: {0}", ex.Message);
            }
        }

/// <summary>
/// 从缓冲中取出完整帧。二进制帧（0x82...0x82）与 ASCII 文本（PreLevel/PostLevel/Mute）
/// 分开处理：先按 0x82 切走二进制帧，剩余纯 ASCII 文本再正则解析。
/// 关键：不能像旧版那样"匹配到 ASCII 就 Clear 整个缓冲"——会把粘连在里面的
/// 二进制 Meter 帧（尤其输出 func=0b）也误删，导致输出电平条不实时更新（30 秒才 6 条）。
/// 孤立 0x82（不完整二进制帧头）时 break 等更多数据，ASCII 文本仍会被单独消费，不会卡死。
/// </summary>
        private void ProcessBuffer()
        {
            int guard = 0;   // 防止死循环
            while (_rxBytes.Count > 0 && guard++ < 300)
            {
                int binStart = _rxBytes.IndexOf(0x82);

                if (binStart < 0)
                {
                    // 无二进制帧：整段 ASCII 文本解析后清空
                    ParseAsciiText(_rxBytes);
                    _rxBytes.Clear();
                    continue;
                }
                else if (binStart == 0)
                {
                    // 开头就是二进制帧头，找帧尾（下一个 0x82）
                    int binEnd = _rxBytes.IndexOf(0x82, 1);
                    if (binEnd > 1)
                    {
                        var bframe = _rxBytes.GetRange(0, binEnd + 1);
                        _rxBytes.RemoveRange(0, binEnd + 1);
                        ParseBinaryFrame(bframe);
                        continue;
                    }
                    // 孤立帧头（0x82 后无帧尾）：不完整，等更多数据
                    break;
                }
                else
                {
                    // 前面有 ASCII 文本，先切 ASCII（到 binStart 之前），再回来切二进制
                    var asciiPart = _rxBytes.GetRange(0, binStart);
                    _rxBytes.RemoveRange(0, binStart);
                    ParseAsciiText(asciiPart);
                    continue;
                }
            }

            // 防累积卡死：缓冲过大（正常单次应答 < 1KB）就清空，避免脏数据永久阻塞
            if (_rxBytes.Count > 8192)
            {
                CrestronConsole.PrintLine("[StageCraft] WARN buffer overflow {0}B, clear", _rxBytes.Count);
                _rxBytes.Clear();
            }
        }

        /// <summary>
        /// 解析一段纯 ASCII 文本（PreLevel/PostLevel/L1Mute/L2Mute，可能多条粘连，
        /// 如 "PreLevel 1:-10dBPreLevel 2:5dB"）。用 Matches 全量匹配，不消费缓冲。
        /// </summary>
        private void ParseAsciiText(List<byte> bytes)
        {
            if (bytes.Count == 0) return;
            string text = Latin1.GetString(bytes.ToArray());

            // L1Mute:01001010...（16位）
            foreach (Match m in RxL1Mute.Matches(text))
                UpdateMuteFb(m.Groups[1].Value, _inMute, FbInMute);
            // L2Mute:01001010...
            foreach (Match m in RxL2Mute.Matches(text))
                UpdateMuteFb(m.Groups[1].Value, _outMute, FbOutMute);
            // PreLevel {ch}:{±}XX.XdB（输入电平，支持小数如 5.0 / -4.0）
            foreach (Match m in RxPreLevel.Matches(text))
            {
                ushort ch = ushort.Parse(m.Groups[1].Value);
                int db = (int)Math.Round(double.Parse(m.Groups[2].Value));
                UpdateLevelFb(ch, db, FbInLevel, FbInLevelText);
            }
            // PostLevel {ch}:{±}XX.XdB（输出电平）
            foreach (Match m in RxPostLevel.Matches(text))
            {
                ushort ch = ushort.Parse(m.Groups[1].Value);
                int db = (int)Math.Round(double.Parse(m.Groups[2].Value));
                UpdateLevelFb(ch, db, FbOutLevel, FbOutLevelText);
            }
        }

        private void UpdateMuteFb(string bits, bool[] states, ushort fbBase)
        {
            if (VerboseLog) CrestronConsole.PrintLine("[StageCraft] MuteFb base={0} bits={1}", fbBase, bits);
            for (int i = 0; i < bits.Length && i < Channels; i++)
            {
                bool on = bits[i] == '1';
                states[i + 1] = on;
                RaiseDigital((ushort)(fbBase + i), (ushort)(on ? 1 : 0));
            }
        }

        private void UpdateLevelFb(ushort ch, int db, ushort analogBase, ushort textBase)
        {
            if (ch < 1 || ch > Channels) return;
            // 输入 mute 时强制电平/文本归零（设备硬件 meter 不变，视觉上"静音后灯灭"）
            // 输出 mute 时强制电平/文本归零（输出端真的切断，meter 应该归零）
            bool muted = (analogBase == FbInLevel && _inMute[ch])
                      || (analogBase == FbOutLevel && _outMute[ch]);
            if (muted)
            {
                if (VerboseLog) CrestronConsole.PrintLine("[StageCraft] LevelFb ch={0} MUTED -> 0", ch);
                RaiseAnalog((ushort)(analogBase + ch - 1), 0);
                RaiseSerial((ushort)(textBase + ch - 1), new SimplSharpString(ch + ":Mute"));
            }
            else
            {
                RaiseAnalog((ushort)(analogBase + ch - 1), DbToAnalog(db));
                RaiseSerial((ushort)(textBase + ch - 1),
                    new SimplSharpString(ch + ":" + (db < 0 ? "-" : "") + Math.Abs(db) + "dB"));
            }
        }

        // ---------------- 二进制应答解析 ----------------
        private void ParseBinaryFrame(List<byte> frame)
        {
            // 设备实测帧结构：82 [vary:0x7D/0x7A/0x80] [type(2B)] [cmd] [func] [sub] [vary2?] [x] [x] [data...] 82
            if (frame.Count < 6) return;
            if (frame[0] != 0x82 || frame[frame.Count - 1] != 0x82) return;
            int start = 1; // 跳过头 0x82
            // 跳过 vary byte（不同设备的 vary 不同：0x7D/0x7A/0x80）
            if (frame.Count > 2 && (frame[start] == 0x7D || frame[start] == 0x7A || frame[start] == 0x80)) start++;
            int end = frame.Count - 1; // 去掉尾 0x82
            if (end - start < 6) return;

            byte[] body = frame.GetRange(start, end - start).ToArray();
            // body: [类型 2B] [cmd] [func] [sub] [vary?] [x x] [out-1]? [数据...]
            byte func = body[3], sub = body[4];

            if (VerboseLog)
            {
                // body 调试输出：前 12 字节十六进制
                string hex = "";
                for (int i = 0; i < Math.Min(12, body.Length); i++) hex += body[i].ToString("X2") + " ";
                CrestronConsole.PrintLine("[StageCraft] BinaryFrame len={0} body[0..11]={1}", frame.Count, hex);

                // Meter 帧打印完整 16 字节数据（诊断通道偏移用）
                if ((func == 0x07 && sub == 0x07) || (func == 0x0b && sub == 0x01))
                {
                    int ds = 8;   // Meter 数据固定从 body[8] 开始（与 UpdateMeter 一致）
                    string mh = "";
                    for (int i = 0; i < Channels && ds + i < body.Length; i++)
                        mh += body[ds + i].ToString("X2") + " ";
                    CrestronConsole.PrintLine("[StageCraft] MeterData func={0:X2} sub={1:X2} dataStart={2} [{3}]", func, sub, ds, mh.Trim());
                }
            }

            if (func == 0x08) // 路由相关（读/写路由的应答）
            {
                // 实测响应体结构：body[0..7]=头(00 00 03 08 01 80 15 00)
                //                 body[8]=输出通道号(out-1)
                //                 body[9..24]=16字节路由状态，每字节对应输入1..16（01=已路由,00=未路由）
                // 注意不能用 body.Length-16（会错位 4 字节，导致路由高亮全错）
                int outIdx = body[8];
                int dataStart = 9;
                if (body.Length >= dataStart + Channels && outIdx >= 0 && outIdx < Channels)
                {
                    for (ushort i = 0; i < Channels; i++)
                    {
                        bool on = body[dataStart + i] != 0;
                        _route[outIdx + 1, i + 1] = on;
                        // 只在当前选中的输出通道上刷新输入高亮（避免别的输出的应答误刷反馈）
                        if (outIdx + 1 == SelectedOut)
                            RaiseDigital((ushort)(FbMixIn + i), (ushort)(on ? 1 : 0));
                    }
                }
            }
            else if (func == 0x07 && sub == 0x07) // 输入音量表
            {
                UpdateMeter(body, FbInMeter);
            }
            else if (func == 0x0b && sub == 0x01) // 输出音量表
            {
                UpdateMeter(body, FbOutMeter);
            }
        }

        private void UpdateMeter(byte[] body, ushort fbBase)
        {
            // 帧头固定 8 字节：00 00 03 [func] [sub] 80 12 00，Meter 数据从 body[8] 开始
            // （不能用 body.Length - 16，因为帧尾还带 7D + 可能混入下一帧字节，会错位）
            int dataStart = 8;
            if (body.Length < dataStart + Channels) return;
            bool isInput = (fbBase == FbInMeter);
            bool isOutput = (fbBase == FbOutMeter);
            for (ushort i = 0; i < Channels; i++)
            {
                int val = body[dataStart + i];
                // 原宏公式：(byte - 31) * 532。byte 范围 31~130（0dB 起点），
                // 无信号时设备返回 0x9C=156 → (156-31)*532=66500 → ushort 自然溢出为 964（≈0，正确表示无信号）
                int analog = (val - 31) * 532;
                ushort ch = (ushort)(i + 1);
                // mute 时强制归零（输入 meter 反映 mute 状态，输出 meter 同理）
                if ((isInput && _inMute[ch]) || (isOutput && _outMute[ch])) analog = 0;
                RaiseAnalog((ushort)(fbBase + i), (ushort)analog);
            }
        }

        // ---------------- 换算与回报 ----------------
        /// <summary>dB → 模拟量（53928 = 0dB）。</summary>
        public static ushort DbToAnalog(int db)
        {
            int v = AnalogMid + db * DbPerStep;
            if (v < 0) v = 0;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

        /// <summary>模拟量 → dB（四舍五入）。</summary>
        public static int AnalogToDb(ushort analog)
        {
            return (int)Math.Round((analog - AnalogMid) / (double)DbPerStep);
        }

        private void RaiseDigital(ushort id, ushort value)
        {
            if (DigitalFb != null) DigitalFb(id, value);
        }
        private void RaiseAnalog(ushort id, ushort value)
        {
            if (AnalogFb != null)
            {
                if (VerboseLog) CrestronConsole.PrintLine("[StageCraft] RaiseAnalog id={0} v={1}", id, value);
                AnalogFb(id, value);
            }
            else if (VerboseLog)
                CrestronConsole.PrintLine("[StageCraft] RaiseAnalog SKIPPED (AnalogFb==null) id={0} v={1}", id, value);
        }
        private void RaiseSerial(ushort id, SimplSharpString text)
        {
            if (SerialFb != null) SerialFb(id, text);
        }
    }
}
