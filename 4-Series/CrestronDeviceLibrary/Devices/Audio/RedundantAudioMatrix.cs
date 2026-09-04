using System;
using Crestron.SimplSharp;

namespace CrestronDeviceLibrary.Devices
{
    /// <summary>
    /// 音频处理器双机热备（一主一备）共享契约与冗余控制器。
    ///
    /// 设计目标（用户需求）：
    ///   1. 中控同时持有主用/备用两台音频处理器，实时检测两端连接状态；
    ///   2. 任何控制（音量加减、静音、混音路由、预设）都【镜像】到两台，保证配置完全一致；
    ///   3. 平时控制主用；主用断连时【立即】把激活设备切到备用（leader 切换），用户无感；
    ///   4. 主用恢复后自动重新同步并切回主用（主用优先）；
    ///   5. 反馈"哪台掉线"：通过在线状态数字量 + 文本串口输出。
    ///
    /// 架构：泛型 <see cref="RedundantAudioMatrix{T}"/> 持有主备两个 T 实例（T=具体音频矩阵类），
    /// 对外暴露与单台设备一致的命令 API（SIMPL+ 薄壳无需改动命令调用），内部把每条命令镜像到两端；
    /// 通过订阅每个实例的 ConnectionStateChanged 做 leader 选举与重同步。
    /// 反馈只转发"当前激活(leader)设备"的 Digital/Analog/Serial，避免两台数据打架。
    /// </summary>

    // ---- 共享委托（SIMPL+ 注册用，与单台设备委托签名一致）----
    public delegate void MatrixDigitalFb(ushort id, ushort value);
    public delegate void MatrixAnalogFb(ushort id, ushort value);
    public delegate void MatrixSerialFb(ushort id, SimplSharpString text);
    /// <summary>连接状态变化：true=已连，false=断开。</summary>
    public delegate void DeviceConnectionHandler(bool online);

    /// <summary>音频矩阵设备的统一契约（BiampTesiraMatrix / StageCraftMatrix 均实现）。</summary>
    public interface IMatrixControl
    {
        MatrixDigitalFb DigitalFb { get; set; }
        MatrixAnalogFb AnalogFb { get; set; }
        MatrixSerialFb SerialFb { get; set; }
        event DeviceConnectionHandler ConnectionStateChanged;

        void Configure(SimplSharpString ip, ushort port);
        void Start();
        void Stop();

        void SetInputMute(ushort ch, ushort mute);
        void ToggleInputMute(ushort ch);
        void SetOutputMute(ushort ch, ushort mute);
        void ToggleOutputMute(ushort ch);
        void AllMute(ushort mute);
        void InputLevelAdd(ushort ch);
        void InputLevelSub(ushort ch);
        void OutputLevelAdd(ushort ch);
        void OutputLevelSub(ushort ch);
        void SetInputLevel(ushort ch, int db);
        void SetOutputLevel(ushort ch, int db);
        void SetInputLevelAnalog(ushort ch, ushort analog);
        void SetOutputLevelAnalog(ushort ch, ushort analog);
        void SelectOutput(ushort outCh);
        void ToggleRoute(ushort inCh);
        void SetRoute(ushort outCh, ushort inCh, ushort on);
        void ReadMixRoute(ushort outCh);
        void LoadPreset(ushort n);
        void StartLevelPolling(ushort intervalMs);
        void StopLevelPolling();
        void SetPollMode(ushort mode);   // 页面驱动轮询：0=停,1=输入,2=输出,3=混音
    }

    /// <summary>
    /// 泛型双机热备控制器。0=主用(Primary)，1=备用(Backup)。
    /// SIMPL+ 不能直接实例化泛型类，故由具体子类（RedundantBiampMatrix / RedundantStageCraftMatrix）继承。
    ///
    /// 运行机制速览：
    ///   命令下发 → Mirror() 镜像到两台在线设备；
    ///   设备反馈 → WireDevice() 里只转发 leader 的数据到 SIMPL+（避免两台冲突），
    ///              同时 SyncToPeer() 把该台改动同步到对端（保持两台配置一致）；
    ///   掉线切换 → OnConnectionChanged() 立即把 leader 切到在线端（用户无感）；
    ///   恢复补同步 → 上线后延迟 2s Resync() 把期望状态全量补发给刚恢复的设备。
    /// </summary>
    public class RedundantAudioMatrix<T> where T : IMatrixControl, new()
    {
        // ---- 常量 ----
        private const int AnalogMid = 53928;   // 0dB 中点
        private const int DbStep = 963;        // 每 dB 的模拟量刻度
        private const int MaxCh = 64;

