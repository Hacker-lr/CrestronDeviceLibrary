using System.Collections.Generic;
using Crestron.SimplSharp;

namespace CrestronDeviceLibrary
{
    /// <summary>
    /// 设备管理器：统一登记/获取受控设备实例 + 统一调试日志。
    ///
    /// 用法：
    ///   DeviceManager.Register("Camera1", myCamera);
    ///   var cam = DeviceManager.Get&lt;SonyViscaCamera&gt;("Camera1");
    ///
    /// 好处：多个 SIMPL+ 模块共享同一实例时，不必各自 new；也便于集中管理设备清单。
    /// </summary>
    public static class DeviceManager
    {
        private static readonly Dictionary<string, object> Devices = new Dictionary<string, object>();

        /// <summary>登记设备（同名覆盖），并打日志。</summary>
        public static void Register(string name, object device)
        {
            if (device == null) return;
            Devices[name] = device;
            Log("DEVICE", "registered: " + name);
        }

        /// <summary>按名字取设备；不存在返回 null。</summary>
        public static T Get<T>(string name) where T : class
        {
            object d;
            return Devices.TryGetValue(name, out d) ? d as T : null;
        }

        /// <summary>统一日志：输出到 Crestron 控制台（Toolbox 可看）。</summary>
        public static void Log(string tag, string message)
        {
            CrestronConsole.PrintLine("[{0}] {1}", tag, message);
        }
    }
}
