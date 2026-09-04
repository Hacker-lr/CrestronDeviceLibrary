using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// TendZone（东微）7216 音频处理器（16x16）。
    ///
    /// 协议为"ASCII 管道协议 + 二进制帧"双协议，原宏用两条 TCP 连接分别承载，本类保持一致：
    /// 【控制连接 ASCII】（电平/静音/订阅/音量表/预设）
    ///   命令以 ';' 结尾：set|gain_82|1|mute:true; / set|gain_82|1|step:1;
    ///                       set|gain_82|1|gain:-7; / get|meter_85|1|level; / LOADP 3#
    ///   订阅上报：set|report|enable:true; + set|report|gain_82|enable:true;
    ///   上报/应答：0|report|gain_82|1|gain:2.500000,mute:true,name:;
    ///              0|get|meter_85|13|level:-120.000000;
    ///   gain_82/gain_83/meter_85/meter_86 为东微配置软件生成的模块名，做成 .usp 参数。
    /// 【矩阵连接二进制】（登录 + 混音路由，帧格式 82 7D ... 7D 82，与原宏 chr() 序列字节级一致）
    ///   登录：    82 7D 01 00 02 01 01 01 05 00 "admin" 7D 82
    ///   读路由：  82 7D 01 00 03 08 01 00 01 00 [out-1] 7D 82
    ///   写路由：  82 7D 01 00 03 08 01 01 03 00 [out-1] [in-1] [01/00] 7D 82
    ///   读应答：  82 7D 00 00 03 08 01 80 11 00 [out-1] [16字节路由] 7D 82
    ///
    /// 架构与 StageCraftMatrix/BiampTesiraMatrix 一致：C# 直连设备双 TCP，SIMPL+ 薄壳只接线。
    /// 反馈 id 约定与 StageCraft/Biamp 完全相同（数字/模拟/串口三套独立编号）。
    ///
    /// 相对原纯 SIMPL+ 宏修复的问题：
    ///   1. 原宏在 SIMPL+ 内读 DIGITAL_OUTPUT 判断状态（OUTPUT 程序侧不可读，恒为 0），
    ///      导致静音/路由切换只能"开"不能"关"——本类用 C# 状态缓存正确切换。
    ///   2. 原宏 meter_flash&&mixpage_fb 的 while 死循环轮询（且 16 路 get 互相覆盖只发出第 12 路）
    ///      ——本类改为 CTimer 页面驱动轮询，16 路合并一条发送。
    ///   3. 原宏 mode1/mode2 把 ASCII 命令 LOADP 13# 发到二进制口（设备忽略）——
    ///      统一改为 LOADP 1#/2#/3#/4# 发控制连接。
    ///   4. 原宏 do{}UNTIL(1) 同步等路由应答可能死循环——本类异步解析，无阻塞。
    /// </summary>
    public class TendZoneMatrix : IMatrixControl
    {
        // ---------- 常量 ----------
        public const ushort Channels = 16;
        public const ushort AnalogMid = 53928;       // 模拟量 0dB 中点（增益推子）
        public const ushort DbPerStep = 963;         // 每 dB 的模拟量刻度
        public const int MeterDbMin = -120;          // 音量表(VU)下限 dB（东微 meter 实测下限）
        public const int MeterDbMax = 12;            // 音量表(VU)上限 dB

        // 反馈 id 约定（与 .usp 回调 fnDigital/fnAnalog/fnSerial 一致；数字/模拟/串口分开编号）
        public const ushort FbInMute = 1;            // 1..16   输入静音（数字）
        public const ushort FbOutMute = 17;          // 17..32  输出静音（数字）
        public const ushort FbMixIn = 33;            // 33..48  混音输入路由（数字）
        public const ushort FbMixOut = 49;           // 49..64  混音输出选择（数字）
        public const ushort FbMode1 = 65;            // 65..68  模式 1..4（数字）
        public const ushort FbMeterFlash = 69;       //         音量表开关反馈（数字）
        public const ushort FbAllMute = 70;          //         全部静音（数字）
        public const ushort FbInLevel = 1;           // 1..16   输入电平（模拟）
        public const ushort FbOutLevel = 17;         // 17..32  输出电平（模拟）
        public const ushort FbInMeter = 33;          // 33..48  输入音量表（模拟）
        public const ushort FbOutMeter = 49;         // 49..64  输出音量表（模拟）
        public const ushort FbInLevelText = 1;       // 1..16   输入电平显示串（串口）
        public const ushort FbOutLevelText = 17;     // 17..32  输出电平显示串（串口）

        // 调试开关（static readonly 避免 CS0162 不可达代码警告）
        private static readonly bool VerboseLog = false;

        // ---------- 输出委托（SIMPL+ 用 RegisterDelegate 订阅） ----------
        public MatrixDigitalFb DigitalFb { get; set; }
        public MatrixAnalogFb AnalogFb { get; set; }
        public MatrixSerialFb SerialFb { get; set; }

        /// <summary>连接状态变化：true=控制连接已连，false=断开（冗余控制器据此做 leader 选举）。</summary>
        public event DeviceConnectionHandler ConnectionStateChanged;

        // ---------- 状态 ----------
        public ushort SelectedOut { get; private set; }
        public ushort SelectedIn { get; private set; }

        private readonly bool[] _inMute = new bool[Channels + 1];
        private readonly bool[] _outMute = new bool[Channels + 1];
        private readonly bool[,] _route = new bool[Channels + 1, Channels + 1];   // [输出, 输入]

        // ---- dirty check：增益值没变不 Raise（3 系 VTP 卡顿优化；4 系行为不变）----
        // 用 int.MaxValue 作"无值"哨兵（合法模拟值 0~65535 不会碰撞）
        private readonly int[] _lastLevelAnalog = InitNeg(Channels + 1);
        private static int[] InitNeg(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = int.MaxValue; return a; }

        // 模块名（东微配置软件生成，做成 .usp 参数）
        private string _inGainName = "gain_82";
        private string _outGainName = "gain_83";
        private string _inMeterName = "meter_85";
        private string _outMeterName = "meter_86";

        // 双 TCP 连接
        private string _ip = "192.168.0.200";
        private int _ctrlPort = 5000;                // ASCII 控制口
        private int _binPort = 5000;                 // 二进制矩阵口（与控制口相同则设备单端口混合协议）
        private readonly TcpLink _ctrl = new TcpLink();
        private readonly TcpLink _bin = new TcpLink();

        // 页面驱动轮询（0=停，1=输入页，2=输出页，3=混音页）+ 音量表总开关
        private CTimer _pollTimer;
        private int _pollMode;
        private bool _meterFlash;
        private ushort _pollIntervalMs = 400;        // 3 系友好周期
        private int _tick;

        // 路由读应答匹配（防止把写应答/登录应答误当路由数据）
        private volatile bool _routeReadPending;

        // =====================================================================
        //  连接配置（SIMPL+ 在 Main 里调用）
        // =====================================================================
        /// <summary>接口方法：设置 IP，控制口/矩阵口同为 port（单端口混合协议场景）。</summary>
        public void Configure(SimplSharpString ip, ushort port)
        {
            ConfigurePorts(ip, port, port);
        }

        /// <summary>
        /// 设置设备地址：IP + ASCII 控制口 + 二进制矩阵口。变化时自动重连。
        /// 注意：方法名必须不同于接口的 Configure（SIMPL+ 编译器不支持同名重载，否则报 Error 1002 Missing ')'）。
        /// </summary>
        public void ConfigurePorts(SimplSharpString ip, ushort controlPort, ushort matrixPort)
        {
            string s = (ip != null) ? ip.ToString() : "";
            string newIp = !string.IsNullOrEmpty(s) ? s.Trim() : _ip;
            int newCtrl = controlPort > 0 ? controlPort : _ctrlPort;
            int newBin = matrixPort > 0 ? matrixPort : _binPort;
            bool changed = (newIp != _ip) || (newCtrl != _ctrlPort) || (newBin != _binPort);
            _ip = newIp;
            _ctrlPort = newCtrl;
            _binPort = newBin;
            CrestronConsole.PrintLine("[TendZone] Configure ip={0} ctrl={1} bin={2} (changed={3})",
                _ip, _ctrlPort, _binPort, changed);
            if (changed && _ctrl.Client != null)
            {
                Stop();
                Start();
            }
        }

        /// <summary>设置东微模块名（gain_82/gain_83/meter_85/meter_86，来自东微配置软件）。</summary>
        public void ConfigureNames(SimplSharpString inGain, SimplSharpString outGain,
            SimplSharpString inMeter, SimplSharpString outMeter)
        {
            _inGainName = TrimName(inGain, _inGainName);
            _outGainName = TrimName(outGain, _outGainName);
            _inMeterName = TrimName(inMeter, _inMeterName);
            _outMeterName = TrimName(outMeter, _outMeterName);
            CrestronConsole.PrintLine("[TendZone] Names inGain={0} outGain={1} inMeter={2} outMeter={3}",
                _inGainName, _outGainName, _inMeterName, _outMeterName);
        }

        private static string TrimName(SimplSharpString s, string fallback)
        {
            if (s == null) return fallback;
            string t = s.ToString().Trim();
            return t.Length > 0 ? t : fallback;
        }

        /// <summary>开始连接设备（异步；控制连接就绪后自动订阅+全量初始化，矩阵连接自动登录）。</summary>
        public void Start()
        {
            _ctrl.OnConnected = ControlConnected;
            _ctrl.OnData = ControlData;
            _ctrl.OnStateChange = OnCtrlState;
            _ctrl.Open(_ip, _ctrlPort);

            _bin.OnConnected = BinConnected;
            _bin.OnData = BinData;
            _bin.Open(_ip, _binPort);
        }

        /// <summary>断开双连接并停止重连/轮询。</summary>
        public void Stop()
        {
            StopLevelPolling();
            _ctrl.Close();
            _bin.Close();
            SetConnected(false);
        }

        // =====================================================================
        //  控制连接（ASCII）回调
        // =====================================================================
        private void ControlConnected()
        {
            CrestronConsole.PrintLine("[TendZone] v1.0 control link ONLINE {0}:{1}", _ip, _ctrlPort);
            // 连上即推默认状态（VTP 立即有显示），订阅上报 + 全量 meter 读回随后覆盖实际值
            PushDefaultStates();
            SubscribeReports();
            PollAllMeters();
            if (_pollMode == 3)
                ReadMixRoute(SelectedOut < 1 ? (ushort)1 : SelectedOut);
        }

        private void OnCtrlState(bool online)
        {
            SetConnected(online);
            if (!online)
                CrestronConsole.PrintLine("[TendZone] control link OFFLINE");
        }

        /// <summary>接收缓冲与 ';' 分句解析（上报/应答以 ';' 结尾，多条可能粘连）。</summary>
        private readonly List<byte> _ctrlRx = new List<byte>(4096);

        private void ControlData(byte[] data, int len)
        {
            _ctrlRx.AddRange(data);
            int guard = 0;
            while (_ctrlRx.Count > 0 && guard++ < 500)
            {
                int sep = _ctrlRx.IndexOf((byte)';');
                if (sep < 0) break;
                var sentence = _ctrlRx.GetRange(0, sep);
                _ctrlRx.RemoveRange(0, sep + 1);
                ParseSentence(sentence);
            }
            if (_ctrlRx.Count > 8192)
            {
                CrestronConsole.PrintLine("[TendZone] WARN ctrl buffer overflow {0}B, clear", _ctrlRx.Count);
                _ctrlRx.Clear();
            }
        }

        /// <summary>
        /// 解析一条 ASCII 句子：
        ///   0|report|gain_82|N|gain:2.500000,mute:true,name:;
        ///   0|get|meter_85|N|level:-120.000000;
        /// </summary>
        private void ParseSentence(List<byte> bytes)
        {
            if (bytes.Count == 0) return;
#if SERIES3
            string t = Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count).Trim();
#else
            string t = Encoding.ASCII.GetString(bytes.ToArray()).Trim();
#endif
            if (t.Length == 0) return;
            if (VerboseLog) CrestronConsole.PrintLine("[TendZone] RX: {0}", t);

            // 分隔符 '|'；payload 内只有 ',' ':'，无 '|'
            string[] parts = t.Split('|');
            if (parts.Length < 5) return;
            string kind = parts[1];          // report / get / set(应答回显忽略)
            string module = parts[2];
            int ch;
#if SERIES3
            if (!TryParseInt(parts[3], out ch)) return;
#else
            if (!int.TryParse(parts[3], out ch)) return;
#endif
            if (ch < 1 || ch > Channels) return;
            string payload = parts[4];

            if (kind == "report" && module == _inGainName)
                ParseGainReport((ushort)ch, payload, true);
            else if (kind == "report" && module == _outGainName)
                ParseGainReport((ushort)ch, payload, false);
            else if (kind == "get" && module == _inMeterName)
                ParseMeterReport((ushort)ch, payload, true);
            else if (kind == "get" && module == _outMeterName)
                ParseMeterReport((ushort)ch, payload, false);
            // 其余（set 回显/错误/未知模块）忽略
        }

        /// <summary>解析增益上报 payload："gain:2.500000,mute:true,name:xxx"。</summary>
        private void ParseGainReport(ushort ch, string payload, bool isInput)
        {
            // 逗号分 token：gain:X / mute:true|false / name:xxx
            string[] tokens = payload.Split(',');
            double db = 0;
            bool mute = isInput ? _inMute[ch] : _outMute[ch];   // 缺省保持原状态
            for (int i = 0; i < tokens.Length; i++)
            {
                string tok = tokens[i];
                if (tok.StartsWith("gain:"))
                {
                    string s = tok.Substring(5);
#if SERIES3
                    double d; if (TryParseDouble(s, out d)) db = d;
#else
                    double d;
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d)) db = d;
#endif
                }
                else if (tok.StartsWith("mute:"))
                {
                    mute = tok.Substring(5).Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }

            // 静音状态反馈（dirty check）
            if (isInput)
            {
                if (_inMute[ch] != mute)
                {
                    _inMute[ch] = mute;
                    RaiseDigital((ushort)(FbInMute + ch - 1), (ushort)(mute ? 1 : 0));
                }
            }
            else
            {
                if (_outMute[ch] != mute)
                {
                    _outMute[ch] = mute;
                    RaiseDigital((ushort)(FbOutMute + ch - 1), (ushort)(mute ? 1 : 0));
                }
            }

            // 增益推子反馈（始终反映 dB 位置；静音不把推子拉到底——规范③）
            int idb = (int)Math.Round(db);
            ushort analog = DbToAnalog(idb);
            if (_lastLevelAnalog[ch] != analog)
            {
                _lastLevelAnalog[ch] = analog;
                RaiseAnalog((ushort)((isInput ? FbInLevel : FbOutLevel) + ch - 1), analog);
            }
            RaiseSerial((ushort)((isInput ? FbInLevelText : FbOutLevelText) + ch - 1),
                new SimplSharpString(ch + ":" + (idb < 0 ? "-" : "") + Math.Abs(idb) + "dB"));
        }

        /// <summary>解析音量表应答 payload："level:-120.000000" → 线性映射到 0..65535。</summary>
        private void ParseMeterReport(ushort ch, string payload, bool isInput)
        {
            if (!payload.StartsWith("level:")) return;
            string s = payload.Substring(6);
            double db;
#if SERIES3
            if (!TryParseDouble(s, out db)) return;
#else
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out db)) return;
#endif
            ushort analog = MeterDbToAnalog(db);
            // mute 时强制归零（杀 VU，规范③）；meter 是实时信号，不做 dirty check
            bool muted = isInput ? _inMute[ch] : _outMute[ch];
            ushort fbBase = isInput ? FbInMeter : FbOutMeter;
            RaiseAnalog((ushort)(fbBase + ch - 1), muted ? (ushort)0 : analog);
        }

        // =====================================================================
        //  矩阵连接（二进制）回调
        // =====================================================================
        private void BinConnected()
        {
            CrestronConsole.PrintLine("[TendZone] matrix link ONLINE {0}:{1}, login...", _ip, _binPort);
            SendLogin();
        }

        private readonly List<byte> _binRx = new List<byte>(1024);

        private void BinData(byte[] data, int len)
        {
            _binRx.AddRange(data);
            int guard = 0;
            while (_binRx.Count > 0 && guard++ < 300)
            {
                int start = _binRx.IndexOf(0x82);
                if (start < 0) { _binRx.Clear(); break; }          // 纯脏数据
                if (start > 0) { _binRx.RemoveRange(0, start); continue; }  // 对齐到帧头
                int end = _binRx.IndexOf(0x82, 1);
                if (end < 0) break;                                 // 半帧，等更多数据
                var frame = _binRx.GetRange(0, end + 1);
                _binRx.RemoveRange(0, end + 1);
                ParseBinaryFrame(frame);
            }
            if (_binRx.Count > 4096)
            {
                CrestronConsole.PrintLine("[TendZone] WARN bin buffer overflow {0}B, clear", _binRx.Count);
                _binRx.Clear();
            }
        }

        /// <summary>
        /// 解析二进制路由读应答帧（82 7D ... 7D 82）。
        /// 与原宏 readaudiomix 的 check$/mid() 逻辑精确对齐：
        ///   应答头（11 字节）：82 7D 00 00 03 08 01 80 11 00 [out-1]
        ///   之后 16 字节 = 输入 1..16 的路由状态（01=已路由），帧尾 7D 82
        ///   帧索引：           [0][1] [2][3][4][5][6][7][8][9] [10]   [11..26]         [27][28]
        /// </summary>
        private void ParseBinaryFrame(List<byte> frame)
        {
            if (frame.Count < 29) return;                          // 11头 + 16数据 + 2尾
            if (frame[0] != 0x82 || frame[frame.Count - 1] != 0x82) return;

            // 匹配功能码段（对应原宏 check$ 的 frame[2..9]）
            if (frame[2] != 0x00 || frame[3] != 0x00 || frame[4] != 0x03 || frame[5] != 0x08 ||
                frame[6] != 0x01 || frame[7] != 0x80 || frame[8] != 0x11 || frame[9] != 0x00)
            {
                if (VerboseLog)
                {
                    string hex = "";
                    for (int i = 0; i < Math.Min(frame.Count, 14); i++) hex += frame[i].ToString("X2") + " ";
                    CrestronConsole.PrintLine("[TendZone] bin frame (non-route) len={0} [{1}]", frame.Count, hex.Trim());
                }
                return;
            }

            if (!_routeReadPending)
            {
                if (VerboseLog) CrestronConsole.PrintLine("[TendZone] route frame ignored (no read pending)");
                return;
            }
            _routeReadPending = false;

            int outCh = frame[10] + 1;                             // frame[10] = out-1
            if (outCh < 1 || outCh > Channels) return;

            if (VerboseLog) CrestronConsole.PrintLine("[TendZone] route resp out={0}", outCh);
            for (ushort i = 1; i <= Channels; i++)
            {
                bool on = frame[10 + i] != 0;                      // frame[11..26] = 输入 1..16 路由
                _route[outCh, i] = on;
                if (outCh == SelectedOut)
                    RaiseDigital((ushort)(FbMixIn + i - 1), (ushort)(on ? 1 : 0));
            }
        }

        // =====================================================================
        //  命令接口（IMatrixControl，SIMPL+ 薄壳调用）
        // =====================================================================
        public void SetInputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > Channels) return;
            _inMute[ch] = mute != 0;
            SendCmd("set|" + _inGainName + "|" + ch + "|mute:" + (mute != 0 ? "true" : "false") + ";");
            RaiseDigital((ushort)(FbInMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
            // mute 时立即熄灭音量表（杀 VU），推子保持原位（规范③）
            if (mute != 0) RaiseAnalog((ushort)(FbInMeter + ch - 1), 0);
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
            SendCmd("set|" + _outGainName + "|" + ch + "|mute:" + (mute != 0 ? "true" : "false") + ";");
            RaiseDigital((ushort)(FbOutMute + ch - 1), (ushort)(mute != 0 ? 1 : 0));
            if (mute != 0) RaiseAnalog((ushort)(FbOutMeter + ch - 1), 0);
        }

        public void ToggleOutputMute(ushort ch)
        {
            if (ch < 1 || ch > Channels) return;
            SetOutputMute(ch, (ushort)(_outMute[ch] ? 0 : 1));
        }

        public void AllMute(ushort mute)
        {
            for (ushort ch = 1; ch <= Channels; ch++)
                SetOutputMute(ch, mute);
            RaiseDigital(FbAllMute, mute);
        }

        // ---- 电平调节 ----
        public void InputLevelAdd(ushort ch)  { if (ch >= 1 && ch <= Channels) SendCmd("set|" + _inGainName + "|" + ch + "|step:1;"); }
        public void InputLevelSub(ushort ch)  { if (ch >= 1 && ch <= Channels) SendCmd("set|" + _inGainName + "|" + ch + "|step:-1;"); }
        public void OutputLevelAdd(ushort ch) { if (ch >= 1 && ch <= Channels) SendCmd("set|" + _outGainName + "|" + ch + "|step:1;"); }
        public void OutputLevelSub(ushort ch) { if (ch >= 1 && ch <= Channels) SendCmd("set|" + _outGainName + "|" + ch + "|step:-1;"); }

        /// <summary>设置输入电平（dB，可负）。</summary>
        public void SetInputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > Channels) return;
            SendCmd("set|" + _inGainName + "|" + ch + "|gain:" + (db < 0 ? "-" + (-db) : db.ToString()) + ";");
        }

        /// <summary>设置输出电平（dB，可负）。</summary>
        public void SetOutputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > Channels) return;
            SendCmd("set|" + _outGainName + "|" + ch + "|gain:" + (db < 0 ? "-" + (-db) : db.ToString()) + ";");
        }

        public void SetInputLevelAnalog(ushort ch, ushort analog)  { SetInputLevel(ch, AnalogToDb(analog)); }
        public void SetOutputLevelAnalog(ushort ch, ushort analog) { SetOutputLevel(ch, AnalogToDb(analog)); }

        // ---- 混音路由 ----
        /// <summary>选中输出通道并读回其路由状态（应答驱动反馈）。</summary>
        public void SelectOutput(ushort outCh)
        {
            if (outCh < 1 || outCh > Channels) return;
            SelectedOut = outCh;
            for (ushort i = 1; i <= Channels; i++)
            {
                RaiseDigital((ushort)(FbMixOut + i - 1), (ushort)(i == outCh ? 1 : 0));
                RaiseDigital((ushort)(FbMixIn + i - 1), (ushort)(_route[outCh, i] ? 1 : 0));
            }
            ReadMixRoute(outCh);   // 读回实际路由覆盖缓存
        }

        /// <summary>按下输入通道：切换该交叉点路由。</summary>
        public void ToggleRoute(ushort inCh)
        {
            if (inCh < 1 || inCh > Channels || SelectedOut < 1) return;
            SelectedIn = inCh;
            SetRoute(SelectedOut, inCh, (ushort)(_route[SelectedOut, inCh] ? 0 : 1));
        }

        /// <summary>直接设置交叉点路由：1=连接，0=断开。与原宏 chr() 序列字节级一致。</summary>
        public void SetRoute(ushort outCh, ushort inCh, ushort on)
        {
            if (outCh < 1 || outCh > Channels || inCh < 1 || inCh > Channels) return;
            _route[outCh, inCh] = on != 0;
            SendLogin();
            SendBin(new byte[]
            {
                0x82, 0x7D, 0x01, 0x00, 0x03, 0x08, 0x01, 0x01, 0x03, 0x00,
                (byte)(outCh - 1), (byte)(inCh - 1), (byte)(on != 0 ? 0x01 : 0x00), 0x7D, 0x82
            });
            if (outCh == SelectedOut)
                RaiseDigital((ushort)(FbMixIn + inCh - 1), on);
        }

        /// <summary>读取某输出通道的路由状态（应答异步驱动反馈）。与原宏 chr() 序列字节级一致。</summary>
        public void ReadMixRoute(ushort outCh)
        {
            if (outCh < 1 || outCh > Channels) return;
            SendLogin();
            _routeReadPending = true;
            SendBin(new byte[]
            {
                0x82, 0x7D, 0x01, 0x00, 0x03, 0x08, 0x01, 0x00, 0x01, 0x00,
                (byte)(outCh - 1), 0x7D, 0x82
            });
        }

        // ---- 预设（ASCII 发控制连接；原宏 mode1/2 发错口已修）----
        public void LoadPreset(ushort n)
        {
            SendCmd("LOADP " + n + "#");
            // 预设加载后电平/静音状态全变：请求上报刷新（设备若未推则保持订阅覆盖）
        }

        // ---- 音量表总开关（meter_flash，1=显示 0=熄灭）----
        /// <summary>设置音量表总开关。</summary>
        public void SetMeterFlash(ushort on)
        {
            _meterFlash = on != 0;
            RaiseDigital(FbMeterFlash, on);
            if (!_meterFlash) ClearMeters();   // 关闭时立即熄灭（对应原宏 RELEASE meter_flash）
        }

        /// <summary>翻转音量表总开关（SIMPL+ PUSH meter_flash 调用；OUTPUT 不可读，状态在 C# 侧维护）。</summary>
        public void ToggleMeterFlash()
        {
            SetMeterFlash((ushort)(_meterFlash ? 0 : 1));
        }

        private void ClearMeters()
        {
            for (ushort ch = 1; ch <= Channels; ch++)
            {
                RaiseAnalog((ushort)(FbInMeter + ch - 1), 0);
                RaiseAnalog((ushort)(FbOutMeter + ch - 1), 0);
            }
        }

        // =====================================================================
        //  页面驱动轮询（IMatrixControl：0=停，1=输入页，2=输出页，3=混音页）
        // =====================================================================
        /// <summary>
        /// 设置轮询模式（页面驱动）。mode: 0=停，1=输入页，2=输出页，3=混音页。
        /// 由 .usp 的 CHANGE mixpage_fb/mixin_button_fb/mixout_button_fb 调用。
        /// 离开混音页（mode 3→0）时自动熄灭所有音量表（对应原宏 RELEASE mixpage_fb）。
        /// </summary>
        public void SetPollMode(ushort mode)
        {
            if (mode > 3) mode = 0;
            int old = _pollMode;
            if (old == mode) return;
            _pollMode = mode;
            CrestronConsole.PrintLine("[TendZone] SetPollMode -> {0} (0=停,1=输入,2=输出,3=混音)", mode);
            if (old == 3 && mode == 0)
            {
                _meterFlash = false;             // 离开混音页关闭音量表
                RaiseDigital(FbMeterFlash, 0);
                ClearMeters();
            }
            if (mode == 0)
                StopLevelPolling();
            else if (_pollTimer == null)
                StartLevelPolling(_pollIntervalMs);
        }

        public void StartLevelPolling(ushort intervalMs)
        {
            if (_pollTimer != null) return;
            if (intervalMs < 100) intervalMs = 100;
            _pollIntervalMs = intervalMs;
            _tick = 0;
            _pollTimer = new CTimer(PollTick, null, intervalMs, intervalMs);
        }

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
            if (_pollMode == 0) return;          // 已停（双保险）
            if (!_meterFlash) return;            // 音量表开关关 → 不轮询

            if (_pollMode == 1)
                PollMeter(_inMeterName);
            else if (_pollMode == 2)
                PollMeter(_outMeterName);
            else if (_pollMode == 3)
                PollMeter(_inMeterName, _outMeterName);   // 混音页双侧 meter
        }

        /// <summary>一次轮询：16 路合并一条发送（修复原宏 16 次赋值互相覆盖只发第 12 路的 bug）。</summary>
        private void PollMeter(string module) { PollMeter(module, null); }

        private void PollMeter(string moduleA, string moduleB)
        {
            var sb = new StringBuilder();
            AppendMeterGets(sb, moduleA);
            if (moduleB != null) AppendMeterGets(sb, moduleB);
            SendCmd(sb.ToString());
        }

        private static void AppendMeterGets(StringBuilder sb, string module)
        {
            for (int ch = 1; ch <= Channels; ch++)
                sb.Append("get|").Append(module).Append('|').Append(ch).Append("|level;");
        }

        /// <summary>开机/重连全量读回音量表（覆盖默认值）。</summary>
        private void PollAllMeters()
        {
            PollMeter(_inMeterName, _outMeterName);
        }

        // =====================================================================
        //  内部：订阅 / 组包发送 / 回报
        // =====================================================================
        /// <summary>订阅上报（对应原宏 mixpage_fb PUSH 的 3 条 + Main 开机订阅）。</summary>
        private void SubscribeReports()
        {
            SendCmd("set|report|enable:true;");
            SendCmd("set|report|" + _inGainName + "|enable:true;");
            SendCmd("set|report|" + _outGainName + "|enable:true;");
        }

        /// <summary>二进制登录包（原宏 chr() 序列：82 7D 01 00 02 01 01 01 05 00 "admin" 7D 82）。</summary>
        private void SendLogin()
        {
            SendBin(new byte[]
            {
                0x82, 0x7D, 0x01, 0x00, 0x02, 0x01, 0x01, 0x01, 0x05, 0x00,
                0x61, 0x64, 0x6D, 0x69, 0x6E, 0x7D, 0x82   // "admin"
            });
        }

        /// <summary>推送默认状态（VTP 立即有显示，避免空白）：电平 0dB、静音 off、音量表 0。</summary>
        private void PushDefaultStates()
        {
            for (ushort ch = 1; ch <= Channels; ch++)
            {
                RaiseAnalog((ushort)(FbInLevel + ch - 1), DbToAnalog(0));
                RaiseAnalog((ushort)(FbOutLevel + ch - 1), DbToAnalog(0));
                RaiseSerial((ushort)(FbInLevelText + ch - 1), new SimplSharpString(ch + ":0dB"));
                RaiseSerial((ushort)(FbOutLevelText + ch - 1), new SimplSharpString(ch + ":0dB"));
                RaiseDigital((ushort)(FbInMute + ch - 1), 0);
                RaiseDigital((ushort)(FbOutMute + ch - 1), 0);
                RaiseAnalog((ushort)(FbInMeter + ch - 1), 0);
                RaiseAnalog((ushort)(FbOutMeter + ch - 1), 0);
            }
            for (ushort i = 1; i <= Channels; i++)
            {
                RaiseDigital((ushort)(FbMixIn + i - 1), 0);
                RaiseDigital((ushort)(FbMixOut + i - 1), 0);
            }
        }

        private void SendCmd(string cmd) { _ctrl.Send(Encoding.ASCII.GetBytes(cmd)); }
        private void SendBin(byte[] frame) { _bin.Send(frame); }

        /// <summary>设置连接状态并（仅在变化时）触发 ConnectionStateChanged，供冗余控制器做 leader 选举。</summary>
        private bool _connected;
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
            if (AnalogFb != null) AnalogFb(id, value);
        }
        private void RaiseSerial(ushort id, SimplSharpString text)
        {
            if (SerialFb != null) SerialFb(id, text);
        }

        // ---------------- 换算 ----------------
        /// <summary>dB → 模拟量（53928 = 0dB，与原宏公式一致）。</summary>
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

        /// <summary>音量表 dB → 模拟量：-120dB→0，+12dB→65535 线性。internal：仅类内部换算用。</summary>
        internal static ushort MeterDbToAnalog(double db)
        {
            double v = (db - MeterDbMin) * 65535.0 / (MeterDbMax - MeterDbMin);
            if (v < 0) v = 0;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

#if SERIES3
        // ---- .NET CF 3.5 兼容辅助（3代无 int.TryParse / double.TryParse）----
        private static bool TryParseInt(string s, out int r) { try { r = int.Parse(s); return true; } catch { r = 0; return false; } }
        private static bool TryParseDouble(string s, out double r)
        {
            try { r = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); return true; }
            catch { r = 0; return false; }
        }
