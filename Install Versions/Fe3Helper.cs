using System;
using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Install_Versions
{
    public static class Fe3Helper
    {
        private const string FE3_URL = "https://fe3cr.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
        private const string DEVICE_ATTRS = "App=WU;AppVer=10.0.16251.1000;AttrDataVer=22;BranchReadinessLevel=CB;CurrentBranch=rs_prerelease;DeviceFamily=Windows.Desktop;FirmwareVersion=6.00;FlightContent=Active;FlightingBranchName=External;InstallLanguage=en-US;OSUILocale=en-US;InstallationType=Client;OSSkuId=48;OSVersion=10.0.19041.0;ProcessorManufacturer=GenuineIntel;OEMName_Uncleaned=Microsoft%20Corporation;OSArchitecture=AMD64;IsFlightingEnabled=0;TelemetryLevel=1;DefaultUserRegion=39070;WuClientVer=1310.2503.26012.0;DeviceFamily=Windows.Desktop";

        public static string GetDownloadUrl(string updateId)
        {
            string now = DateTime.UtcNow.ToString("o");
            string expires = DateTime.UtcNow.AddMinutes(5).ToString("o");

            string soap = string.Format(@"<s:Envelope xmlns:a=""http://www.w3.org/2005/08/addressing"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
<s:Header>
<a:Action s:mustUnderstand=""1"">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/GetExtendedUpdateInfo2</a:Action>
<a:MessageID>urn:uuid:{0}</a:MessageID>
<a:To s:mustUnderstand=""1"">{1}</a:To>
<o:Security s:mustUnderstand=""1"" xmlns:o=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"">
<Timestamp xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">
<Created>{2}</Created><Expires>{3}</Expires>
</Timestamp>
</o:Security>
</s:Header>
<s:Body>
<GetExtendedUpdateInfo2 xmlns=""http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"">
<updateIDs><UpdateIdentity><UpdateID>{4}</UpdateID><RevisionNumber>1</RevisionNumber></UpdateIdentity></updateIDs>
<infoTypes><XmlUpdateFragmentType>FileUrl</XmlUpdateFragmentType></infoTypes>
<deviceAttributes>{5}</deviceAttributes>
</GetExtendedUpdateInfo2>
</s:Body>
</s:Envelope>",
                Guid.NewGuid(), FE3_URL, now, expires, updateId, DEVICE_ATTRS);

            byte[] body = Encoding.UTF8.GetBytes(soap);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;

            var req = (HttpWebRequest)WebRequest.Create(FE3_URL);
            req.Method = "POST";
            req.ContentType = "application/soap+xml; charset=utf-8";
            req.ContentLength = body.Length;

            using (var s = req.GetRequestStream())
                s.Write(body, 0, body.Length);

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string responseXml = reader.ReadToEnd();
                var doc = XDocument.Parse(responseXml);
                XNamespace ns = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

                foreach (var url in doc.Descendants(ns + "Url"))
                {
                    string u = WebUtility.HtmlDecode(url.Value);
                    if (u.Contains("?P1=") || u.Contains("tlu.dl."))
                        return u;
                }
            }

            throw new Exception("FE3 未返回可用 URL");
        }
    }
}