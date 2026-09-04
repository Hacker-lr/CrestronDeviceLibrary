using System;
using Crestron.SimplSharp;
using CrestronDeviceLibrary.Common;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// Sony VISCA over IP 摄像机控制（协议逻辑层）。
    ///
    /// 职责：只负责 VISCA 命令组包与内部状态；不碰引脚、不碰 IPID。
    /// SIMPL+ 薄壳负责：把数字/模拟/串口引脚映射到本类的方法，
    /// 并通过 RegisterDelegate 订阅 SendTx / ReportStatus 拿回数据。
    ///
    /// 命令格式：VISCA 命令 以 0x80+地址 开头、0xFF 结尾；
    /// 经 8 字节 VISCA-over-IP 包头封装后由 SendTx 推给 SIMPL+。
    /// </summary>
    public class SonyViscaCamera
    {
        // ---------- 常量 ----------
        public const ushort MaxAddress = 7;
        public const ushort MinPreset = 1;
        public const ushort MaxPreset = 12;
        public const ushort SpeedLow = 1;   // speed1
        public const ushort SpeedHigh = 10; // speed2
        public const ushort DefaultSpeed = 15;

        // 反馈 id 约定（SIMPL+ 端按此路由到 _fb 引脚）
        public const ushort FbAddress = 1;     // 当前摄像机地址（数字）
        public const ushort FbSpeed = 2;       // 当前云台速度（模拟）
        public const ushort FbPresetSave = 3;  // 预置位保存模式（数字）

        // ---------- 状态 ----------
        public ushort Address { get; private set; }      // 当前摄像机 1..7
        public ushort Speed { get; private set; }        // 云台速度
        public bool PresetSaveMode { get; private set; } // 预置位保存模式

        // ---------- 输出委托（SIMPL+ 用 RegisterDelegate 订阅） ----------
        /// <summary>发命令包：camIndex=目标摄像机(1..7)，packet=完整 VISCA-over-IP 包。</summary>
        public delegate void TxDelegate(ushort camIndex, SimplSharpString packet);
        public TxDelegate SendTx { get; set; }

        /// <summary>状态回报：id 见 Fb* 常量，value=数字值/模拟值。</summary>
        public delegate void StatusDelegate(ushort id, ushort value);
        public StatusDelegate ReportStatus { get; set; }

        public SonyViscaCamera()
        {
            Address = 1;
            Speed = DefaultSpeed;
            PresetSaveMode = false;
        }

        // ---------- 对外控制方法（SIMPL+ 引脚直接调） ----------

        /// <summary>
        /// 推送默认状态（VTP 立即有显示，避免初始化空白）：
        /// 当前摄像机 1、速度默认、预置位保存模式 off。SIMPL+ 在 Main 里 RegisterDelegate 后调用一次。
        /// </summary>
        public void PushDefaultStates()
        {
            RaiseStatus(FbAddress, Address);                 // 默认摄像机 1
            RaiseStatus(FbSpeed, Speed);                     // 默认速度
            RaiseStatus(FbPresetSave, 0);                    // 保存模式 off
        }

        /// <summary>选择摄像机（1..7），更新地址并回报。</summary>
        public void Select(ushort cam)
        {
            if (cam < 1 || cam > MaxAddress) return;
            Address = cam;
            RaiseStatus(FbAddress, cam);
        }

        /// <summary>设置云台速度：which=1 低速，which=2 高速。</summary>
        public void SetSpeed(ushort which)
        {
            Speed = (which == 1) ? SpeedLow : SpeedHigh;
            RaiseStatus(FbSpeed, Speed);
        }

        /// <summary>变焦：放大 / 缩小 / 停止。</summary>
        public void ZoomIn()  { Send(0x01, 0x04, 0x07, 0x23, 0xFF); }
        public void ZoomOut() { Send(0x01, 0x04, 0x07, 0x33, 0xFF); }
        public void ZoomStop(){ Send(0x01, 0x04, 0x07, 0x00, 0xFF); }

        /// <summary>云台方向：1=上 2=下 3=左 4=右（按住期间持续移动，松手调 PanTiltStop）。</summary>
        public void PanTilt(ushort dir)
        {
            byte pan, tilt;
            switch (dir)
            {
                case 1: pan = 0x03; tilt = 0x01; break; // 上
                case 2: pan = 0x03; tilt = 0x02; break; // 下
                case 3: pan = 0x01; tilt = 0x03; break; // 左
                case 4: pan = 0x02; tilt = 0x03; break; // 右
                default: return;
            }
            byte v = (byte)Speed;
            Send(0x01, 0x06, 0x01, v, v, pan, tilt, 0xFF);
        }

        /// <summary>云台停止（松开方向键时调用）。</summary>
        public void PanTiltStop() { Send(0x01, 0x06, 0x01, 0x18, 0x18, 0x03, 0x03, 0xFF); }

        /// <summary>进入预置位保存模式（先按此键，再按 Recall(n) 即为保存）。</summary>
        public void PresetSave()
        {
            PresetSaveMode = true;
            RaiseStatus(FbPresetSave, 1);
        }

        /// <summary>预置位：保存模式下为保存，否则为调用。n=1..12。</summary>
        public void Recall(ushort n)
        {
            if (n < MinPreset || n > MaxPreset) return;
            byte op = PresetSaveMode ? (byte)0x01 : (byte)0x02; // 01 保存 / 02 调用
            Send(0x01, 0x04, 0x3F, op, (byte)(n - 1), 0xFF);
            PresetSaveMode = false;
            RaiseStatus(FbPresetSave, 0);
        }

        /// <summary>AI 追踪：1=开，0=关。</summary>
        public void AiTrack(ushort on)
        {
            Send(0x01, 0x7E, 0x04, 0x3A, (byte)(on != 0 ? 1 : 0), 0xFF);
        }

        /// <summary>串口接收入口：收到摄像机应答时调用，按 0xFF 分帧并打日志。</summary>
        public void OnDataReceived(SimplSharpString data)
        {
            var frames = ResponseParser.SplitFrames(data, 0xFF);
            foreach (var f in frames)
                DeviceManager.Log("VISCA-RX", "帧: " + PacketBuilder.ToHex(f));
            // TODO: 解析完成包(90 50..FF) / 错误包(90 6y..FF) 并回报状态
        }

        // ---------- 内部：组包与发送 ----------

        /// <summary>命令前拼上地址字节 0x80+Address，发出一条 VISCA 命令。</summary>
        private void Send(params byte[] command)
        {
            byte[] full = new byte[command.Length + 1];
            full[0] = (byte)(0x80 + Address);
            Array.Copy(command, 0, full, 1, command.Length);
            if (SendTx != null)
                SendTx(Address, PacketBuilder.ToSimplSharpString(BuildViscaIpPacket(full)));
        }

        /// <summary>
        /// 8 字节 VISCA-over-IP 包头 + 负载：
        ///   [0..1]=0x01 0x00（负载类型=命令）
        ///   [2..3]=负载长度（大端，C# 动态计算，无需硬编码）
        ///   [4..7]=序号（这里固定 1）
        /// </summary>
        private static byte[] BuildViscaIpPacket(byte[] payload)
        {
            byte[] p = new byte[8 + payload.Length];
            p[0] = 0x01; p[1] = 0x00;
            p[2] = (byte)(payload.Length >> 8);
            p[3] = (byte)(payload.Length & 0xFF);
            p[4] = 0x00; p[5] = 0x00; p[6] = 0x00; p[7] = 0x01;
            Array.Copy(payload, 0, p, 8, payload.Length);
            return p;
        }

        private void RaiseStatus(ushort id, ushort value)
        {
            if (ReportStatus != null) ReportStatus(id, value);
        }
    }
}
