// 區塊職責：**Senate 宿主的 HTTP 抓取器** —— `SCP.Core.Market.ISCP_HttpFetcher` 的實作。
// 物理意義：SCP_Core 刻意不引入網路（它釘在 netstandard2.1 且 **Unity 也要編它**，
//           見 `SCP_RateSource.cs` 守衛①）⇒ 真正會連外的那一段住在宿主這裡。
//           ⇒ 網路出口只在宿主（Senate CLI / Server）。
// 數值影響：只讀不寫（GET）。逾時由呼叫端給，預設由 `rate op=sync --arg timeout_sec` 決定。
// 🩸 守衛：
//   ① **不丟例外**：呼叫端是 Cmd，它要把失敗印成一行字，不是讓整支指令炸掉。
//   ② **非 2xx 一律當失敗**，並把狀態碼與回應前綴帶回去 ——
//      交易所在被限流時會回 200 加一段錯誤 JSON，也會回 418/429；
//      只看「有沒有拿到字串」的話，兩者都長得像成功。
//   ③ **只允許 https**：明文抓來的價格會被中間人改掉，而改掉的價格長得跟真的一樣。
//      （`op=source` 存設定時已擋一次，這裡是第二道 —— 設定檔是可以被手改的。）
#nullable enable
using System.Collections.Generic;
using System.Net.Http;
using SCP.Core.Market;

namespace Senate.Core;

