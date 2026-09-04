using System;
using System.Text;

namespace CrestronDeviceLibrary.Common
{
    /// <summary>
    /// MQTT 3.1.1 最小协议层（纯 C#，无 Crestron 依赖）。
    /// 只实现"中控当 broker"所需的服务端报文：CONNECT/SUBSCRIBE/PUBLISH(QoS0/1)/PINGREQ/DISCONNECT 解析，
    /// 以及 CONNACK/SUBACK/PUBACK/PINGRESP/PUBLISH 组包。
    /// 3 代(.NET CF 3.5, C#3 语法) / 4 代(.NET 4.7.2) 共用同一份代码；
    /// 不含 Crestron 类型，因此可在 Windows 上用 csc 单独编译做离线联调。
    /// </summary>
    public static class MqttProtocol
    {
        // 报文类型（fixed header 高 4 位）
        public const byte TypeConnect = 1;
        public const byte TypeConnAck = 2;
        public const byte TypePublish = 3;
        public const byte TypePubAck = 4;
        public const byte TypeSubscribe = 8;
        public const byte TypeSubAck = 9;
        public const byte TypePingReq = 12;
        public const byte TypePingResp = 13;
        public const byte TypeDisconnect = 14;

        /// <summary>一个解析完成的 MQTT 报文。</summary>
        public class Packet
        {
            public byte Type;      // 报文类型
            public byte Flags;     // fixed header 低 4 位（PUBLISH 的 dup/qos/retain）
            public byte[] Body;    // 可变头 + payload 整段
            public int BodyLen;
        }

        /// <summary>CONNECT 报文解析结果。</summary>
        public class ConnectInfo
        {
            public string ClientId = "";
            public string UserName = "";
            public string Password = "";
            public string WillTopic = "";
            public string WillMessage = "";
            public ushort KeepAliveSec;
            public bool CleanSession;
        }

        /// <summary>从 buf[0..count) 尝试切出一个完整报文。返回消费的字节数（0=数据不足）。</summary>
        public static int TryReadPacket(byte[] buf, int count, out Packet pkt)
        {
            pkt = null;
            if (count < 2) return 0;

            // remaining length：1-4 字节变长
            int multiplier = 1;
            int remaining = 0;
            int pos = 1;
            byte digit;
            do
            {
                if (pos >= count) return 0;          // remaining length 未收全
                digit = buf[pos++];
                remaining += (digit & 127) * multiplier;
                multiplier *= 128;
            } while ((digit & 128) != 0);

            if (remaining < 0 || pos + remaining > count) return 0;   // body 未收全

            pkt = new Packet();
            pkt.Type = (byte)(buf[0] >> 4);
            pkt.Flags = (byte)(buf[0] & 0x0F);
            pkt.BodyLen = remaining;
            pkt.Body = new byte[remaining];
            if (remaining > 0)
                Array.Copy(buf, pos, pkt.Body, 0, remaining);
            return pos + remaining;
        }

        /// <summary>解析 CONNECT body。</summary>
        public static ConnectInfo ParseConnect(Packet pkt)
        {
            ConnectInfo info = new ConnectInfo();
            int pos = 0;
            /*string proto =*/ ReadUtf(pkt.Body, ref pos);   // "MQTT"
            /*byte level =*/ ReadByte(pkt.Body, ref pos);    // 协议级别 4
            byte flags = pkt.Body[pos++];
            info.KeepAliveSec = (ushort)((pkt.Body[pos] << 8) | pkt.Body[pos + 1]);
            pos += 2;
            info.CleanSession = (flags & 0x02) != 0;
            bool hasWill = (flags & 0x04) != 0;
            bool hasPass = (flags & 0x40) != 0;
            bool hasUser = (flags & 0x80) != 0;

            info.ClientId = ReadUtf(pkt.Body, ref pos);
            if (hasWill)
            {
                info.WillTopic = ReadUtf(pkt.Body, ref pos);
                info.WillMessage = ReadUtf(pkt.Body, ref pos);
            }
            if (hasUser) info.UserName = ReadUtf(pkt.Body, ref pos);
            if (hasPass) info.Password = ReadUtf(pkt.Body, ref pos);
            return info;
        }

