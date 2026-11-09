// 區塊職責：YouTube 串接的底層 —— 憑證（用戶端 JSON、refresh token）、OAuth 登入（本機 loopback ＋ PKCE）、Data API v3 呼叫。
//           `senate cmd youtube`（Cmd_YouTube.cs）是它唯一的呼叫端。
// 物理意義：
//   · 放宿主、不放 SCP_Core：只有 Senate 在用、而且整支都在打網路 ——
//     SCP_Core 撰寫規範 §5「只有一個消費端在用、又碰 IO 的東西是宿主的」＋ SCP_RateSource 守衛①「SCP_Core 不引入網路」。
//   · 憑證住 secrets 資料夾（`SCP_SecretStore.ResolveDir`，現況 `<資料根>/Secret/`，資料根的 .gitignore 擋掉整個資料夾）：
//       `youtube_client.enc`／`.txt` —— Google Cloud 下載的 OAuth 用戶端 JSON（類型必須是「電腦版應用程式」＝ `installed`）
//       `youtube_token.enc`／`.txt`  —— refresh token ＋ 授權到的頻道（`op=login` 寫）
//       `youtube_uploads.json`       —— 本工具上傳過的影片（Cmd_YouTube 的帳；unlisted 的影片 id 就等於網址 ⇒ 不放公開 repo）
//     寫法照 Discord token 那一套：先加密、自己回解驗證、再寫 .enc 與 .txt；密碼從檔案讀，⛔ 不收值本身（參數會進 log）。
//   · access token 每次執行用 refresh token 現換，不落檔。
// 數值影響：
//   · ⛔ client_secret、refresh token、access token 不進任何輸出與錯誤訊息（`Scrub`）。上傳的 session 網址也不印（它本身就是上傳權）。
//   · 登入時聽 127.0.0.1 的隨機埠；等不到就逾時（預設 300 秒），不留常駐的東西。
//   · scope 只要一個 `youtube.force-ssl`（上傳、改資料、縮圖、播放清單、留言都涵蓋）；
//     2026-11-09 Tim 在 Cloud Console 讀到它歸「機密範圍」（sensitive）⇒ 自用不送驗證，登入時會多一頁「尚未驗證」警告。
#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Secret;

namespace Senate.Core;

/// <summary>憑證狀態（⛔ 不含任何密鑰）。</summary>
public sealed class YouTubeCredStatus
{
    public string SecretsDir = "";
    public string DirWarning = "";
    public string ClientEncPath = "";
    public string ClientPlainPath = "";
    public string TokenEncPath = "";
    public string TokenPlainPath = "";
    public bool ClientEncExists;
    public bool ClientReady;
    public bool TokenEncExists;
    public bool TokenReady;
    /// <summary>用戶端的 client_id（不是密鑰；印出來讓人對得上 Cloud Console 那一個）。</summary>
    public string ClientId = "";
    public string ChannelId = "";
    public string ChannelTitle = "";
    public string ObtainedAt = "";
    /// <summary>token 是用哪個 client_id 拿的；跟現在的用戶端不同 ⇒ refresh 一定失敗，要重登。</summary>
    public string TokenClientId = "";
    /// <summary>讀檔時的問題（壞檔、缺欄位）；空 ＝ 沒問題。</summary>
    public string Problem = "";
}

/// <summary>一次 HTTP 的結果。<see cref="Status"/> 0 ＝ 沒拿到回應（看 <see cref="Error"/>）。</summary>
public sealed class YouTubeHttp
{
    public int Status;
    public string Body = "";
    public string Location = "";
    public string Error = "";
    public bool Ok => Status >= 200 && Status < 300;
}

public static class YouTubeClient
{
    public const string Scope = "https://www.googleapis.com/auth/youtube.force-ssl";
    public const string ClientSecretName = "youtube_client";
    public const string TokenSecretName = "youtube_token";
    public const string LedgerFileName = "youtube_uploads.json";