#endif

        // =====================================================================
        //  TcpLink：TCP 连接 + 断线重连（双连接共用的小封装）
        // =====================================================================
        private class TcpLink
        {
            public TCPClient Client;
            public bool Connected;
            public Action OnConnected;
            public Action<byte[], int> OnData;
            public Action<bool> OnStateChange;

            private string _ip;
            private int _port;
            private CTimer _reconnectTimer;

            public void Open(string ip, int port)
            {
                _ip = ip;
                _port = port;
                Connect();
            }

            public void Close()
            {
                CancelReconnect();
                try
                {
                    if (Client != null)
                    {
                        Client.SocketStatusChange -= OnSocketStatusChange;
                        Client.DisconnectFromServer();
                        Client = null;
                    }
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine("[TendZone] close EXCEPTION: {0}", ex.Message);
                }
                SetConnected(false);
            }

            private void Connect()
            {
                CancelReconnect();
                try
                {
                    Client = new TCPClient(_ip, _port, 4096);
                    Client.SocketStatusChange += OnSocketStatusChange;
                    SocketErrorCodes err = Client.ConnectToServerAsync(OnConnectComplete);
                    if (err != SocketErrorCodes.SOCKET_OPERATION_PENDING && err != SocketErrorCodes.SOCKET_OK)
                    {
                        CrestronConsole.PrintLine("[TendZone] connect async err={0} ({1}:{2})", err, _ip, _port);
                        ScheduleReconnect();
                    }
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine("[TendZone] connect EXCEPTION: {0}", ex.Message);
                    ScheduleReconnect();
                }
            }

            private void OnConnectComplete(TCPClient c)
            {
                if (c.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    SetConnected(true);
                    c.ReceiveDataAsync(OnReceiveData);
                    if (OnConnected != null) OnConnected();
                }
                else
                {
                    CrestronConsole.PrintLine("[TendZone] connect failed status={0} ({1}:{2})", c.ClientStatus, _ip, _port);
                    ScheduleReconnect();
                }
            }

            private void OnReceiveData(TCPClient c, int n)
            {
                if (n <= 0)
                {
                    SetConnected(false);
                    ScheduleReconnect();
                    return;
                }
                try
                {
                    byte[] data = new byte[n];
                    Array.Copy(c.IncomingDataBuffer, data, n);
                    if (OnData != null) OnData(data, n);
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine("[TendZone] receive EXCEPTION: {0}", ex.Message);
                }
                c.ReceiveDataAsync(OnReceiveData);   // 必须重注册，否则只收一次
            }

            private void OnSocketStatusChange(TCPClient c, SocketStatus status)
            {
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
                        ScheduleReconnect();
                        break;
                    // 中间状态（WAITING / DNS_*）忽略
                }
            }

            public void Send(byte[] data)
            {
                try
                {
                    if (Client == null || !Connected) return;
                    SocketErrorCodes err = Client.SendData(data, data.Length);
                    if (err != SocketErrorCodes.SOCKET_OK)
                        CrestronConsole.PrintLine("[TendZone] WARN send err={0} ({1}:{2})", err, _ip, _port);
                }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine("[TendZone] send EXCEPTION: {0}", ex.Message);
                }
            }

            private void SetConnected(bool online)
            {
                if (Connected == online) return;
                Connected = online;
                if (OnStateChange != null) OnStateChange(online);
            }

            private void ScheduleReconnect()
            {
                if (_reconnectTimer != null) return;
                _reconnectTimer = new CTimer(o => { _reconnectTimer = null; Connect(); }, null, 5000, 0);
            }

            private void CancelReconnect()
            {
                if (_reconnectTimer == null) return;
                _reconnectTimer.Stop();
                _reconnectTimer.Dispose();
                _reconnectTimer = null;
            }
        }
    }
}