        // ---- 反馈委托（SIMPL+ 注册）----
        public MatrixDigitalFb DigitalFb { get; set; }
        public MatrixAnalogFb AnalogFb { get; set; }
        public MatrixSerialFb SerialFb { get; set; }
        /// <summary>主用在线（1=在线，0=掉线）。</summary>
        public MatrixDigitalFb PrimaryOnlineFb { get; set; }
        /// <summary>备用在线（1=在线，0=掉线）。</summary>
        public MatrixDigitalFb BackupOnlineFb { get; set; }
        /// <summary>掉线指示：0=都正常，1=主用掉线，2=备用掉线，3=都掉线。</summary>
        public MatrixDigitalFb DroppedFb { get; set; }
        /// <summary>状态文本（人读）：如 "Primary offline - switched to Backup"。</summary>
        public MatrixSerialFb StatusTextFb { get; set; }

        // ---- 受保护字段（子类用于访问具体设备实例）----
        protected T _primary;
        protected T _backup;

        // ---- 运行时状态 ----
        private int _channels = 16;
        private bool[] _inMute;
        private bool[] _outMute;
        private bool[,] _route;          // [输出, 输入]
        private ushort[] _inAnalog;      // 每路输入电平（模拟量，用于重同步）
        private ushort[] _outAnalog;
        private ushort _selectedOut;
        private bool _allMute;
        private ushort _lastPreset;

        private readonly bool[] _online = new bool[2];
        private int _leader = 0;         // 当前激活设备：0=主用，1=备用，-1=都掉线

        // ---- 防循环：设备推送→同步对端→对端推送→再同步回来的无限循环 ----
        // 用"正在同步"标记：同步对端时设置，对端推送回来时跳过
        private bool _syncingToPeer;

        public RedundantAudioMatrix()
        {
            ConfigureChannels(16);
            _primary = new T();
            _backup = new T();
            WireDevice(0, _primary);
            WireDevice(1, _backup);
        }

        /// <summary>设置通道数（对应 .usp 的 #DEFINE_CONSTANT CH）。1~64。</summary>
        public void ConfigureChannels(ushort channels)
        {
            if (channels < 1) channels = 1;
            if (channels > MaxCh) channels = MaxCh;
            _channels = channels;
            _inMute = new bool[_channels + 1];
            _outMute = new bool[_channels + 1];
            _route = new bool[_channels + 1, _channels + 1];
            _inAnalog = new ushort[_channels + 1];
            _outAnalog = new ushort[_channels + 1];
            // 电平缓存初始化为 0dB 中点：与设备默认增益一致，避免启动后第一次
            // InputLevelAdd/Sub 基于 0（实际 -56dB）起算产生跳变；订阅推送随后会校正为真实值。
            for (int i = 0; i <= _channels; i++) { _inAnalog[i] = AnalogMid; _outAnalog[i] = AnalogMid; }
        }

        // ---- 连接配置（SIMPL+ 在 Main 里调用）----
        public void ConfigurePrimary(SimplSharpString ip, ushort port) { _primary.Configure(ip, port); }
        public void ConfigureBackup(SimplSharpString ip, ushort port) { _backup.Configure(ip, port); }

        /// <summary>开始连接两台设备（异步）。轮询由各设备类内部自行管理（Biamp 订阅模式忽略、StageCraft 连接后自启），此处不重复调用。</summary>
        public void Start()
        {
            _primary.Start();
            _backup.Start();
        }

        public void Stop()
        {
            _primary.Stop();
            _backup.Stop();
        }

        // =====================================================================
        //  命令 API：更新"期望状态" + 镜像到两端在线设备
        // =====================================================================
        public void SetInputMute(ushort ch, ushort mute)
        {
            if (ch < 1 || ch > _channels) return;
            _inMute[ch] = mute != 0;
            Mirror(d => d.SetInputMute(ch, mute));
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
            Mirror(d => d.SetOutputMute(ch, mute));
        }
        public void ToggleOutputMute(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            SetOutputMute(ch, (ushort)(_outMute[ch] ? 0 : 1));
        }
        public void AllMute(ushort mute)
        {
            _allMute = mute != 0;
            Mirror(d => d.AllMute(mute));
        }

