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
        /// <summary>
        /// Latin-1(ISO-8859-1)：字节 0..255 与字符一一对应，
        /// 保证 0x80-0xFF 高字节不被 SimplSharpString 默认的 ASCII 编码截断。
        /// </summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>byte[] → SimplSharpString（Latin-1 保字节，可发往 SIMPL+ / 串口）。</summary>
        public static SimplSharpString ToSimplSharpString(byte[] data)
        {
            return new SimplSharpString(Latin1.GetString(data));
        }

        /// <summary>SimplSharpString → byte[]（按 Latin-1 还原字节）。</summary>
        public static byte[] FromSimplSharpString(SimplSharpString data)
        {
            if (data == null) return new byte[0];
            string s;
            try { s = data.ToString(); }
            catch { return new byte[0]; }
            if (string.IsNullOrEmpty(s)) return new byte[0];
            try { return Latin1.GetBytes(s); }
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
