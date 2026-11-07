using System;

namespace Install_Versions
{
    public class CikKey
    {
        public Guid Guid;
        public byte[] TKey;
        public byte[] DKey;

        public CikKey(string hexString)
        {
            byte[] raw = HexToBytes(hexString);
            if (raw.Length != 48)
                throw new ArgumentException("CIK 必须为 48 字节");

            byte[] guidBytes = new byte[16];
            Array.Copy(raw, 0, guidBytes, 0, 16);
            Guid = new Guid(guidBytes);

            TKey = new byte[16];
            Array.Copy(raw, 16, TKey, 0, 16);

            DKey = new byte[16];
            Array.Copy(raw, 32, DKey, 0, 16);
        }

        public static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            if (hex.Length % 2 != 0)
                throw new ArgumentException("hex 长度必须为偶数");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}