using System;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// Tasmota MQTT 人体存在传感器（圆形版，ESP8266 + 雷达开关量 + BH1750 光照）。
    ///
    /// 数据来源（传感器 Topic 默认 "huang"，以下 xxx 处替换之）：
    ///   tele/xxx/SENSOR  周期上报：{"Time":..,"Switch1":"ON","BH1750":{"Illuminance":63}}
    ///   stat/xxx/POWER   Rule1 即时上报：ON=有人 / OFF=无人（实时性来源）
    ///   tele/xxx/LWT     Online / Offline（在线状态，含 keepalive 超时遗嘱）
    ///
    /// 联动逻辑（C# 内部完成）：
    ///   有人 → light_out 立即置 1；人走 → 延时 OffDelaySec（默认 60s）后 light_out 置 0；
    ///   force_off=1 期间 light_out 强制为 0（外部联动），释放后按当前有人状态恢复。
    ///
    /// 距离/能量值：此圆形版硬件（雷达只接 GPIO 开关量）不提供，Fb 引脚已预留，固件支持即自动生效。
    /// </summary>
    public class MqttPresenceSensor
    {
        // ---------------- 委托（SIMPL+ RegisterDelegate 绑定） ----------------
        public delegate void SensorDigitalFb(ushort id, ushort value);
        public delegate void SensorAnalogFb(ushort id, ushort value);
        public delegate void SensorSerialFb(ushort id, SimplSharpString text);

        public SensorDigitalFb DigitalFb { get; set; }
        public SensorAnalogFb AnalogFb { get; set; }
        public SensorSerialFb SerialFb { get; set; }

        // ---------------- 数字反馈 ID ----------------
        public const ushort FbPresence = 1;   // 有人=1 / 无人=0
        public const ushort FbLightOut = 2;   // 灯光联动输出（人走延时后为 0）
        public const ushort FbOnline = 3;     // 传感器在线

        // ---------------- 模拟反馈 ID ----------------
        public const ushort FbLux = 1;        // 光照 lux（BH1750，直出 0-65535）
        public const ushort FbDistance = 2;   // 预留：距离 cm（硬件支持时有效）
        public const ushort FbEnergy = 3;     // 预留：能量值

        // ---------------- 串口反馈 ID ----------------
        public const ushort FbUptime = 1;     // 运行时间等状态文本

        /// <summary>人走延时熄灭秒数（默认 60，可在 SIMPL+ 里 SetOffDelay 改）。</summary>
        public ushort OffDelaySec = 60;

        /// <summary>调试日志开关。</summary>
        public bool VerboseLog { get; set; }

        private string _topic = "huang";
        private bool _started;

        private ushort _presence;      // 当前有人状态
        private ushort _lightOut;      // 当前联动输出
        private ushort _online;
        private ushort _lux = 0xFFFF;  // 初值置为不可能值，保证第一次必上报
        private bool _forceOff;
        private CTimer _offTimer;
        private readonly object _lock = new object();

        /// <summary>SIMPL+ 只能执行无参构造，初始化请走 Configure/Start。</summary>
        public MqttPresenceSensor() { }

        /// <summary>设设备 topic（如 "huang"）。须在 Start 前调用。</summary>
        public void Configure(SimplSharpString topic)
        {
            _topic = topic.ToString();
        }

        /// <summary>挂到 broker 消息总线上开始工作。重复调用安全。</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }
            MqttMiniBroker broker = MqttMiniBroker.GetInstance();
            broker.OnMessage += OnBrokerMessage;
            broker.OnClientState += OnClientState;
            Log("[Presence] started, topic=" + _topic);
        }

        /// <summary>外部联动强制关闭：1=强制熄灯并抑制有人重开，0=恢复自动逻辑。</summary>
        public void ForceOff(ushort on)
        {
            lock (_lock)
            {
                _forceOff = on != 0;
                if (_forceOff)
                {
                    StopOffTimer();
                    SetLightOut(0);
                }
                else if (_presence != 0)
                {
                    SetLightOut(1);   // 释放时仍有人 → 恢复亮灯
                }
            }
        }

        /// <summary>配置并启动迷你 broker（全程序调一次即可，多传感器实例重复调用安全），随后挂接本传感器。</summary>
        public void StartBroker(int port, SimplSharpString user, SimplSharpString pass)
        {
            try
            {
                CrestronConsole.PrintLine("[Presence] StartBroker enter, topic=" + _topic);
                MqttMiniBroker broker = MqttMiniBroker.GetInstance();
                broker.Configure(port, user.ToString(), pass.ToString());
                broker.Start();
                Start();
                CrestronConsole.PrintLine("[Presence] StartBroker done");
            }
            catch (Exception ex) { ErrorLog.Error("[Presence] StartBroker failed: " + ex.Message); }
        }

        /// <summary>运行时改延时（秒）。</summary>
        public void SetOffDelay(ushort seconds) { OffDelaySec = seconds; }

        /// <summary>开关调试日志（1=开），SSH/console 里可见报文收发。</summary>
        public void SetVerbose(ushort on)
        {
            VerboseLog = on != 0;
            MqttMiniBroker.GetInstance().VerboseLog = VerboseLog;
        }

        /// <summary>让传感器立即上报一次全部状态（broker 代发 cmnd/xxx/STATUS 10/11）。</summary>
        public void RefreshNow()
        {
            MqttMiniBroker.GetInstance().Publish("cmnd/" + _topic + "/STATUS", "10");
        }

        // ---------------- broker 消息处理 ----------------

        private void OnBrokerMessage(string topic, string payload)
        {
            // topic 形如 "tele/huang/SENSOR"，拆三段过滤本设备
            string[] parts = topic.Split('/');
            if (parts.Length != 3 || parts[1] != _topic) return;
            string prefix = parts[0], leaf = parts[2];

            if (prefix == "stat" && leaf == "POWER")
            {
                SetPresence(payload == "ON" ? (ushort)1 : (ushort)0);
            }
            else if (prefix == "tele" && leaf == "LWT")
            {
                SetOnline(payload == "Online" ? (ushort)1 : (ushort)0);
            }
            else if (prefix == "tele" && leaf == "SENSOR")
            {
                ParseSensorJson(payload);
            }
            else if (prefix == "tele" && leaf == "STATE")
            {
                Match m = Regex.Match(payload, "\"Uptime\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) RaiseSerial(FbUptime, new SimplSharpString(m.Groups[1].Value));
            }
        }

        private void OnClientState(string deviceTopic, bool connected)
        {
            if (deviceTopic == _topic)
                SetOnline(connected ? (ushort)1 : (ushort)0);
        }

        /// <summary>解析 tele SENSOR JSON。正则无依赖，3/4 代行为一致。</summary>
        private void ParseSensorJson(string json)
        {
            Match m = Regex.Match(json, "\"Switch1\"\\s*:\\s*\"(ON|OFF)\"");
            if (m.Success)
                SetPresence(m.Groups[1].Value == "ON" ? (ushort)1 : (ushort)0);

            m = Regex.Match(json, "\"Illuminance\"\\s*:\\s*(\\d+)");
            if (m.Success)
            {
                ushort lux;
                if (TryParseU16(m.Groups[1].Value, out lux) && lux != _lux)
                {
                    _lux = lux;
                    RaiseAnalog(FbLux, lux);
                }
            }

            // 预留：LD2410 串口版固件的字段，硬件支持即自动生效
            m = Regex.Match(json, "\"Distance\"\\s*:\\s*(\\d+)");
            if (m.Success) { ushort v; if (TryParseU16(m.Groups[1].Value, out v)) RaiseAnalog(FbDistance, v); }
            m = Regex.Match(json, "\"Energy\"\\s*:\\s*(\\d+)");
            if (m.Success) { ushort v; if (TryParseU16(m.Groups[1].Value, out v)) RaiseAnalog(FbEnergy, v); }
        }

        /// <summary>3 代运行时没有 TryParse，用 int.Parse + try/catch 中转并截断到 0-65535。</summary>
        private static bool TryParseU16(string s, out ushort value)
        {
            try
            {
                int v = int.Parse(s);
                if (v < 0) v = 0;
                if (v > 65535) v = 65535;
                value = (ushort)v;
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
        }

        // ---------------- 有人/延时熄灭状态机 ----------------

        private void SetPresence(ushort value)
        {
            lock (_lock)
            {
                bool changed = (value != _presence);
                if (changed)
                {
                    _presence = value;
                    RaiseDigital(FbPresence, _presence);
                }
                if (_forceOff) return;

                if (_presence == 1)
                {
                    StopOffTimer();
                    SetLightOut(1);            // 有人 → 立即亮
                }
                else
                {
                    // 人走：仅当"从有人翻到无人"（changed=true）才启动延时；
                    // 周期 SENSOR 上报的 OFF（unchanged）不重置延时，
                    // 否则 10s 一次的上报会把 60s 延时无限推迟，灯永远不灭。
                    if (changed)
                        StartOffTimer();
                }
            }
        }

        private void StartOffTimer()
        {
            StopOffTimer();
            if (OffDelaySec == 0) { SetLightOut(0); return; }
            _offTimer = new CTimer(OnOffTimer, null, (long)OffDelaySec * 1000, 0);
            Log("[Presence] off-delay " + OffDelaySec + "s started");
        }

        private void StopOffTimer()
        {
            if (_offTimer != null)
            {
                _offTimer.Stop();
                _offTimer.Dispose();
                _offTimer = null;
            }
        }

        private void OnOffTimer(object unused)
        {
            lock (_lock)
            {
                _offTimer = null;
                if (_presence == 0 && !_forceOff)
                    SetLightOut(0);            // 延时到 → 熄灭
            }
        }

        private void SetLightOut(ushort value)
        {
            if (value == _lightOut) return;   // dirty check
            _lightOut = value;
            RaiseDigital(FbLightOut, value);
            Log("[Presence] light_out=" + value);
        }

        private void SetOnline(ushort value)
        {
            if (value == _online) return;
            _online = value;
            RaiseDigital(FbOnline, value);
        }

        // ---------------- 反馈输出 ----------------

        private void RaiseDigital(ushort id, ushort value)
        {
            SensorDigitalFb h = DigitalFb;
            if (h != null) h(id, value);
        }
        private void RaiseAnalog(ushort id, ushort value)
        {
            SensorAnalogFb h = AnalogFb;
            if (h != null) h(id, value);
        }
        private void RaiseSerial(ushort id, SimplSharpString text)
        {
            SensorSerialFb h = SerialFb;
            if (h != null) h(id, text);
        }

        private void Log(string msg)
        {
            if (VerboseLog) CrestronConsole.PrintLine(msg);
        }
    }
}
