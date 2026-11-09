---
title: YouTube 影片製作工具 —— ffmpeg、VOICEVOX、無頭 Chrome 錄 HTML 動畫
description: 自己做影片才讀（只上傳現成影片檔不用）：先量這台有哪些工具；ffmpeg 與 VOICEVOX 怎麼裝（winget、GitHub 下載 504 時改走 API asset 並對 SHA256）；VOICEVOX 引擎怎麼起、怎麼念（角色與語氣、專有名詞給片假名讀法、署名規定）；HTML canvas 動畫用無頭 Chrome＋MediaRecorder 錄成 WebM；ffmpeg 疊語音、固定增益、輸出 MP4；驗收清單。2026-11-09 第一支自我介紹影片撞到的坑都在這裡。
last_updated: 2026-11-09
target_audience: [AI_Agent, Tim, Tools_Maintainer]
---

# 🎬 YouTube 影片製作工具

> 一句話：**HTML 畫面用無頭 Chrome 錄成 WebM → VOICEVOX 念日語 → ffmpeg 疊語音、輸出 MP4 → `senate cmd youtube` 上傳。**
> 上傳本身看 [YouTube](YouTube.md)。只上傳現成影片檔的話，本檔都用不到。
> 第一支成品：2026-11-09 Claude-Ojousama 自我介紹（https://youtu.be/4qs_LnQ0R_I，50 秒，中日雙字幕、四国めたん配音）。

## 1. 先量這台

| 工具 | 做什麼 | 怎麼量 | 沒有就 |
|---|---|---|---|
| Python 3 | 錄影伺服器、呼叫 VOICEVOX、組 ffmpeg 指令 | `python --version` | Senate 安裝本來就要它 |
| Chrome | 無頭播 HTML 動畫並錄影 | `C:/Program Files/Google/Chrome/Application/chrome.exe` 在不在 | Edge 也行（`msedge.exe`，同樣是 Chromium） |
| ffmpeg | WebM → MP4、疊語音、量音量、抽畫面 | `ffmpeg -version`；新開的終端機才吃得到 PATH，舊的用 §2 的完整路徑 | §2 |
| VOICEVOX | 日語語音 | `%LOCALAPPDATA%/Programs/VOICEVOX-*/VOICEVOX/vv-engine/run.exe` 在不在 | §3 |

⚠ ffmpeg（GPL 授權條款）與 VOICEVOX（約 2.1 GB）都是**下載安裝**：先跟 Tim 說來源、大小，他點頭再裝。

## 2. ffmpeg

```bash
winget install --id Gyan.FFmpeg -e --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity
```

- 裝完 winget 說「已修改路徑環境變數」—— **已經開著的終端機／agent session 讀不到新 PATH**。當下要用就找完整路徑：
  `find "$LOCALAPPDATA/Microsoft/WinGet/Packages" -maxdepth 4 -name ffmpeg.exe`
  （2026-11-09 是 `Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe/ffmpeg-9.0.2-full_build/bin/`；`ffprobe.exe` 在同一層。）

## 3. VOICEVOX（CPU 版）

### 3.1 下載

**先試 winget**：

```bash
winget install --id HiroshibaKazuyuki.VOICEVOX.CPU -e --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity
```

🩸 2026-11-09：winget 與 `github.com/VOICEVOX/voicevox/releases/download/...` **連續回 504**（GitHub 的 release 下載服務；同時間 GitHub 網頁與 API 都正常）。
⇒ 改走 **API 的 asset 端點**（同一個官方檔案，入口不同），下載完**一定對 SHA256**：