        /// <summary>解析 SUBSCRIBE body，返回报文 ID，topics 输出订阅的主题过滤器列表。</summary>
        public static ushort ParseSubscribe(Packet pkt, out string[] topics)
        {
            int pos = 0;
            ushort packetId = (ushort)((pkt.Body[0] << 8) | pkt.Body[1]);
            pos = 2;
            var list = new System.Collections.Generic.List<string>();
            while (pos < pkt.BodyLen)
            {
                string filter = ReadUtf(pkt.Body, ref pos);
                pos++;   // 请求的 QoS
                list.Add(filter);
            }
            topics = list.ToArray();
            return packetId;
        }

        /// <summary>解析 PUBLISH，输出主题、payload（按 UTF-8 转字符串）与报文 ID（QoS>0 时有效）。</summary>
        public static void ParsePublish(Packet pkt, out string topic, out string payload, out ushort packetId)
        {
            int pos = 0;
            topic = ReadUtf(pkt.Body, ref pos);
            int qos = (pkt.Flags >> 1) & 0x03;
            packetId = 0;
            if (qos > 0)
            {
                packetId = (ushort)((pkt.Body[pos] << 8) | pkt.Body[pos + 1]);
                pos += 2;
            }
            payload = pkt.BodyLen > pos ? Encoding.UTF8.GetString(pkt.Body, pos, pkt.BodyLen - pos) : "";
        }

        /// <summary>主题过滤器匹配（支持结尾 '#' 多级通配与单层 '+'）。</summary>
        public static bool MatchTopic(string filter, string topic)
        {
            string[] f = filter.Split('/');
            string[] t = topic.Split('/');
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] == "#") return true;            // '#' 必须是最后一级（协议保证）
                if (i >= t.Length) return false;
                if (f[i] == "+") continue;
                if (f[i] != t[i]) return false;
            }
            return f.Length == t.Length;
        }

        /// <summary>从 tele/xxx/LWT 或 cmnd/xxx/# 这类主题里取设备 topic（第二段）。取不到返回 ""。</summary>
        public static string DeviceTopicOf(string topic)
        {
            string[] parts = topic.Split('/');
            return parts.Length >= 2 ? parts[1] : "";
        }

        // ---------------- 组包 ----------------

        public static byte[] BuildConnAck(bool accepted)
        {
            // 0x20 0x02 [session present=0] [rc]
            return new byte[] { 0x20, 0x02, 0x00, accepted ? (byte)0x00 : (byte)0x05 };
        }

        public static byte[] BuildSubAck(ushort packetId, int topicCount)
        {
            byte[] pkt = new byte[4 + topicCount];
            pkt[0] = 0x90;
            pkt[1] = (byte)(2 + topicCount);
            pkt[2] = (byte)(packetId >> 8);
            pkt[3] = (byte)(packetId & 0xFF);
            for (int i = 0; i < topicCount; i++) pkt[4 + i] = 0x00;   // 一律授予 QoS0
            return pkt;
        }

        public static byte[] BuildPubAck(ushort packetId)
        {
            return new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)(packetId & 0xFF) };
        }

        public static byte[] BuildPingResp()
        {
            return new byte[] { 0xD0, 0x00 };
        }

        /// <summary>组一个 QoS0 PUBLISH（broker 向传感器下发 cmnd 用）。</summary>
        public static byte[] BuildPublish(string topic, string payload)
        {
            byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload ?? "");
            int bodyLen = 2 + topicBytes.Length + payloadBytes.Length;

            int headerLen = 1;
            int rl = bodyLen;
            do { headerLen++; rl /= 128; } while (rl > 0);

            byte[] pkt = new byte[headerLen + bodyLen];
            pkt[0] = 0x30;
            int pos = 1;
            int len = bodyLen;
            do
            {
                int d = len % 128; len /= 128;
                if (len > 0) d |= 0x80;
                pkt[pos++] = (byte)d;
            } while (len > 0);
            pkt[pos++] = (byte)(topicBytes.Length >> 8);
            pkt[pos++] = (byte)(topicBytes.Length & 0xFF);
            Array.Copy(topicBytes, 0, pkt, pos, topicBytes.Length); pos += topicBytes.Length;
            if (payloadBytes.Length > 0)
                Array.Copy(payloadBytes, 0, pkt, pos, payloadBytes.Length);
            return pkt;
        }

        // ---------------- 内部 ----------------

        static byte ReadByte(byte[] buf, ref int pos)
        {
            return buf[pos++];
        }

        static string ReadUtf(byte[] buf, ref int pos)
        {
            int len = (buf[pos] << 8) | buf[pos + 1];
            pos += 2;
            string s = Encoding.UTF8.GetString(buf, pos, len);
            pos += len;
            return s;
        }
    }
}
