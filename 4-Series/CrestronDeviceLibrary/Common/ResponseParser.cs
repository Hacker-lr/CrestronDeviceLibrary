using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;

namespace CrestronDeviceLibrary.Common
{
    /// <summary>
    /// 串口应答解析：按"帧尾定界符"把字节流切成完整帧。
    /// 适用于 VISCA（0xFF 结束）、以及多数二进制协议。
    /// </summary>
    public static class ResponseParser
    {
#if !SERIES3
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
#endif

        /// <summary>从 SimplSharpString（收到的串口数据）按终止符切帧。</summary>
        public static List<byte[]> SplitFrames(SimplSharpString data, byte terminator)
        {
#if SERIES3
            // .NET CF 3.5 无代码页 28591：按字符低字节还原（VISCA 全 ASCII 命令/应答，低字节即字节）
            string s = data.ToString();
            var raw = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) raw[i] = (byte)(s[i] & 0xFF);
            return SplitFrames(raw, terminator);
#else
            return SplitFrames(Latin1.GetBytes(data.ToString()), terminator);
#endif
        }

        /// <summary>从 byte[] 按终止符切帧；末尾不完整帧（缺终止符）自动丢弃。</summary>
        public static List<byte[]> SplitFrames(byte[] data, byte terminator)
        {
            var frames = new List<byte[]>();
            var frame = new List<byte>(16);
            foreach (byte b in data)
            {
                frame.Add(b);
                if (b == terminator)
                {
                    frames.Add(frame.ToArray());
                    frame.Clear();
                }
            }
            return frames;
        }
    }
}