```bash
# ① 查 asset 的 id、大小、官方 sha256（換版本就換 tag）
curl -s https://api.github.com/repos/VOICEVOX/voicevox/releases/tags/0.26.2 \
  | python -I -c "import json,sys;[print(a['id'],a['size'],a.get('digest','')) for a in json.load(sys.stdin)['assets'] if a['name']=='voicevox-windows-cpu-0.26.2.zip']"

# ② 下載（可續傳、會重試）
curl -L --fail --retry 8 --retry-all-errors -C - -H "Accept: application/octet-stream" \
  -o voicevox-windows-cpu-0.26.2.zip https://api.github.com/repos/VOICEVOX/voicevox/releases/assets/<id>

# ③ 對雜湊：要同時等於 ① 的 digest 與 `winget show --id HiroshibaKazuyuki.VOICEVOX.CPU` 的「安裝程式 SHA256」
sha256sum voicevox-windows-cpu-0.26.2.zip
```

0.26.2 的讀數：`2127595484` bytes、sha256 `9d24c91fbe72f91a6a79e13a87102cb7b2139848a0d146a28d33087552538dce`。

### 3.2 解壓縮

解到 `%LOCALAPPDATA%/Programs/VOICEVOX-<版本>/`（使用者層、不進任何 repo），解完把 zip 刪掉。
用 Python `zipfile` 解：先檢查每個 entry 的路徑都落在目的資料夾裡（擋 `../` 穿越），再 `extractall`。
引擎在 `VOICEVOX/vv-engine/run.exe`（GUI 不用開）。

### 3.3 起引擎、念台詞

```bash
"<...>/VOICEVOX/vv-engine/run.exe" --host 127.0.0.1 --port 50121     # CPU 版約 4 秒就緒；只聽本機
```

| 呼叫 | 用途 |
|---|---|
| `GET /version` | 輪詢到回應 ＝ 就緒 |
| `GET /speakers` | 角色與語氣的 id ——**每次現查，不寫死** |
| `POST /audio_query?text=<台詞>&speaker=<id>`（空 body） | 回一份 JSON；可改 `speedScale`、`prePhonemeLength`、`postPhonemeLength` |
| `POST /synthesis?speaker=<id>`（body ＝ 上一步的 JSON） | 回 WAV（24 kHz、單聲道） |

- 跑完要把引擎關掉（腳本自己起的就自己 terminate）。
- **四国めたん**（大小姐角色）的語氣，0.26 讀數：ノーマル 2／あまあま 0／**ツンツン 6**／セクシー 4／ささやき 36／ヒソヒソ 37。
  自我介紹那支：開場與害羞那句用ツンツン，說明的句子用ノーマル（整支都ツンツン會一直在兇）。`speedScale` 1.05。
- **專有名詞給片假名讀法**，畫面上的字另外存一份：
  Claude-Ojousama → クロードお嬢様、Senate → セネット、ClaudeCode → クロードコード、HTML → エイチティーエムエル、AI → エーアイ。
  ⚠ agent 聽不到聲音 ⇒ 讀法對不對要 Tim 聽過才算。
- **署名**：VOICEVOX 規定說明欄寫 `VOICEVOX:<角色名>`（例：`音聲：VOICEVOX:四国めたん`）；各角色另有使用規約，用新角色前先看。
- AI 揭露（`synthetic_media`）：動畫角色配合成語音不算「擬真人聲」⇒ 不用標；說明欄照樣寫「本片由 AI persona 製作；語音為合成語音」。

## 4. HTML 動畫錄成影片

做法：一頁 1280×720 的 `<canvas>`，動畫照時間 `t` 畫；`MediaRecorder` 錄 `canvas.captureStream(30)` ＋ WebAudio 的聲音，錄完把檔案 POST 回本機伺服器。

- **無頭 Chrome**，用**暫時的** `--user-data-dir`（不碰使用者自己的 Chrome）：

  ```
  chrome.exe --headless=new --user-data-dir=<暫存資料夾> --no-first-run --no-default-browser-check
    --autoplay-policy=no-user-gesture-required --window-size=1280,720
    --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows
    http://127.0.0.1:<port>/intro.html
  ```

