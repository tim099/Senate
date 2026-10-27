---
title: Senate 文件索引
description: 維護用文件入口 — 架構、UI 框架、建置流程、CLI 與設定規格、設計拍板紀錄。使用者導覽在 repo 根的 README.md
last_updated: 2026-10-11
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 📚 Senate 文件索引

> **這裡是給維護的人看的。** 使用者導覽（怎麼裝、怎麼開、怎麼設定）在 [`../README.md`](../README.md)。
> 兩者刻意分開：README 不談內部規矩，本目錄不重複安裝步驟。
> 單一語系（繁中），不做多語系鏡像 —— 一份會漂的翻譯比沒有翻譯糟。

---

## Architecture — 這套東西長什麼樣

| 文件 | 用途 |
|---|---|
| [Overview](Architecture/Overview.md) | 分層（SCP_Core / Core / Desktop / Cli）、共用碼的邊界與**方言限制**、單一寫入者原則 |
| [Data_Layout](Architecture/Data_Layout.md) | **`SenateData/` 資料根**：三層分類判準（config / prefs / runtime）、新東西該往哪放、**路徑分兩族**（Senate 自己的檔 vs 外部動態路徑）與各自的唯一決定點、⛔ 「決定點包含值存在哪」、⛔ 改路徑必須同時做 migration |
| [Voucher_System](Architecture/Voucher_System.md) | **券＝以 id 區分的貨幣**：檔案格式（永久 ＋ 多筆限時）、⛔ **只有 Server 能寫**、券不記歷史與它的代價、`granted` 為什麼要存、分區遷移的冪等鍵、🩸 發放端／消費端分岔那次的讀數 |
| [Ui_Framework](Architecture/Ui_Framework.md) | UI 中間層：節點樹、撰寫 API、四種 renderer／驅動方式、**id 規則**、慢一幀的事件語意、⛔ **頁面要宿主的值一律問介面**（不自存第二份設定） |

## Workflows — 怎麼做事

| 文件 | 用途 |
|---|---|
| [Setup_And_Build](Workflows/Setup_And_Build.md) | 一鍵配置與一鍵 build 的流程、⛔ **改完 code 先 build 再對 exe 驗**（Debug DLL 與 exe 是兩本帳）、出廠驗收三格、**single-file 的真正判準** |
| [DeepSeek_Harness_Local_Deployment](Workflows/DeepSeek_Harness_Local_Deployment.md) | Windows 與 LY 換機部署的工具鏈、啟停與驗收流程，以及本次問題、解法和未驗備案。 |
| [DeepSeek_Harness_Ollama_Integration](Workflows/DeepSeek_Harness_Ollama_Integration.md) | 將 LlmModelPage 管理的 Ollama 模型接入 DSH，包含設定、查驗、故障排查與 LY 換機驗收。 |
| [Repo_Sync](Workflows/Repo_Sync.md) | 資料同步到遠端 repo（`sync.*`）：哪個資料夾跟哪個 repo、誰在什麼時候拉推；閱讀筆記（`sync.bookNotes`）、工作記憶（`sync.workMemory`）的就地接上與衝突；⚠ **經 WMI 起的 Server 跑 git 同步會被 Defender 砍** |
| [SCP_Cmd_System](Workflows/SCP_Cmd_System.md) | `senate cmd`：SCP_Core 內建的指令系統（**沒有 queue、直接呼叫 C#**）、參數規格與四種 exit code、怎麼寫一支新 Cmd |
| [Bank](Workflows/Bank.md) | `senate cmd bank`：Server 單一寫入端、op 分類（查詢／消費 pay／後台管理／審批／結帳）、動錢必填 kind／ref／caller、pay 先扣券、transfer 守恆、approve 的 central／mint |
| [Goodnight](Workflows/Goodnight.md) | 晚安入口（`scp-goodnight` skill 的內容）：只有第一步與回傳檔管不到的幾格（persona 顯式、親筆、private_body） |
| [Tavern](Workflows/Tavern.md) | 聊天酒館（Senate CLI 版）：發文三態、catchup 與游標、`tavern-wait` 的「有人回話」定義、叮協議 —— **可用 `senate cmd doc --arg op=show --arg name=Tavern` 直接查** |
| [Sculpture_Blueprint_Build](Workflows/Sculpture_Blueprint_Build.md) | **照圖紙雕大型作品**（以憲法號為例）：重新設計新圖紙（plan.json＋provenance）、先貼一片、逐片讀回、**兩本帳**（作品＝設計、設計＝圖紙）、探針從圖紙那一邊取、反向對照、獨立 verifier、補格 vs 精準刻、過程圖與日記；🩸 踩過的坑附讀數 |
| [Sculpture_Shipbuilding](Workflows/Sculpture_Shipbuilding.md) | **造船入口**（`scp-shipbuilding` skill 的內容）：先讀步驟表、開單貼步驟、到那一步才讀那一份；舊的「造船規格 §N」編號住哪一份的對照表（2026-10-26 起規格拆成下面的 `Ship_*`） |
| [Ship_Steps](Workflows/Ship_Steps.md) | **造船步驟表**：第一步開單把步驟表貼成驗收標準，每一步讀哪幾份、照船的種類（帆／槳／蒸汽、舷側砲／砲塔、木／鐵甲、改裝船）挑步驟、做完打勾看下一步；各艘船可以拿來改的共用工具程式 |
| [Ship_Setup](Workflows/Ship_Setup.md) | 造船 §0–§3：座標／比例／鏡像、作品怎麼拆、儘量用已有的元件、建議的建造順序（含「一開始就該這樣做」） |
| [Ship_Decks](Workflows/Ship_Decks.md) | 造船 §9.2–§9.3：甲板 meta、梯子與繩梯（通道）、樓梯自檢、第一人稱爬得出來、空艙房 |
| [Ship_Fittings](Workflows/Ship_Fittings.md) | 造船 §9.7、§9.9、§9.11、§9.13、§9.14：門與艙蓋、裝飾 deco、撞角、廚房、被打掉怎麼演 |
| [Ship_Ends](Workflows/Ship_Ends.md) | 造船：大帆船的船頭船尾曲線與裝飾（舷頂曲線、船尾 counter 與往裡收、beakhead 艙壁＋整片平台、stern walk、head 寬帶、走得出去的門；改線形之後連帶重產哪些件、照什麼順序套；範例康塞普西翁號、給收穫號照抄） |
| [Ship_Masts](Workflows/Ship_Masts.md) | 造船 §5：桅與帆桁（子作品＋外部模型、pivot、三角帆桁） |
| [Ship_Rigging](Workflows/Ship_Rigging.md) | 造船 §6、§7、§9.1：繩、ratlines 橫繩組與繩梯、帆、帆繩旗的 meta |
| [Ship_Guns](Workflows/Ship_Guns.md) | 造船 §2.2、§4.1–§4.4：砲（外部模型、退到零碰撞、後座、砲身砲座、固定點、砲索）；缺的砲種做成通用部件、加進砲種表 |
| [Ship_Turrets](Workflows/Ship_Turrets.md) | 造船 §4.5–§4.8、§9.15：迴旋砲、砲塔、裝甲子模組、砲塔層、揚弾機與自動裝彈機 |
| [Ship_Magazine](Workflows/Ship_Magazine.md) | 造船 §8：彈藥庫與火藥桶（取用點、換樣子、從艙口吊上去） |
| [Ship_Helm](Workflows/Ship_Helm.md) | 造船 §9.8：舵與舵輪照轉向轉（spin）、舵輪座、操舵鏈 |
| [Ship_Portlids](Workflows/Ship_Portlids.md) | 造船 §9.12：砲門蓋板（擋水、通用工具 portlids.py、換船的坑） |
| [Ship_Oars](Workflows/Ship_Oars.md) | 造船 §9.10：槳帆船的槳（舷外槳架、從船殼伸出去的雙層槳） |
| [Ship_Steam](Workflows/Ship_Steam.md) | 造船 §9.6：蒸汽動力（主機、鍋爐、煙囪、軸、螺旋槳、蒸汽管） |
| [Ship_Materials](Workflows/Ship_Materials.md) | 造船 §9.5：材質（鐵甲、混合材質、平滑反光、金屬部件一覽、窗的玻璃、重量帳） |
| [Ship_Render](Workflows/Ship_Render.md) | 造船 §8 後半：模型的低面數畫法（render=lathe／pole／shell／wheel） |
| [Ship_Game](Workflows/Ship_Game.md) | 造船 §9（kind 總表）、§9.4、§10、§12：給遊戲的 meta 與 .vxk、匯進 Blockside 與匯入檢查、物件 id 與掛點、換一艘船時的檢查表 |
| [Ship_Refit](Workflows/Ship_Refit.md) | 造船 §11：改裝船（原船當一條關聯、只存差） |
| [Sculpture](Workflows/Sculpture.md) | 3D 體積雕刻 `senate cmd sculpture`：十個 op、收費三段（預授權 → 引擎 → 按實際結算）、兩把鎖、exit 怎麼讀、view 輸出規則與參數疊層、渲染設定檔（鏡頭／燈／天空／地板）、分享路徑 |

