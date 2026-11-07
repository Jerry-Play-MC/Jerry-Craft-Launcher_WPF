using System;
using System.Security.Cryptography;

namespace Install_Versions
{
    public class AesXts : IDisposable
    {
        private readonly Aes _aesD;
        private readonly Aes _aesT;

        public AesXts(byte[] dKey, byte[] tKey)
        {
            _aesD = Aes.Create();
            _aesD.KeySize = 128;
            _aesD.Key = dKey;
            _aesD.Mode = CipherMode.ECB;
            _aesD.Padding = PaddingMode.None;

            _aesT = Aes.Create();
            _aesT.KeySize = 128;
            _aesT.Key = tKey;
            _aesT.Mode = CipherMode.ECB;
            _aesT.Padding = PaddingMode.None;
        }

        public void Decrypt(byte[] input, int inputOffset, byte[] output, int outputOffset, int length, byte[] tweakIv)
        {
            if (tweakIv.Length < 16)
                throw new ArgumentException("tweakIv 至少 16 字节");

            byte[] tweak = new byte[16];
            using (var encT = _aesT.CreateEncryptor())
                encT.TransformBlock(tweakIv, 0, 16, tweak, 0);

            byte[] xored = new byte[16];
            byte[] decTweak = new byte[16];

            using (var decD = _aesD.CreateDecryptor())
            {
                int blocks = length / 16;
                for (int i = 0; i < blocks; i++)
                {
                    for (int j = 0; j < 16; j++)
                        xored[j] = (byte)(input[inputOffset + i * 16 + j] ^ tweak[j]);

                    decD.TransformBlock(xored, 0, 16, decTweak, 0);

                    for (int j = 0; j < 16; j++)
                        output[outputOffset + i * 16 + j] = (byte)(decTweak[j] ^ tweak[j]);

                    Gf128Mul(tweak);
                }

                int remainder = length % 16;
                if (remainder > 0)
                {
                    int start = blocks * 16;
                    byte[] tail = new byte[16];
                    Buffer.BlockCopy(input, inputOffset + start, tail, 0, remainder);
                    for (int j = 0; j < 16; j++)
                        xored[j] = (byte)(tail[j] ^ tweak[j]);
                    decD.TransformBlock(xored, 0, 16, decTweak, 0);
                    for (int j = 0; j < remainder; j++)
                        output[outputOffset + start + j] = (byte)(decTweak[j] ^ tweak[j]);
                }
            }
        }

        private static void Gf128Mul(byte[] tweak)
        {
            int msb = (tweak[15] >> 7) & 1;
            for (int i = 15; i > 0; i--)
                tweak[i] = (byte)((tweak[i] << 1) | (tweak[i - 1] >> 7));
            tweak[0] <<= 1;
            if (msb != 0)
                tweak[0] ^= 0x87;
        }

        public void Dispose()
        {
            if (_aesD != null) _aesD.Dispose();
            if (_aesT != null) _aesT.Dispose();
        }
    }
}