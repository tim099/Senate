// 區塊職責：**Senate 宿主的 HTTP 抓取器** —— `SCP.Core.Market.ISCP_HttpFetcher` 的實作。
// 物理意義：SCP_Core 刻意不引入網路（它釘在 netstandard2.1 且 **Unity 也要編它**，
//           見 `SCP_RateSource.cs` 守衛①）⇒ 真正會連外的那一段住在宿主這裡。
//           ⇒ Senate CLI / Server 有網路出口；Unity Editor 端沒有，而那會被**明說**不會靜默。
// 數值影響：只讀不寫（GET）。逾時由呼叫端給，預設由 `rate op=sync --arg timeout_sec` 決定。
// 🩸 守衛：
//   ① **不丟例外**：呼叫端是 Cmd，它要把失敗印成一行字，不是讓整支指令炸掉。
//   ② **非 2xx 一律當失敗**，並把狀態碼與回應前綴帶回去 ——
//      交易所在被限流時會回 200 加一段錯誤 JSON，也會回 418/429；
//      只看「有沒有拿到字串」的話，兩者都長得像成功。
//   ③ **只允許 https**：明文抓來的價格會被中間人改掉，而改掉的價格長得跟真的一樣。
//      （`op=source` 存設定時已擋一次，這裡是第二道 —— 設定檔是可以被手改的。）
#nullable enable
using System.Net.Http;
using SCP.Core.Market;

namespace Senate.Cli;

/// <summary>以 <see cref="HttpClient"/> 實作的抓取器。單例共用一個 client（避免 socket 耗盡）。</summary>
public sealed class SenateHttpFetcher : ISCP_HttpFetcher
{
    // ⚠ HttpClient 要**共用**不要每次 new：每次 new 會讓 TIME_WAIT 的 socket 堆起來，
    //   而它的失效樣子是「跑久了之後突然連不出去」，看起來像網路壞了。
    static readonly HttpClient s_Client = new HttpClient();

    public string FetcherName => "senate_http";

    public bool TryGetText(string iUrl, int iTimeoutSec, out string oBody, out string? oError)
    {
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
            aReq.Headers.TryAddWithoutValidation("User-Agent", "senate-rate-sync/1.0");

            using var aCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(iTimeoutSec));
            using HttpResponseMessage aRes = s_Client.Send(aReq, aCts.Token);

            string aText = aRes.Content.ReadAsStringAsync(aCts.Token).GetAwaiter().GetResult();

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

    static string Sample(string iText)
    {
        string aOne = (iText ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return aOne.Length <= 160 ? aOne : aOne.Substring(0, 160) + "…";
    }
}