    const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    const string TokenUrl = "https://oauth2.googleapis.com/token";
    const string ApiBase = "https://www.googleapis.com/youtube/v3";
    const string UploadBase = "https://www.googleapis.com/upload/youtube/v3";
    const int ApiTimeoutSec = 30;

    static readonly HttpClient s_Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    // ── 路徑與狀態 ───────────────────────────────────────────────

    public static string SecretsDir(string iDataRoot, out string? oWarning) => SCP_SecretStore.ResolveDir(iDataRoot, out oWarning);

    public static string LedgerPath(string iDataRoot) => SecretsDir(iDataRoot, out _) + "/" + LedgerFileName;

    public static YouTubeCredStatus Status(string iDataRoot)
    {
        var s = new YouTubeCredStatus();
        s.SecretsDir = SecretsDir(iDataRoot, out string? aWarn);
        s.DirWarning = aWarn ?? "";
        s.ClientEncPath = s.SecretsDir + "/" + ClientSecretName + ".enc";
        s.ClientPlainPath = SCP_SecretStore.PlainPathOf(s.ClientEncPath);
        s.TokenEncPath = s.SecretsDir + "/" + TokenSecretName + ".enc";
        s.TokenPlainPath = SCP_SecretStore.PlainPathOf(s.TokenEncPath);
        s.ClientEncExists = File.Exists(s.ClientEncPath);
        s.TokenEncExists = File.Exists(s.TokenEncPath);
        var aProblems = new List<string>();

        if (File.Exists(s.ClientPlainPath))
        {
            if (TryParseClient(ReadText(s.ClientPlainPath), out string aId, out _, out string? aErr)) { s.ClientReady = true; s.ClientId = aId; }
            else aProblems.Add($"用戶端明文讀不懂：{aErr}");
        }
        if (File.Exists(s.TokenPlainPath))
        {
            try
            {
                SCP_JsonData t = SCP_JsonParser.Parse(ReadText(s.TokenPlainPath));
                s.TokenReady = t.GetString("refresh_token", "").Length > 0;
                s.ChannelId = t.GetString("channel_id", "");
                s.ChannelTitle = t.GetString("channel_title", "");
                s.ObtainedAt = t.GetString("obtained_at", "");
                s.TokenClientId = t.GetString("client_id", "");
                if (!s.TokenReady) aProblems.Add("token 明文裡沒有 refresh_token");
            }
            catch (Exception e) { aProblems.Add($"token 明文讀不懂：{e.GetType().Name}"); }
        }
        s.Problem = string.Join("；", aProblems);
        return s;
    }

    // ── set-credentials：用戶端 JSON ────────────────────────────

    /// <summary>
    /// 驗 Google Cloud 下載的用戶端 JSON：要 `installed`（電腦版應用程式）、要有 client_id 與 client_secret。
    /// ⛔ 錯誤訊息不含 client_secret。
    /// </summary>
    public static bool TryParseClient(string iJson, out string oClientId, out string oClientSecret, out string? oError)
    {
        oClientId = ""; oClientSecret = ""; oError = null;
        SCP_JsonData j;
        try { j = SCP_JsonParser.Parse(iJson); }
        catch (Exception e) { oError = $"不是 JSON（{e.GetType().Name}）"; return false; }
        if (!j.IsObject) { oError = "不是 JSON 物件"; return false; }
        if (!j["installed"].IsObject)
        {
            oError = j["web"].IsObject
                ? "這是「網頁應用程式」的用戶端 ⇒ 到 Google Auth Platform →「用戶端」重建一個「電腦版應用程式」"
                : "找不到 `installed` 區段 —— 要用 Google Cloud 下載的用戶端 JSON（類型：電腦版應用程式）";
            return false;
        }
        SCP_JsonData c = j["installed"];
        oClientId = c.GetString("client_id", "").Trim();
        oClientSecret = c.GetString("client_secret", "").Trim();
        if (oClientId.Length == 0) { oError = "`installed.client_id` 是空的"; return false; }
        if (oClientSecret.Length == 0) { oError = "`installed.client_secret` 是空的（Google 只在建立當下給一次；不見了就到「用戶端」新增一組密鑰）"; return false; }
        return true;
    }