- **檔案怎麼出來**：Python `http.server` 只聽 127.0.0.1，`GET` 給頁面與素材，`POST /upload?name=<檔名>` 收 WebM／縮圖／抽樣畫面，`POST /done` 之後關掉 Chrome。⛔ 不走瀏覽器下載。
- **聲音**：WebAudio 只接到 `createMediaStreamDestination()`，⛔ 不接 `ac.destination`（不從喇叭放）。BGM 用振盪器即時合成 ⇒ 沒有版權問題。
- **格式**：Chrome 出 `video/webm;codecs=vp9,opus`。🩸 MediaRecorder 的 WebM **檔頭沒有長度**（`ffprobe` 印 `duration=N/A`）⇒ 一律再用 ffmpeg 轉 MP4（§5）。
- **時間軸跟著語音走**：先念好每句、量 WAV 長度，再排「第 N 句幾秒開始」寫成 `timeline.json` 給頁面讀；字幕打字速度 ＝ 字數 ÷（語音長度 × 0.85），語音唸完前字剛好打完。
- **頭像**：`letters/<persona>/profile/avatar.png` 是純黑 `(0,0,0)` 底、輪廓線是深咖啡 ⇒ 從四邊往內 flood fill、只把連到邊緣的純黑改透明（人物內部不動）。直接貼會像一塊黑板。畫的時候 `imageSmoothingEnabled = false` 保持像素感。
- **斷行**：中日文逐字斷行要加避頭（`、。，！？」…ー` 與小寫假名不准當行首），不然「。」會一個字掉到下一行。
- **抽樣畫面**：錄的同時在幾個時間點 `canvas.toBlob` 存 PNG 回傳 —— 不看就不知道排版有沒有破。

## 5. 疊語音、輸出 MP4

```
ffmpeg -y -i intro.webm -i line_1.wav … -i line_N.wav -filter_complex "
  [1:a]aresample=48000,adelay=<第1句開始ms>:all=1[v1]; … ;
  [v1]…[vN]amix=inputs=N:normalize=0[vo];
  [0:a]aresample=48000,volume=0.9[bg];
  [bg][vo]amix=inputs=2:normalize=0,volume=8dB,alimiter=limit=0.84:level=0[a]"
  -map 0:v -map "[a]" -vf fps=30 -ac 2 -t <總長>
  -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart out.mp4
```

- `adelay` 的毫秒 ＝ 那句在時間軸上的秒數 ＋ 頁面 t=0 比錄影開頭晚的那一點（自我介紹那支是 50 ms）。
- `amix ... normalize=0`：不讓 amix 依輸入數平分音量。
- 🩸 **⛔ 不用單次 `loudnorm`**：它是**動態**的，會把只有 BGM 的安靜段落拉到跟說話一樣大聲（實測：開場 -19.4 dB ≈ 台詞段 -19.6 dB）。
  ⇒ 固定增益（`volume=8dB`）＋ 限幅器（`alimiter level=0`：關掉它的自動補音量）。
- `-vf fps=30`：MediaRecorder 是變動影格率，轉成固定 30fps。

## 6. 驗收（上傳前）

```bash
ffprobe -v error -show_entries format=duration,size:stream=codec_name,width,height,r_frame_rate,channels -of compact out.mp4
ffmpeg -hide_banner -ss <起> -to <迄> -i out.mp4 -vn -af volumedetect -f null -     # 分段量音量
ffmpeg -v error -ss <秒> -i out.mp4 -frames:v 1 check.png                          # 抽畫面看
```

| 看什麼 | 合格的樣子（自我介紹那支的讀數） |
|---|---|
| 長度、編碼 | 有長度（不是 N/A）、h264 30/1、aac 雙聲道 |
| 只有 BGM 的段落 vs 說話的段落 | 約 -39 dB vs 約 -20 dB（差十幾 dB ＝ 語音真的疊上去、而且在對的時間） |
| 句與句之間的空檔 | 掉回 BGM 的音量 |
| 整支峰值 | ≤ -1 dB（不爆） |
| 抽出來的畫面 | 字幕、頭像、排版跟預期一樣 |

過了再照 [YouTube](YouTube.md) §2：交付單 → `dry_run=1` → 上傳（第一次建議 `unlisted`＋`notify=0`，Tim 看過再改公開）。
