using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Install_Versions
{
    public class Downloader
    {
        public static void DownloadFile(string url, string savePath, Action<long, long> progress = null)
        {
            string[] candidates = BuildCandidates(url);
            Exception lastEx = null;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                try
                {
                    if (candidates.Length > 1 && progress != null)
                        Console.WriteLine("尝试源 " + (i + 1) + "/" + candidates.Length);
                    DownloadSingle(candidate, savePath, progress);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (File.Exists(savePath)) File.Delete(savePath);
                    if (progress != null)
                        Console.WriteLine("源失败: " + ex.Message);
                }
            }
            throw new Exception("所有下载源都失败", lastEx);
        }

        private static void DownloadSingle(string url, string savePath, Action<long, long> progress)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "BedrockInstaller/1.0");

                long lastBytes = 0;
                DateTime lastUpdate = DateTime.MinValue;

                if (progress != null)
                {
                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        var now = DateTime.Now;
                        if ((now - lastUpdate).TotalMilliseconds < 150) return;
                        lastUpdate = now;
                        progress(e.BytesReceived, e.TotalBytesToReceive);
                    };
                }

                var wait = new ManualResetEvent(false);
                Exception error = null;

                wc.DownloadFileCompleted += (s, e) =>
                {
                    error = e.Error;
                    wait.Set();
                };

                wc.DownloadFileAsync(new Uri(url), savePath);
                wait.WaitOne();

                if (error != null) throw error;
            }
        }

        private static string[] BuildCandidates(string url)
        {
            if (!url.Contains("xboxlive"))
                return new[] { url };

            int idx = url.IndexOf("://") + 3;
            int slashIdx = url.IndexOf('/', idx);
            string path = url.Substring(slashIdx);

            return new[]
            {
                url,
                "http://assets1.xboxlive.cn" + path,
                "http://assets2.xboxlive.cn" + path,
                "http://assets1.xboxlive.com" + path,
                "http://assets2.xboxlive.com" + path,
                "http://d1.xboxlive.cn" + path,
                "http://d2.xboxlive.cn" + path,
                "http://d1.xboxlive.com" + path,
                "http://d2.xboxlive.com" + path
            };
        }

        public static bool VerifyMd5(string filePath, string expectedMd5)
        {
            using (var md5 = MD5.Create())
            using (var fs = File.OpenRead(filePath))
            {
                byte[] hash = md5.ComputeHash(fs);
                string actual = BitConverter.ToString(hash).Replace("-", "");
                return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}