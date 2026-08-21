using System.Text;
using Crestron.SimplSharp;

namespace CrestronDeviceLibrary.Common
{
    /// <summary>
    /// 二进制数据工具：byte[] 与 SimplSharpString 互转、十六进制显示。
    /// 供各设备类的组包、日志使用（集中处理"字节保真"这一最容易踩坑的点）。
    /// </summary>
    public static class PacketBuilder
    {
#if SERIES3
        // ---- .NET CF 3.5 兼容：无代码页 28591（Latin-1），手写字节<->字符映射（0..255 一一对应）----
        private static string Latin1ToString(byte[] b)
        {
            var sb = new StringBuilder(b.Length);
            for (int i = 0; i < b.Length; i++) sb.Append((char)b[i]);
            return sb.ToString();
        }
        private static byte[] Latin1ToBytes(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
            return b;
        }
#else
        /// <summary>
        /// Latin-1(ISO-8859-1)：字节 0..255 与字符一一对应，
        /// 保证 0x80-0xFF 高字节不被 SimplSharpString 默认的 ASCII 编码截断。
        /// </summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
#endif

        /// <summary>byte[] → SimplSharpString（Latin-1 保字节，可发往 SIMPL+ / 串口）。</summary>
        public static SimplSharpString ToSimplSharpString(byte[] data)
        {
#if SERIES3
            return new SimplSharpString(Latin1ToString(data));
#else
            return new SimplSharpString(Latin1.GetString(data));
#endif
        }

        /// <summary>SimplSharpString → byte[]（按 Latin-1 还原字节）。</summary>
        public static byte[] FromSimplSharpString(SimplSharpString data)
        {
            if (data == null) return new byte[0];
            string s;
            try { s = data.ToString(); }
            catch { return new byte[0]; }
            if (string.IsNullOrEmpty(s)) return new byte[0];
#if SERIES3
            try { return Latin1ToBytes(s); }
#else
            try { return Latin1.GetBytes(s); }
#endif
            catch { return new byte[0]; }
        }

        /// <summary>byte[] → 十六进制文本，如 "81 01 04 07 23 FF"（调试日志用）。</summary>
        public static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return "(空)";
            var sb = new StringBuilder(data.Length * 3);
            foreach (byte b in data)
                sb.Append(b.ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