    public static bool TrySetClient(string iDataRoot, string iClientJson, string iPassphrase, string iHint, bool iOverwrite, out string? oError)
    {
        string aJson = (iClientJson ?? "").Trim().TrimStart('﻿');
        if (!TryParseClient(aJson, out _, out _, out oError)) return false;
        YouTubeCredStatus s = Status(iDataRoot);
        if (s.ClientEncExists && !iOverwrite) { oError = $"`{Path.GetFileName(s.ClientEncPath)}` 已存在 ⇒ 要覆寫請加 `--arg overwrite=1`（覆寫之後舊密碼解不開新檔）"; return false; }
        return TryWriteSecret(s.SecretsDir, s.ClientEncPath, s.ClientPlainPath, aJson, iPassphrase, iHint, "youtube oauth client", out oError);
    }

    static bool TryLoadClient(string iDataRoot, out string oId, out string oSecret, out string? oError)
    {
        oId = ""; oSecret = "";
        YouTubeCredStatus s = Status(iDataRoot);
        if (!File.Exists(s.ClientPlainPath)) { oError = "還沒有用戶端 ⇒ 先跑 `op=set-credentials`"; return false; }
        return TryParseClient(ReadText(s.ClientPlainPath), out oId, out oSecret, out oError);
    }

    // ── login：loopback ＋ PKCE ──────────────────────────────────

    public sealed class LoginResult
    {
        public string ChannelId = "";
        public string ChannelTitle = "";
        public string Scope = "";
        public bool Replaced;
    }