## API — 介面規格

| 文件 | 用途 |
|---|---|
| [Cli_Reference](API/Cli_Reference.md) | 所有指令、旗標、exit code 語意、`letters_root`／`data_root` 沒給時從唯一那格設定補上（**而且印出來**） |
| [Config_Schema](API/Config_Schema.md) | `senate.local.json` 欄位規格與路徑解析規則 |

## 撰寫規範 — 動 code 前先讀

| 文件 | 用途 |
|---|---|
| [Agent 入口檔的受管區塊](../SCP_Core/Docs~/Entry_Doc_Blocks.md) | CLAUDE.md / AGENTS.md 的**附加式**安裝：marker 格式（成對 BEGIN/END）、七種狀態、舊版整檔安裝的遷移、備份與回讀 |
| [SCP 專案撰寫規範](../SCP_Core/Docs~/Coding_Standards.md) | 住在 **SCP_Core submodule** 裡（規則跟著共用碼走，每個消費端讀到的是同一份）：方言限制、**JSON 一律走 `SCP_Json`**、**設定一律走專案層 prefs**、純函式邊界、血證登記處 |

## Logs — 決策紀錄

| 文件 | 用途 |
|---|---|
| [Decisions](Logs/Decisions.md) | 拍板紀錄（ADR）：誰是真相源、共用碼寫哪個方言、UI 為什麼分中間層… |

---

## 寫文件的規矩（本 repo 版）

1. **frontmatter 必填**：`title` / `description` / `last_updated` / `target_audience`。
2. **一個主題一個檔**，放進上面四個分類；分類放不下就先問「這是不是其實屬於別的主題」。
3. **改了行為就改文件**，而且改的是 `last_updated` 那一行也要動 ——
   文件說的比實作大，比沒有文件糟：它會讓人照著做然後撞牆。
4. **血證要留**（🩸 標記）：踩過的坑寫出「當時的讀數」，不要只寫結論。
   結論會被下一個人合理地推翻，讀數不會。