        public void InputLevelAdd(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            _inAnalog[ch] = ClampAnalog(_inAnalog[ch] + DbStep);
            Mirror(d => d.InputLevelAdd(ch));
        }
        public void InputLevelSub(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            _inAnalog[ch] = ClampAnalog(_inAnalog[ch] - DbStep);
            Mirror(d => d.InputLevelSub(ch));
        }
        public void OutputLevelAdd(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            _outAnalog[ch] = ClampAnalog(_outAnalog[ch] + DbStep);
            Mirror(d => d.OutputLevelAdd(ch));
        }
        public void OutputLevelSub(ushort ch)
        {
            if (ch < 1 || ch > _channels) return;
            _outAnalog[ch] = ClampAnalog(_outAnalog[ch] - DbStep);
            Mirror(d => d.OutputLevelSub(ch));
        }
        public void SetInputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > _channels) return;
            _inAnalog[ch] = ClampAnalog(AnalogMid + db * DbStep);
            Mirror(d => d.SetInputLevel(ch, db));
        }
        public void SetOutputLevel(ushort ch, int db)
        {
            if (ch < 1 || ch > _channels) return;
            _outAnalog[ch] = ClampAnalog(AnalogMid + db * DbStep);
            Mirror(d => d.SetOutputLevel(ch, db));
        }
        public void SetInputLevelAnalog(ushort ch, ushort analog)
        {
            if (ch < 1 || ch > _channels) return;
            _inAnalog[ch] = analog;
            Mirror(d => d.SetInputLevelAnalog(ch, analog));
        }
        public void SetOutputLevelAnalog(ushort ch, ushort analog)
        {
            if (ch < 1 || ch > _channels) return;
            _outAnalog[ch] = analog;
            Mirror(d => d.SetOutputLevelAnalog(ch, analog));
        }

        public void SelectOutput(ushort outCh)
        {
            if (outCh < 1 || outCh > _channels) return;
            _selectedOut = outCh;
            Mirror(d => d.SelectOutput(outCh));
        }
        public void ToggleRoute(ushort inCh)
        {
            if (inCh < 1 || inCh > _channels || _selectedOut < 1) return;
            _route[_selectedOut, inCh] = !_route[_selectedOut, inCh];
            // 关键：不能把"翻转命令"镜像到两端——翻转命令读设备当前值取反，而路由反馈同步
            // 已把该值 SetRoute 到对端，对端再 Toggle 会读到同步值又翻回去 → 激活/取消死循环。
            // 必须像静音那样：先从本层缓存算绝对 on/off，镜像绝对 SetRoute（幂等，不会回环）。
            SetRoute(_selectedOut, inCh, (ushort)(_route[_selectedOut, inCh] ? 1 : 0));
        }
        public void SetRoute(ushort outCh, ushort inCh, ushort on)
        {
            if (outCh < 1 || outCh > _channels || inCh < 1 || inCh > _channels) return;
            _route[outCh, inCh] = on != 0;
            Mirror(d => d.SetRoute(outCh, inCh, on));
        }
        public void ReadMixRoute(ushort outCh) { Mirror(d => d.ReadMixRoute(outCh)); }
        public void LoadPreset(ushort n) { _lastPreset = n; Mirror(d => d.LoadPreset(n)); }
        public void StartLevelPolling(ushort intervalMs) { Mirror(d => d.StartLevelPolling(intervalMs)); }
        public void StopLevelPolling() { Mirror(d => d.StopLevelPolling()); }
        /// <summary>设置轮询模式（页面驱动），镜像到主备两端。</summary>
        public void SetPollMode(ushort mode) { Mirror(d => d.SetPollMode(mode)); }

        // =====================================================================
        //  内部：镜像 / 反馈转发 / leader 选举 / 重同步
        // =====================================================================
        /// <summary>把动作镜像到两端在线设备（离线端跳过，待恢复后由 Resync 重同步补齐）。</summary>
        /// <param name="act">要对每台在线设备执行的动作（lambda 接收设备实例）。两端各自独立执行，互不阻塞。</param>
        protected void Mirror(Action<T> act)
        {
            if (_online[0]) act(_primary);
            if (_online[1]) act(_backup);
        }

        /// <summary>把模拟量钳制到 ushort 合法范围 [0, 65535]（防溢出回绕）。</summary>
        private static ushort ClampAnalog(int v)
        {
            if (v < 0) v = 0;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

        /// <summary>
        /// 给一台设备接线：注册其数字/模拟/串口/连接状态回调。
        /// idx=0 主用，idx=1 备用。反馈只转发「当前 leader」的数据（避免两台打架），
        /// 同时把每台设备的本地改动经 SyncToPeer 同步到对端，保持两台配置一致。
        /// </summary>
        private void WireDevice(int idx, T dev)
        {
            dev.DigitalFb = (id, v) => {
                if (_leader == idx) { SyncExpectedDigital(id, v); if (DigitalFb != null) DigitalFb(id, v); }
                // 设备端改动（订阅推送）：同步到对端，保持两台配置一致
                SyncToPeer(idx, id, v);
            };
            dev.AnalogFb = (id, v) => {
                if (_leader == idx) { SyncExpectedAnalog(id, v); if (AnalogFb != null) AnalogFb(id, v); }
                // 电平同步到对端（设备端改动 → 从机跟随；Biamp 的 UpdateLevelFb 有 dirty check，
                // 值没变不会触发推送，所以这里只在真实变化时执行，不会风暴）
                SyncToPeerAnalog(idx, id, v);
            };
            dev.SerialFb = (id, t) => { if (_leader == idx && SerialFb != null) SerialFb(id, t); };
            dev.ConnectionStateChanged += online => OnConnectionChanged(idx, online);
        }

        /// <summary>把数字反馈（mute/路由）同步到对端设备（设备端改动 → 对端跟随）。</summary>
        private void SyncToPeer(int srcIdx, ushort id, ushort v)
        {
            if (_syncingToPeer) return;   // 防循环：同步对端时，对端的推送不再触发同步
            int ch = _channels;
            // 输入静音：id 1..CH
            if (id >= 1 && id <= ch)
            {
                ushort muteCh = (ushort)id;
                // 只在对端在线时同步（离线端恢复后 Resync 会补齐）
                if (_online[1 - srcIdx])
                {
                    _syncingToPeer = true;
                    T peer = (1 - srcIdx == 0) ? _primary : _backup;
                    CrestronConsole.PrintLine("[Redundant] SyncToPeer IN mute ch={0} v={1} from idx={2} to idx={3}", muteCh, v, srcIdx, 1 - srcIdx);
                    peer.SetInputMute(muteCh, v);
                    _syncingToPeer = false;
                }
                else
                {
                    CrestronConsole.PrintLine("[Redundant] SyncToPeer SKIP (peer offline) IN mute ch={0} from idx={1}", muteCh, srcIdx);
                }
            }
            // 输出静音：id CH+1..2CH
            else if (id > ch && id <= 2 * ch)
            {
                ushort muteCh = (ushort)(id - ch);
                if (_online[1 - srcIdx])
                {
                    _syncingToPeer = true;
                    T peer = (1 - srcIdx == 0) ? _primary : _backup;
                    CrestronConsole.PrintLine("[Redundant] SyncToPeer OUT mute ch={0} v={1} from idx={2} to idx={3}", muteCh, v, srcIdx, 1 - srcIdx);
                    peer.SetOutputMute(muteCh, v);
                    _syncingToPeer = false;
                }
                else
                {
                    CrestronConsole.PrintLine("[Redundant] SyncToPeer SKIP (peer offline) OUT mute ch={0} from idx={1}", muteCh, srcIdx);
                }
            }
            // 路由：id 2CH+1..3CH（矩阵输入高亮 = 当前选中输出的交叉点）
            // 设备端改动（如从机直接改路由）→ 同步到对端，保证两台路由配置一致。
            // 早期不同步是怕循环；实际上 _syncingToPeer 标记已保证：同步对端时，
            // 对端推送回来的回调会在本方法开头提前 return，不会回环（与静音/电平同机制）。
            // 用 _selectedOut 定位输出，与 SyncExpectedDigital 里路由回写的取值一致。
            else if (id > 2 * ch && id <= 3 * ch && _selectedOut >= 1)
            {
                ushort inCh = (ushort)(id - 2 * ch);
                if (_online[1 - srcIdx])
                {
                    _syncingToPeer = true;
                    T peer = (1 - srcIdx == 0) ? _primary : _backup;
                    CrestronConsole.PrintLine("[Redundant] SyncToPeer route in={0} v={1} (out={2}) from idx={3} to idx={4}",
                        inCh, v, _selectedOut, srcIdx, 1 - srcIdx);
                    peer.SetRoute(_selectedOut, inCh, v);
                    _syncingToPeer = false;
                }
            }
            // FbMixOut 输出选择高亮（3CH+1..4CH）由 SelectOutput 命令镜像同步，不在此回补；
            // meter（模拟）与其他 id 不同步。
        }

        /// <summary>把模拟反馈（电平）同步到对端设备。</summary>
        private void SyncToPeerAnalog(int srcIdx, ushort id, ushort v)
        {
            if (_syncingToPeer) return;   // 防循环
            int ch = _channels;
            // 输入电平：id 1..CH
            if (id >= 1 && id <= ch)
            {
                ushort levelCh = (ushort)id;
                if (_online[1 - srcIdx])
                {
                    _syncingToPeer = true;
                    T peer = (1 - srcIdx == 0) ? _primary : _backup;
                    peer.SetInputLevelAnalog(levelCh, v);
                    _syncingToPeer = false;
                }
            }
            // 输出电平：id CH+1..2CH
            else if (id > ch && id <= 2 * ch)
            {
                ushort levelCh = (ushort)(id - ch);
                if (_online[1 - srcIdx])
                {
                    _syncingToPeer = true;
                    T peer = (1 - srcIdx == 0) ? _primary : _backup;
                    peer.SetOutputLevelAnalog(levelCh, v);
                    _syncingToPeer = false;
                }
            }
            // 2CH+ 为 meter，是只读信号，不同步
        }

        // =====================================================================
        //  设备端改动回流：leader 推送回来的真实状态回写"期望状态"缓存。
        //  只改内存字段、绝不触发命令发送，故不构成反馈→命令的循环。
        //  这样设备本地/Web 端改的路由/静音/电平也能同步到冗余层，
        //  避免冗余层缓存与设备真实状态漂移（Toggle 取反、Resync 都以它为基准）。
        // =====================================================================
        private void SyncExpectedDigital(ushort id, ushort v)
        {
            int ch = _channels;
            if (id >= 1 && id <= ch) _inMute[id] = v != 0;                       // 输入静音
            else if (id > ch && id <= 2 * ch) _outMute[id - ch] = v != 0;        // 输出静音
            else if (id > 2 * ch && id <= 3 * ch)                                 // 矩阵输入高亮 = 当前选中输出的交叉点
            {
                if (_selectedOut >= 1) _route[_selectedOut, id - 2 * ch] = v != 0;
            }
            // 3CH+1..4CH 为输出选择高亮（SelectOutput 回显）、4CH+ 为预设/全静音回显，无需回写
        }

        private void SyncExpectedAnalog(ushort id, ushort v)
        {
            int ch = _channels;
            if (id >= 1 && id <= ch) _inAnalog[id] = v;                          // 输入电平
            else if (id > ch && id <= 2 * ch) _outAnalog[id - ch] = v;           // 输出电平
            // 2CH+ 为信号表读数，无对应期望状态
        }

        private void OnConnectionChanged(int idx, bool online)
        {
            _online[idx] = online;
            if (online)
            {
                // 设备恢复：先推送在线状态，Resync 延迟执行（避免订阅建立前发 288 条命令风暴）
                // Resync 改为延迟 2s（等设备订阅/登录就绪），且只对"刚上线"的设备执行
                if (_leader < 0 || !_online[_leader]) _leader = idx;
                else if (idx == 0 && _leader == 1) _leader = 0;
                // 延迟 Resync：设备刚连接时 TCP 通了但订阅未建立，立即发 288 条命令会风暴/丢失
                // （3 系 CPU 也扛不住）。延迟 2s 后设备就绪再补齐状态。
                int devIdx = idx;
                CrestronConsole.PrintLine("[Redundant] device idx={0} online, schedule Resync in 2s", idx);
                new CTimer(o => { if (_online[devIdx]) { CrestronConsole.PrintLine("[Redundant] Resync idx={0}", devIdx); Resync(devIdx); } }, null, 2000, 0);
            }
            else
            {
                if (idx == _leader)
                {
                    // 当前激活设备掉线：立即切换（用户无感）
                    _leader = _online[1 - idx] ? (1 - idx) : -1;
                }
            }
            RaiseStatus();
        }

        /// <summary>把"期望状态"重新下发到指定设备，使其与主用完全一致。</summary>
        private void Resync(int idx)
        {
            T d = (idx == 0) ? _primary : _backup;
            for (ushort ch = 1; ch <= _channels; ch++)
            {
                d.SetInputMute(ch, (ushort)(_inMute[ch] ? 1 : 0));
                d.SetOutputMute(ch, (ushort)(_outMute[ch] ? 1 : 0));
                d.SetInputLevelAnalog(ch, _inAnalog[ch]);
                d.SetOutputLevelAnalog(ch, _outAnalog[ch]);
            }
            for (ushort o = 1; o <= _channels; o++)
                for (ushort i = 1; i <= _channels; i++)
                    d.SetRoute(o, i, (ushort)(_route[o, i] ? 1 : 0));
            d.SelectOutput(_selectedOut);
            d.AllMute((ushort)(_allMute ? 1 : 0));
            if (_lastPreset != 0) d.LoadPreset(_lastPreset);
        }

        private void RaiseStatus()
        {
            if (PrimaryOnlineFb != null) PrimaryOnlineFb(1, _online[0] ? (ushort)1 : (ushort)0);
            if (BackupOnlineFb != null) BackupOnlineFb(1, _online[1] ? (ushort)1 : (ushort)0);
            int dropped = (_online[0] ? 0 : 1) | (_online[1] ? 0 : 2);
            if (DroppedFb != null) DroppedFb(1, (ushort)dropped);
            if (StatusTextFb != null)
                StatusTextFb(1, new SimplSharpString(BuildStatusText()));
            CrestronConsole.PrintLine("[Redundant] Status online=[{0},{1}] leader={2} (fb={3}/{4})",
                _online[0], _online[1], _leader,
                PrimaryOnlineFb != null ? "set" : "null",
                BackupOnlineFb != null ? "set" : "null");
        }

        private string BuildStatusText()
        {
            string active = (_leader == 0) ? "Primary" : (_leader == 1 ? "Backup" : "None");
            if (!_online[0] && !_online[1]) return "Both offline";
            if (!_online[0]) return "Primary offline (active: Backup)";
            if (!_online[1]) return "Backup offline (active: Primary)";
            return "All online (active: " + active + ")";
        }
    }

    /// <summary>Biamp Tesira 双机热备（具体子类，供 SIMPL+ 实例化）。</summary>
    public class RedundantBiampMatrix : RedundantAudioMatrix<BiampTesiraMatrix>
    {
        // 显式无参构造：SIMPL+ 实例化具体子类时更稳妥（不依赖编译器隐式构造）。
        public RedundantBiampMatrix() { }

        public void ConfigureTags(SimplSharpString inLevelTag, SimplSharpString outLevelTag,
            SimplSharpString mixerTag, SimplSharpString inMeterTag, SimplSharpString outMeterTag)
        {
            ((BiampTesiraMatrix)_primary).ConfigureTags(inLevelTag, outLevelTag, mixerTag, inMeterTag, outMeterTag);
            ((BiampTesiraMatrix)_backup).ConfigureTags(inLevelTag, outLevelTag, mixerTag, inMeterTag, outMeterTag);
        }
        public void ConfigureCredentials(SimplSharpString username, SimplSharpString password)
        {
            ((BiampTesiraMatrix)_primary).ConfigureCredentials(username, password);
            ((BiampTesiraMatrix)_backup).ConfigureCredentials(username, password);
        }
        public void ConfigureChannelsBiamp(ushort channels)
        {
            base.ConfigureChannels(channels);
            ((BiampTesiraMatrix)_primary).ConfigureChannels(channels);
            ((BiampTesiraMatrix)_backup).ConfigureChannels(channels);
        }
    }

    /// <summary>StageCraft 双机热备（具体子类，供 SIMPL+ 实例化）。</summary>
    public class RedundantStageCraftMatrix : RedundantAudioMatrix<StageCraftMatrix>
    {
        // StageCraft 无 InstanceTag / 凭据 / 动态通道数，仅需 IP/端口 + Start，无需额外配置方法。
        // 下列"查询"方法为 StageCraftMatrix 特有（不在 IMatrixControl 接口里），故放在本子类，
        // 由 Mirror 镜像到主备两台（页面打开 / 按钮刷新时调用，与单台 .usp 一致）。
        public void ReadAllInputLevels()  { Mirror(d => d.ReadAllInputLevels()); }
        public void ReadAllOutputLevels() { Mirror(d => d.ReadAllOutputLevels()); }
        public void ReadInputMutes()      { Mirror(d => d.ReadInputMutes()); }
        public void ReadOutputMutes()     { Mirror(d => d.ReadOutputMutes()); }
        public void ReadInputMeter()      { Mirror(d => d.ReadInputMeter()); }
        public void ReadOutputMeter()     { Mirror(d => d.ReadOutputMeter()); }
    }
}