    /// <summary>
    /// 開瀏覽器讓 Tim 登入 Google、選頻道、同意 ⇒ 換到 refresh token ⇒ 回讀頻道 ⇒ 加密落檔。
    /// <paramref name="iNotice"/> 收「現在請去瀏覽器」那幾行（指令結果要等到最後才印，這幾行要即時看到）。
    /// </summary>
    public static bool TryLogin(string iDataRoot, string iPassphrase, string iHint, int iTimeoutSec, bool iOpenBrowser,
                                Action<string> iNotice, out LoginResult oResult, out string? oError)
    {
        oResult = new LoginResult();
        if (string.IsNullOrEmpty(iPassphrase)) { oError = "沒有密碼（加密 token 用）"; return false; }
        if (!TryLoadClient(iDataRoot, out string aClientId, out string aClientSecret, out oError)) return false;

        string aVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string aChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(aVerifier)));
        string aState = Base64Url(RandomNumberGenerator.GetBytes(16));

        var aListener = new TcpListener(IPAddress.Loopback, 0);
        try { aListener.Start(); }
        catch (Exception e) { oError = $"本機開不了 loopback 埠：{e.Message}"; return false; }
        try
        {
            int aPort = ((IPEndPoint)aListener.LocalEndpoint).Port;
            string aRedirect = "http://127.0.0.1:" + aPort.ToString(CultureInfo.InvariantCulture);
            string aUrl = AuthUrl
                + "?client_id=" + Uri.EscapeDataString(aClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(aRedirect)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&access_type=offline"
                + "&prompt=consent"                         // 每次都問 ⇒ Google 一定回 refresh token
                + "&code_challenge=" + aChallenge
                + "&code_challenge_method=S256"
                + "&state=" + aState;

            iNotice($"🔐 請在瀏覽器登入 Google → 選要運營的頻道 → 看到「尚未驗證」按「進階 → 前往」→ 允許（{iTimeoutSec} 秒內）");
            iNotice("   瀏覽器沒自己打開的話，手動開這個網址：");
            iNotice("   " + aUrl);
            if (iOpenBrowser)
            {
                try { Process.Start(new ProcessStartInfo(aUrl) { UseShellExecute = true }); }
                catch (Exception e) { iNotice($"   ⚠ 開不了瀏覽器（{e.GetType().Name}）⇒ 請手動開上面的網址"); }
            }

            if (!TryAwaitRedirect(aListener, aState, iTimeoutSec, out string aCode, out oError)) return false;

            YouTubeHttp t = PostForm(TokenUrl, new Dictionary<string, string>
            {
                ["code"] = aCode,
                ["client_id"] = aClientId,
                ["client_secret"] = aClientSecret,
                ["redirect_uri"] = aRedirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = aVerifier,
            });
            if (!t.Ok) { oError = "換 token 失敗：" + Scrub(OAuthError(t), aClientSecret, aCode); return false; }

            SCP_JsonData j = SCP_JsonParser.Parse(t.Body);
            string aRefresh = j.GetString("refresh_token", "");
            string aAccess = j.GetString("access_token", "");
            string aScope = j.GetString("scope", "");
            if (aRefresh.Length == 0)
            {
                oError = "Google 沒給 refresh token ⇒ 到 https://myaccount.google.com/permissions 移除這個應用程式的存取權，再登入一次";
                return false;
            }
            if (!aScope.Split(' ').Contains(Scope))
            {
                oError = "授權時沒有勾 YouTube 的權限（同意畫面上那一格要打勾）⇒ 再跑一次 `op=login`";
                return false;
            }

            if (!TryGetJson(aAccess, "/channels?part=snippet&mine=true", out SCP_JsonData ch, out oError)) { oError = "登入成功，但讀不到頻道：" + oError; return false; }
            if (ch["items"].Count == 0) { oError = "登入的帳號底下沒有 YouTube 頻道 ⇒ 先到 YouTube 建頻道，或登入時選對品牌帳號"; return false; }
            oResult.ChannelId = ch["items"][0].GetString("id", "");
            oResult.ChannelTitle = ch["items"][0]["snippet"].GetString("title", "");
            oResult.Scope = aScope;

            YouTubeCredStatus s = Status(iDataRoot);
            oResult.Replaced = s.TokenEncExists || File.Exists(s.TokenPlainPath);
            var aTok = SCP_JsonData.NewObject();
            aTok.Set("refresh_token", aRefresh);
            aTok.Set("scope", aScope);
            aTok.Set("client_id", aClientId);
            aTok.Set("channel_id", oResult.ChannelId);
            aTok.Set("channel_title", oResult.ChannelTitle);
            aTok.Set("obtained_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return TryWriteSecret(s.SecretsDir, s.TokenEncPath, s.TokenPlainPath, aTok.ToJson(true), iPassphrase, iHint, "youtube refresh token", out oError);
        }
        finally { aListener.Stop(); }
    }

    /// <summary>
    /// 等瀏覽器被 Google 轉回 127.0.0.1。只認帶 `code` 或 `error` 的那一次請求（favicon、預先連線之類的照樣回一頁、繼續等）。
    /// </summary>
    static bool TryAwaitRedirect(TcpListener iListener, string iState, int iTimeoutSec, out string oCode, out string? oError)
    {
        oCode = ""; oError = null;
        DateTime aDeadline = DateTime.UtcNow.AddSeconds(iTimeoutSec);
        while (DateTime.UtcNow < aDeadline)
        {
            if (!iListener.Pending()) { Thread.Sleep(200); continue; }
            using TcpClient aClient = iListener.AcceptTcpClient();
            aClient.ReceiveTimeout = 5000;
            aClient.SendTimeout = 5000;
            string aLine;
            NetworkStream aStream = aClient.GetStream();
            try { aLine = ReadRequestLine(aStream); }
            catch (IOException) { continue; }   // 預先連線、什麼都沒送 ⇒ 不算
            Dictionary<string, string> q = ParseQuery(aLine);
            bool aHasCode = q.ContainsKey("code"), aHasError = q.ContainsKey("error");
            bool aStateOk = q.TryGetValue("state", out string? aState) && aState == iState;
            string aMsg = !aHasCode && !aHasError ? "Senate 正在等 Google 的授權回應。"
                        : !aStateOk ? "這不是這一次登入的回應（state 對不上），已丟棄。請回 Senate 重新登入。"
                        : aHasCode ? "授權完成，可以關掉這一頁，回到 Senate。"
                        : "授權沒有完成（" + WebUtility.HtmlEncode(q["error"]) + "）。";
            Respond(aStream, aMsg);
            if (!aHasCode && !aHasError) continue;
            if (!aStateOk) { oError = "回來的 state 對不上 ⇒ 不是這一次登入的回應，已丟棄；再跑一次 `op=login`"; return false; }
            if (aHasError) { oError = q["error"] == "access_denied" ? "在同意畫面按了取消（access_denied）" : "Google 回了錯誤：" + q["error"]; return false; }
            oCode = q["code"];
            return true;
        }
        oError = $"等了 {iTimeoutSec} 秒沒等到瀏覽器回來 ⇒ 再跑一次 `op=login`（可加 `--arg timeout=<秒>`）";
        return false;
    }

    static string ReadRequestLine(NetworkStream iStream)
    {
        var aBytes = new List<byte>();
        while (aBytes.Count < 8192)
        {
            int b = iStream.ReadByte();
            if (b < 0) break;
            if (b == '\n') break;
            if (b != '\r') aBytes.Add((byte)b);
        }
        return Encoding.ASCII.GetString(aBytes.ToArray());
    }

    static Dictionary<string, string> ParseQuery(string iRequestLine)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] aParts = iRequestLine.Split(' ');
        if (aParts.Length < 2) return d;
        int aQ = aParts[1].IndexOf('?');
        if (aQ < 0) return d;
        foreach (string aKv in aParts[1].Substring(aQ + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int aEq = aKv.IndexOf('=');
            string k = Uri.UnescapeDataString((aEq < 0 ? aKv : aKv.Substring(0, aEq)).Replace('+', ' '));
            string v = aEq < 0 ? "" : Uri.UnescapeDataString(aKv.Substring(aEq + 1).Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    static void Respond(NetworkStream iStream, string iMessage)
    {
        byte[] aBody = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Senate × YouTube</title></head>"
            + "<body style=\"font-family:sans-serif;margin:3em\"><h2>" + iMessage + "</h2></body></html>");
        byte[] aHead = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
            + aBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        try { iStream.Write(aHead, 0, aHead.Length); iStream.Write(aBody, 0, aBody.Length); iStream.Flush(); }
        catch (IOException) { /* 瀏覽器先關了 —— 不影響換 token */ }
    }

    // ── access token ────────────────────────────────────────────

    /// <summary>
    /// 用 refresh token 換一顆 access token。回 false 時 <paramref name="oNeedLogin"/> 告訴呼叫端是不是「要重登」（vs 網路問題）。
    /// </summary>
    public static bool TryAccessToken(string iDataRoot, out string oAccess, out bool oNeedLogin, out string? oError)
    {
        oAccess = ""; oNeedLogin = false;
        if (!TryLoadClient(iDataRoot, out string aId, out string aSecret, out oError)) return false;
        YouTubeCredStatus s = Status(iDataRoot);
        if (!s.TokenReady) { oNeedLogin = true; oError = "還沒登入 ⇒ 先跑 `op=login`" + (s.Problem.Length > 0 ? "（" + s.Problem + "）" : ""); return false; }
        if (s.TokenClientId.Length > 0 && s.TokenClientId != aId) { oNeedLogin = true; oError = "token 是舊用戶端拿的（用戶端換過）⇒ 重跑 `op=login`"; return false; }
        string aRefresh = SCP_JsonParser.Parse(ReadText(s.TokenPlainPath)).GetString("refresh_token", "");
        YouTubeHttp t = PostForm(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = aId,
            ["client_secret"] = aSecret,
            ["refresh_token"] = aRefresh,
            ["grant_type"] = "refresh_token",
        });
        if (!t.Ok)
        {
            string e = OAuthError(t);
            oNeedLogin = e.Contains("invalid_grant", StringComparison.Ordinal);
            oError = Scrub(e, aSecret, aRefresh) + (oNeedLogin
                ? " ⇒ refresh token 失效（撤銷了授權、改了密碼、或同意畫面還在「測試」模式 7 天到期）⇒ 重跑 `op=login`"
                : "");
            return false;
        }
        oAccess = SCP_JsonParser.Parse(t.Body).GetString("access_token", "");
        if (oAccess.Length == 0) { oError = "token 端點回了 200 卻沒有 access_token"; return false; }
        return true;
    }

    // ── API ─────────────────────────────────────────────────────

    public static bool TryGetJson(string iAccess, string iPathAndQuery, out SCP_JsonData oJson, out string? oError)
    {
        var aReq = new HttpRequestMessage(HttpMethod.Get, ApiBase + iPathAndQuery);
        aReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", iAccess);
        return Finish(Send(aReq, ApiTimeoutSec), "GET " + StripQuery(iPathAndQuery), iAccess, out oJson, out oError);
    }

    public static bool TrySendJson(string iAccess, HttpMethod iMethod, string iPathAndQuery, string iJson, out SCP_JsonData oJson, out string? oError)
    {
        var aReq = new HttpRequestMessage(iMethod, ApiBase + iPathAndQuery)
        {
            Content = new StringContent(iJson, new UTF8Encoding(false), "application/json"),
        };
        aReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", iAccess);
        return Finish(Send(aReq, ApiTimeoutSec), iMethod.Method + " " + StripQuery(iPathAndQuery), iAccess, out oJson, out oError);
    }

    /// <summary>
    /// resumable upload：先 POST metadata 拿上傳網址，再 PUT 整支檔案（串流，不整支讀進記憶體）。
    /// 回傳的是 API 給的 video 資源（含 id）。⛔ 上傳網址本身就是上傳權 ⇒ 不印。
    /// </summary>
    public static bool TryUploadVideo(string iAccess, string iFile, string iMetaJson, bool iNotifySubscribers, int iTimeoutSec,
                                      out SCP_JsonData oVideo, out string? oError)
    {
        oVideo = SCP_JsonData.NewNull();
        long aSize = new FileInfo(iFile).Length;
        string aMime = VideoMime(iFile);
        var aInit = new HttpRequestMessage(HttpMethod.Post, UploadBase + "/videos?uploadType=resumable&part=snippet,status&notifySubscribers="
            + (iNotifySubscribers ? "true" : "false"))
        {
            Content = new StringContent(iMetaJson, new UTF8Encoding(false), "application/json"),
        };
        aInit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", iAccess);
        aInit.Headers.Add("X-Upload-Content-Length", aSize.ToString(CultureInfo.InvariantCulture));
        aInit.Headers.Add("X-Upload-Content-Type", aMime);
        YouTubeHttp h = Send(aInit, ApiTimeoutSec);
        if (!h.Ok || h.Location.Length == 0)
        {
            oError = h.Ok ? "開上傳時 API 沒給上傳網址" : "開上傳失敗：" + Scrub(ApiError(h), iAccess);
            return false;
        }

        using FileStream aFs = File.OpenRead(iFile);
        var aPut = new HttpRequestMessage(HttpMethod.Put, h.Location) { Content = new StreamContent(aFs, 1 << 20) };
        aPut.Content.Headers.ContentType = new MediaTypeHeaderValue(aMime);
        aPut.Content.Headers.ContentLength = aSize;
        aPut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", iAccess);
        return Finish(Send(aPut, iTimeoutSec), $"上傳檔案（{aSize / 1048576.0:0.0} MB）", iAccess, out oVideo, out oError);
    }

    public static bool TrySetThumbnail(string iAccess, string iVideoId, string iImage, out string? oError)
    {
        string aMime = iImage.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        var aReq = new HttpRequestMessage(HttpMethod.Post, UploadBase + "/thumbnails/set?videoId=" + Uri.EscapeDataString(iVideoId))
        {
            Content = new ByteArrayContent(File.ReadAllBytes(iImage)),
        };
        aReq.Content.Headers.ContentType = new MediaTypeHeaderValue(aMime);
        aReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", iAccess);
        bool aOk = Finish(Send(aReq, 120), "設縮圖", iAccess, out _, out oError);
        if (!aOk && oError != null && oError.Contains("403", StringComparison.Ordinal))
            oError += "（頻道沒做手機驗證就不能設自訂縮圖 ⇒ youtube.com/verify）";
        return aOk;
    }

    public static bool TryAddToPlaylist(string iAccess, string iPlaylistId, string iVideoId, out string? oError)
    {
        var j = SCP_JsonData.NewObject();
        var sn = SCP_JsonData.NewObject();
        var rid = SCP_JsonData.NewObject();
        rid.Set("kind", "youtube#video");
        rid.Set("videoId", iVideoId);
        sn.Set("playlistId", iPlaylistId);
        sn.Set("resourceId", rid);
        j.Set("snippet", sn);
        return TrySendJson(iAccess, HttpMethod.Post, "/playlistItems?part=snippet", j.ToJson(false), out _, out oError);
    }

    // ── HTTP 共用 ────────────────────────────────────────────────

    static YouTubeHttp PostForm(string iUrl, Dictionary<string, string> iFields)
        => Send(new HttpRequestMessage(HttpMethod.Post, iUrl) { Content = new FormUrlEncodedContent(iFields) }, ApiTimeoutSec);

    static YouTubeHttp Send(HttpRequestMessage iReq, int iTimeoutSec)
    {
        var r = new YouTubeHttp();
        using var aCts = new CancellationTokenSource(TimeSpan.FromSeconds(iTimeoutSec));
        try
        {
            using HttpResponseMessage aResp = s_Http.Send(iReq, aCts.Token);
            r.Status = (int)aResp.StatusCode;
            r.Location = aResp.Headers.Location?.ToString() ?? "";
            r.Body = aResp.Content.ReadAsStringAsync(aCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { r.Error = $"逾時（{iTimeoutSec} 秒）"; }
        catch (HttpRequestException e) { r.Error = "連線失敗：" + e.Message; }
        catch (IOException e) { r.Error = "讀寫失敗：" + e.Message; }
        finally { iReq.Dispose(); }
        return r;
    }

    static bool Finish(YouTubeHttp iHttp, string iWhat, string iAccess, out SCP_JsonData oJson, out string? oError)
    {
        oJson = SCP_JsonData.NewNull();
        oError = null;
        if (!iHttp.Ok) { oError = $"{iWhat} 失敗：" + Scrub(ApiError(iHttp), iAccess); return false; }
        if (iHttp.Body.Trim().Length == 0) return true;
        try { oJson = SCP_JsonParser.Parse(iHttp.Body); return true; }
        catch (Exception e) { oError = $"{iWhat}：回應不是 JSON（{e.GetType().Name}）"; return false; }
    }

    /// <summary>Data API 的錯誤：`{"error":{"code":403,"message":"…","errors":[{"reason":"quotaExceeded"}]}}` ⇒ 一行字。</summary>
    static string ApiError(YouTubeHttp h)
    {
        if (h.Status == 0) return h.Error;
        try
        {
            SCP_JsonData e = SCP_JsonParser.Parse(h.Body)["error"];
            if (e.IsObject)
            {
                string aReason = e["errors"].IsArray && e["errors"].Count > 0 ? e["errors"][0].GetString("reason", "") : "";
                return $"HTTP {h.Status}" + (aReason.Length > 0 ? $" {aReason}" : "") + "：" + e.GetString("message", "");
            }
        }
        catch (Exception) { }
        return $"HTTP {h.Status}：" + Clip(h.Body, 300);
    }

    /// <summary>token 端點的錯誤：`{"error":"invalid_grant","error_description":"…"}`。</summary>
    static string OAuthError(YouTubeHttp h)
    {
        if (h.Status == 0) return h.Error;
        try
        {
            SCP_JsonData e = SCP_JsonParser.Parse(h.Body);
            if (e["error"].IsString) return $"HTTP {h.Status} {e.GetString("error", "")}：{e.GetString("error_description", "")}";
        }
        catch (Exception) { }
        return $"HTTP {h.Status}：" + Clip(h.Body, 300);
    }

    // ── 小工具 ──────────────────────────────────────────────────

    static readonly Dictionary<string, string> s_VideoMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo", [".wmv"] = "video/x-ms-wmv", [".flv"] = "video/x-flv",
        [".mpg"] = "video/mpeg", [".mpeg"] = "video/mpeg", [".3gp"] = "video/3gpp",
    };

    public static bool IsVideoFile(string iPath) => s_VideoMime.ContainsKey(Path.GetExtension(iPath));

    static string VideoMime(string iPath) => s_VideoMime.TryGetValue(Path.GetExtension(iPath), out string? m) ? m : "application/octet-stream";

    static bool TryWriteSecret(string iDir, string iEncPath, string iPlainPath, string iPlain, string iPassphrase, string iHint, string iLabel, out string? oError)
    {
        oError = null;
        if (string.IsNullOrEmpty(iPassphrase)) { oError = "沒有密碼"; return false; }
        byte[] aPlain = new UTF8Encoding(false).GetBytes(iPlain);
        byte[] aEnc;
        try
        {
            aEnc = SCP_SecretCrypto.Encrypt(aPlain, iPassphrase, iHint ?? "", iLabel);
            if (!SCP_SecretCrypto.Decrypt(aEnc, iPassphrase).AsSpan().SequenceEqual(aPlain)) { oError = "加密後回解對不上 —— 沒有寫任何東西"; return false; }
        }
        catch (Exception e) { oError = $"加密失敗：{e.GetType().Name} —— 沒有寫任何東西"; return false; }
        try
        {
            Directory.CreateDirectory(iDir);
            WriteReplace(iEncPath, aEnc);
            WriteReplace(iPlainPath, aPlain);
        }
        catch (Exception e) { oError = $"寫不進去：{e.Message}"; return false; }
        if (ReadText(iPlainPath) != iPlain.Trim()) { oError = "寫完回讀明文對不上"; return false; }
        return true;
    }

    public static void WriteReplace(string iPath, byte[] iBytes)
    {
        string aTmp = iPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(aTmp, iBytes);
        File.Move(aTmp, iPath, overwrite: true);
    }

    static string ReadText(string iPath)
    {
        try { return File.ReadAllText(iPath, Encoding.UTF8).Trim().TrimStart('﻿'); }
        catch (Exception) { return ""; }
    }

    static string Base64Url(byte[] iBytes) => Convert.ToBase64String(iBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string StripQuery(string iPathAndQuery) { int q = iPathAndQuery.IndexOf('?'); return q < 0 ? iPathAndQuery : iPathAndQuery.Substring(0, q); }

    static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /// <summary>把密鑰從任何要印出去的字串裡拿掉。</summary>
    public static string Scrub(string iText, params string[] iSecrets)
    {
        string s = iText ?? "";
        foreach (string x in iSecrets)
            if (!string.IsNullOrEmpty(x) && x.Length >= 8) s = s.Replace(x, "<已遮蔽>", StringComparison.Ordinal);
        return s;
    }
}