/// <summary>以 <see cref="HttpClient"/> 實作的抓取器。單例共用一個 client（避免 socket 耗盡）。</summary>
public sealed class SenateHttpFetcher : ISCP_HttpHeaderFetcher, ISCP_HttpPoster, ISCP_HttpBytesFetcher, ISCP_HttpMultipartPoster,
                                        ISCP_HttpFormRequester
{
    // ⚠ HttpClient 要**共用**不要每次 new：每次 new 會讓 TIME_WAIT 的 socket 堆起來，
    //   而它的失效樣子是「跑久了之後突然連不出去」，看起來像網路壞了。
    static readonly HttpClient s_Client = new HttpClient();

    public string FetcherName => "senate_http";

    public bool TryGetText(string iUrl, int iTimeoutSec, out string oBody, out string? oError)
        => TryGetText(iUrl, s_NoHeaders, iTimeoutSec, out oBody, out _, out oError);

    static readonly IReadOnlyDictionary<string, string> s_NoHeaders = new Dictionary<string, string>();

    /// <summary>
    /// 帶標頭的 GET（TASK-0319：Discord API）。⚠ 標頭值可能是憑證 ⇒ ⛔ 不寫進錯誤訊息
    /// （回應前綴仍會帶回去 —— 那是對方說的話，不是我們送的）。
    /// </summary>
    public bool TryGetText(string iUrl, IReadOnlyDictionary<string, string> iHeaders, int iTimeoutSec,
                           out string oBody, out int oStatus, out string? oError)
    {
        oStatus = 0;
        oBody = "";
        oError = null;

        if (string.IsNullOrWhiteSpace(iUrl)) { oError = "端點是空的"; return false; }
        if (!iUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        {
            oError = $"端點必須是 https（收到 '{iUrl}'）—— 明文抓來的價格會被中間人改掉，而改掉的價格長得跟真的一樣";
            return false;
        }
        if (iTimeoutSec <= 0) iTimeoutSec = 12;

        try
        {
            using var aReq = new HttpRequestMessage(HttpMethod.Get, iUrl);
            // 有些公開端點會擋沒有 UA 的請求，而擋下來的回應是 200 加一頁 HTML ⇒ 解析器會說「缺欄位」。
            foreach (var kv in iHeaders) aReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (!iHeaders.ContainsKey("User-Agent")) aReq.Headers.TryAddWithoutValidation("User-Agent", "senate-rate-sync/1.0");

            using var aCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(iTimeoutSec));
            using HttpResponseMessage aRes = s_Client.Send(aReq, aCts.Token);

            string aText = aRes.Content.ReadAsStringAsync(aCts.Token).GetAwaiter().GetResult();
            oStatus = (int)aRes.StatusCode;

            if (!aRes.IsSuccessStatusCode)
            {
                // 🩸 狀態碼一定要帶回去：429/418（限流）與 404（端點改了）要吃不同的藥，
                //   而「抓失敗」三個字對兩者都成立、對兩者都沒用。
                oError = $"HTTP {(int)aRes.StatusCode} {aRes.StatusCode}　回應前綴：{Sample(aText)}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(aText))
            {
                oError = "HTTP 200 但回應是空的 —— 那不是「沒有報價」，是沒有收到東西";
                return false;
            }

            oBody = aText;
            return true;
        }
        catch (System.OperationCanceledException)
        {
            oError = $"逾時（{iTimeoutSec}s）";
            return false;
        }
        catch (System.Exception e)
        {
            oError = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>POST JSON（Discord webhook）。⛔ URL 不進錯誤訊息（它帶 webhook token）。</summary>
    public bool TryPostJson(string iUrl, string iJson, int iTimeoutSec,
                            out string oBody, out int oStatus, out double oRetryAfterSec, out string? oError)
    {
        return Post(iUrl, new StringContent(iJson ?? "", System.Text.Encoding.UTF8, "application/json"), iTimeoutSec,
                    out oBody, out oStatus, out oRetryAfterSec, out oError);
    }

    /// <summary>
    /// multipart POST（TASK-0323：Discord webhook 帶圖）：`payload_json` ＋ 檔案段。⛔ URL 不進錯誤訊息。
    /// </summary>
    public bool TryPostMultipart(string iUrl, string iPayloadJson, IReadOnlyList<SCP_HttpFilePart> iFiles, int iTimeoutSec,
                                 out string oBody, out int oStatus, out double oRetryAfterSec, out string? oError)
    {
        var aForm = new MultipartFormDataContent();
        aForm.Add(new StringContent(iPayloadJson ?? "", System.Text.Encoding.UTF8, "application/json"), "payload_json");
        foreach (SCP_HttpFilePart f in iFiles)
        {
            var aPart = new ByteArrayContent(f.Data);
            aPart.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(f.ContentType);
            aForm.Add(aPart, f.FieldName, f.FileName);
        }
        return Post(iUrl, aForm, iTimeoutSec, out oBody, out oStatus, out oRetryAfterSec, out oError);
    }

    /// <summary>
    /// 帶標頭的表單 POST（TASK-0362：Plurk OAuth）。**拿到回應就回 true**（不論狀態碼，body 照填）；
    /// 連線層失敗才回 false、oStatus=0。⛔ 標頭（含簽章）與 URL 不進錯誤訊息。
    /// </summary>
    public bool TryPostForm(string iUrl, IReadOnlyDictionary<string, string> iHeaders,
                            IReadOnlyList<KeyValuePair<string, string>> iFields, IReadOnlyList<SCP_HttpFilePart>? iFiles,
                            int iTimeoutSec, out string oBody, out int oStatus, out string? oError)
    {
        oBody = ""; oStatus = 0; oError = null;
        if (string.IsNullOrWhiteSpace(iUrl) || !iUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        { oError = "端點必須是 https"; return false; }
        if (iTimeoutSec <= 0) iTimeoutSec = 30;
        try
        {
            HttpContent aContent;
            if (iFiles == null)
                aContent = new FormUrlEncodedContent(iFields ?? new List<KeyValuePair<string, string>>());
            else
            {
                var aForm = new MultipartFormDataContent("SenateForm" + System.Guid.NewGuid().ToString("N"));
                if (iFields != null)
                    foreach (KeyValuePair<string, string> kv in iFields) aForm.Add(new StringContent(kv.Value ?? ""), kv.Key);
                foreach (SCP_HttpFilePart f in iFiles)
                {
                    var aPart = new ByteArrayContent(f.Data);
                    aPart.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(f.ContentType);
                    aForm.Add(aPart, f.FieldName, f.FileName);
                }
                aContent = aForm;
            }
            using var aReq = new HttpRequestMessage(HttpMethod.Post, iUrl) { Content = aContent };
            if (iHeaders != null)
                foreach (KeyValuePair<string, string> h in iHeaders) aReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
            using var aCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(iTimeoutSec));
            using HttpResponseMessage aRes = s_Client.Send(aReq, aCts.Token);
            oStatus = (int)aRes.StatusCode;
            using var aReader = new System.IO.StreamReader(aRes.Content.ReadAsStream(aCts.Token), System.Text.Encoding.UTF8);
            oBody = aReader.ReadToEnd();
            return true;
        }
        catch (System.OperationCanceledException) { oError = $"逾時（{iTimeoutSec}s）"; return false; }
        catch (System.Exception e) { oError = e.GetType().Name + ": " + e.Message; return false; }
    }

    /// <summary>
    /// 抓位元組（TASK-0323：Discord 附件）。超過 <paramref name="iMaxBytes"/> ⇒ 中止回 false（邊讀邊數，⛔ 不先整包讀完）。
    /// ⛔ URL 不進錯誤訊息（附件網址帶簽章）。
    /// </summary>
    public bool TryGetBytes(string iUrl, long iMaxBytes, int iTimeoutSec, out byte[] oData, out int oStatus, out string? oError)
    {
        oData = System.Array.Empty<byte>(); oStatus = 0; oError = null;
        if (string.IsNullOrWhiteSpace(iUrl) || !iUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        { oError = "端點必須是 https"; return false; }
        if (iTimeoutSec <= 0) iTimeoutSec = 30;
        try
        {
            using var aReq = new HttpRequestMessage(HttpMethod.Get, iUrl);
            aReq.Headers.TryAddWithoutValidation("User-Agent", "DiscordBot (senate, 1.0)");
            using var aCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(iTimeoutSec));
            using HttpResponseMessage aRes = s_Client.Send(aReq, HttpCompletionOption.ResponseHeadersRead, aCts.Token);
            oStatus = (int)aRes.StatusCode;
            if (!aRes.IsSuccessStatusCode) { oError = $"HTTP {oStatus} {aRes.StatusCode}"; return false; }
            long? aLen = aRes.Content.Headers.ContentLength;
            if (aLen.HasValue && aLen.Value > iMaxBytes) { oError = $"太大（{aLen.Value} bytes）"; return false; }
            using var aStream = aRes.Content.ReadAsStream(aCts.Token);
            using var aMem = new System.IO.MemoryStream();
            byte[] aBuf = new byte[81920];
            int n;
            while ((n = aStream.Read(aBuf, 0, aBuf.Length)) > 0)
            {
                aMem.Write(aBuf, 0, n);
                if (aMem.Length > iMaxBytes) { oError = $"太大（超過 {iMaxBytes} bytes）"; return false; }
            }
            oData = aMem.ToArray();
            return true;
        }
        catch (System.OperationCanceledException) { oError = $"逾時（{iTimeoutSec}s）"; return false; }
        catch (System.Exception e) { oError = e.GetType().Name; return false; }   // ⛔ 不帶 e.Message：HttpRequestException 的訊息可能含網址
    }

    static bool Post(string iUrl, HttpContent iContent, int iTimeoutSec,
                     out string oBody, out int oStatus, out double oRetryAfterSec, out string? oError)
    {
        oBody = ""; oStatus = 0; oRetryAfterSec = 0; oError = null;
        using HttpContent aContent = iContent;
        if (string.IsNullOrWhiteSpace(iUrl) || !iUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        { oError = "端點必須是 https"; return false; }
        if (iTimeoutSec <= 0) iTimeoutSec = 15;
        try
        {
            using var aReq = new HttpRequestMessage(HttpMethod.Post, iUrl);
            aReq.Headers.TryAddWithoutValidation("User-Agent", "DiscordBot (senate, 1.0)");
            aReq.Content = aContent;
            using var aCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(iTimeoutSec));
            using HttpResponseMessage aRes = s_Client.Send(aReq, aCts.Token);
            oStatus = (int)aRes.StatusCode;
            oBody = aRes.Content.ReadAsStringAsync(aCts.Token).GetAwaiter().GetResult();
            if (aRes.Headers.TryGetValues("Retry-After", out var aRa)
                && double.TryParse(System.Linq.Enumerable.FirstOrDefault(aRa), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double aSec)) oRetryAfterSec = aSec;
            if (aRes.IsSuccessStatusCode) return true;
            oError = $"HTTP {oStatus} {aRes.StatusCode}　回應前綴：{Sample(oBody)}";
            return false;
        }
        catch (System.OperationCanceledException) { oError = $"逾時（{iTimeoutSec}s）"; return false; }
        catch (System.Exception e) { oError = $"{e.GetType().Name}: {e.Message}"; return false; }
    }

    static string Sample(string iText)
    {
        string aOne = (iText ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return aOne.Length <= 160 ? aOne : aOne.Substring(0, 160) + "…";
    }
}
