---
title: Bilibili 安裝與登入 —— bili-cli（bili-agent）
description: 在一台電腦上把 B 站的命令列工具 bili 裝好並登入：先量這台、鎖版本安裝（先對雜湊、先讀原始碼）、憑證資料夾指到 Secret/bilibili、掃碼登入由 Tim 做、換一台電腦、升級前要再讀一次差異、不能跑的三個指令。只有還沒裝、還沒登入或要升級時才讀。
last_updated: 2026-11-10
target_audience: [AI_Agent, Tim, Tools_Maintainer]
---

# 🔧 Bilibili 安裝與登入

> **還沒裝、還沒登入、或要升級時才讀這份。** 平常投稿與互動看 [Bilibili](Bilibili.md)。
> 工具是開源的 [kukemc/bili-cli](https://github.com/kukemc/bili-cli)（PyPI 套件名 `bili-agent`，指令 `bili`）—— 非官方，來由與風險看 [Bilibili_Proposal](Bilibili_Proposal.md)。

## 1. 先量這台

```bash
bili --version                                                     # 有沒有裝、哪一版
BILI_CLI_HOME="<資料根>/Secret/bilibili" PYTHONUTF8=1 bili auth status    # 有沒有登入、登入的是誰
```

| 讀數 | 走哪條 |
|---|---|
| `bili: command not found` | §2 安裝 |
| 版本不是下面「現況」那一版 | 先別升級，看 §5 |
| `not_logged_in`（exit 3）或 `auth_expired` | §3 登入 |
| `logged_in: true` 而且帳號名對 | 好了，回 [Bilibili](Bilibili.md) |

現況（2026-11-10 設好的那一套）：

| 項目 | 值 |
|---|---|
| 版本 | `bili-agent 1.3.0`（2026-11-04 發布） |
| wheel 的 SHA-256 | `c93f841b8280018f7922855a3d24282ee5d1de4c4f5ff64aab17bb96d1ec6e50` |
| 主要依賴 | `bilibili-api-python 17.4.2`、`curl-cffi 0.17.0`、`httpx 0.28.1` |
| 裝在哪 | `uv tool` 的獨立環境；指令在 `~/.local/bin/bili` |
| 憑證資料夾 | `<資料根>/Secret/bilibili/`（`credential.json`、`audit.log`、`ratelimit.json`） |
| 登入的帳號 | `tim099999`（UID 490068083）—— Tim 原本在運營的帳號 |

## 2. 安裝

前提：Python ≥3.10、`uv`（`uv --version`）。ffmpeg 選用（沒給封面時用它截一格）。

**⛔ 不直接 `uv tool install bili-agent`。** 先把檔抓下來、對雜湊、讀過，再裝那一個檔：

1. 從 PyPI 的 JSON（`https://pypi.org/pypi/bili-agent/json`）找到要裝那一版 wheel 的網址與 `sha256`。
2. 下載到一個**新的空資料夾**，`sha256sum` 對得上才往下。
3. 解開來讀（§4 的清單）。**跟「現況」同一個雜湊 ⇒ 已經讀過，可以跳過這一步。**
4. 裝那個檔：

```bash
uv tool install "<下載資料夾>/bili_agent-<版本>-py3-none-any.whl"
bili --version
```

🩸 這個工具拿的是帳號的登入狀態（等同帳號密碼），而且才出來三週、三週內改了 18 版 —— 所以裝的一定要是讀過的那個檔。

## 3. 登入（掃碼，Tim 做）

```bash
export BILI_CLI_HOME="<資料根>/Secret/bilibili" PYTHONUTF8=1
bili auth login                    # 產生 QR 圖：<憑證資料夾>/login_qrcode.png，180 秒內有效
bili auth status --wait 170        # 等掃碼；agent 代跑時放背景
bili auth status                   # 再讀一次：logged_in、帳號名
```

- agent 把 QR 圖傳給 Tim，Tim 用 B 站 App 掃、在手機上按確認。帳號密碼、簡訊驗證碼都不經過 agent。
- `bili auth login` 的輸出裡有登入網址（`qr_url`）—— ⛔ 不印進回傳檔、酒館或 commit 訊息。
- 過期了（`login_expired`）就重跑 `bili auth login`。
- **每次呼叫都要帶 `BILI_CLI_HOME`**：沒帶會落到 `%APPDATA%\bili-cli\`，那邊沒登入，只會回「尚未登录」。
- 🩸 **`PYTHONUTF8=1` 要帶**：這台主控台是 cp950，`--help` 印簡體字會直接 `UnicodeEncodeError`。
- 憑證是**明文** `credential.json` ⇒ 只能放在 `Secret/` 底下（資料根的 `.gitignore` 擋著 `Secret/*`，只放行 `*.enc`）。
  裝好後確認一次：`git -C <資料根> check-ignore -v Secret/bilibili/credential.json` 要有輸出。

### 換一台電腦

那一台重新走 §2、§3，自己掃一次碼。⛔ 不搬 `credential.json`（明文，不進 git、不丟雲端或聊天軟體）。

## 4. 讀原始碼要看什麼

| 看什麼 | 1.3.0 的讀數（2026-11-10；51 個檔、Python 9174 行） |
|---|---|
| 帶登入 cookie 的請求送去哪 | 只有 B 站：`passport`／`api`／`api.vc`／`member.bilibili.com`，加上私訊監聽的 `broadcast.chat.bilibili.com` |
| 憑證怎麼存 | `credential.json`，明文 |
| 遙測、自動更新 | 沒有 |
| `eval`／`exec`／`pickle` | 沒有 |
| 執行外部程式 | 只有 ffmpeg，以及 §6 那三個要另外下指令才會跑的 |
| 連到 B 站以外的 | 只有語音辨識（§6） |
| 限流 | 留言 10 秒一次、每小時 30；私訊每小時 20；動態 60 秒一次；投稿 5 分鐘一次、每小時 5。可用 `BILI_CLI_NO_RATE_LIMIT=1` 關掉 —— ⛔ 不設 |
| AI 聲明 | 投稿預設勾，要關得明寫 `--no-ai-declare` |

怎麼掃：對整個套件 grep 網址與主機名、`subprocess`／`eval`／`exec`／`pickle`／`base64`、`sessdata`／`bili_jct`／`cookies`；`config.py`、`commands/auth.py`、`runtime.py`、`guard.py` 逐行讀。
**沒讀的**：依賴 `bilibili-api-python`（社群的老牌函式庫，版本被鎖死）與其他間接依賴。

## 5. 升級

⛔ 不自動升級、不跑 `uv tool upgrade`。要升：照 §2 下載新版 → 對雜湊 → **跟目前這版逐檔比差異**，§4 的清單重看一次 → 再裝 → 更新 §1「現況」那張表。
B 站改版把工具弄壞時（常見症狀：`api_error`、登入後拿不到 cookie）才需要升。

## 6. ⛔ 不跑的三個指令

| 指令 | 為什麼不跑 |
|---|---|
| `bili skills install` | 會把它自己的 skill 複製進這台所有 agent 的資料夾（`~/.agents`、`~/.claude`…）。我們的 skill 是 `scp-bilibili` |
| `bili mcp install` | 會改各家 agent 的 MCP 設定 |
| `bili asr setup`／`bili video transcribe` 的雲端引擎 | 會跑 `pip install`、下載模型；雲端引擎會把影片的聲音送到阿里雲、騰訊雲或自訂端點 |

## 7. 移除

`uv tool uninstall bili-agent`。登出：`bili auth logout`（刪掉 `credential.json`）；要讓那顆登入狀態在 B 站那邊也失效，Tim 在 App 的「登入裝置管理」裡移除。
