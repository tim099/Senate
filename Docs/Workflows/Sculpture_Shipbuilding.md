---
title: 造船入口 —— 照步驟表走，到那一步才讀那一份；舊的「造船規格 §N」編號對照
description: 造船（風帆戰艦、槳帆船、蒸汽艦、鐵甲艦這一類雕刻作品）的入口：第一步開單、把步驟表（Ship_Steps）貼成驗收標準，每一步只讀那一行寫的 Ship_* 文件，做完打勾看下一步；以前整份寫在這裡的規格（座標、拆法、砲、帆桁、繩、帆、彈藥庫、給遊戲的 meta、換船檢查表…）2026-10-26 拆成 Ship_* 十六份，§ 編號照舊，本檔留對照表
last_updated: 2026-10-26（拆檔：§0–§12 照行號逐字搬進 Ship_Setup／Ship_Guns／Ship_Turrets／Ship_Masts／Ship_Rigging／Ship_Magazine／Ship_Render／Ship_Decks／Ship_Materials／Ship_Steam／Ship_Fittings／Ship_Helm／Ship_Oars／Ship_Portlids／Ship_Game／Ship_Refit，新寫 Ship_Steps 步驟表；Tim 2026-10-26「參考早安流程 把 /scp-shipbuilding 造船流程的一些細節拆成獨立文件 按造步驟建造到那一步(步驟一會先開task並列好每一步 完成後打勾 查看下一步)才去讀取對應細節」「指令其實都可以透過查詢文件指令 只是要確保知道每一步該看哪幾份文件」；gura TASK-0967）。拆檔之前的修訂紀錄：2026-10-24（§9.3 甲板 meta `walk_y`：walk 只管走道那幾欄；apex-one TASK-0857）；2026-10-22（§8 模型 meta render=wheel：舵輪畫成輪式的低面數網格（四件舵輪已標）、wheel_n、wheel_axis；§9 wheel 那一列；ame TASK-0792）；2026-10-22（§8 render=shell：小艇還原成方塊、算法保留、目前沒有作品標；§9 boat 那一列拿掉 render=shell；ame TASK-0824）；2026-10-22（§8 模型 meta render=shell：小艇的外殼畫成平滑的低面數網格＋體素顏色烤成貼圖（兩種小艇已標）；§9 boat 那一列；ame TASK-0820）；2026-10-22（§8 模型 meta render=pole：槳畫成長桿式的低面數網格（槳帆船的槳已標）；§9 oar 那一列；ame TASK-0815）；2026-10-21（§8 模型 meta render=lathe：遊戲畫成車床式的低面數網格（平滑法線；火藥桶已標）、lathe_n、lathe_axis；§9 powder 那一列；ame TASK-0795）；2026-10-21（§9.6、§12.4 第四段收尾：錨的關聯也當掛點、定遠號與松島號的主機鍋爐改成模型、成品不帶主機鍋爐的網格、鏡像判法多一條；ame TASK-0793）；2026-10-21（§9.5 薄的金屬板、混了木頭的金屬層用混合材質 `iron*0.1`、`iron*0.5+oak*0.5`：比重、硬度、易燃、pen 照比例，外觀照第一個成分；遊戲那側 ships[].matMap 只換遊戲；§10 多一條）；2026-10-18（§2.2 不是大砲的武器做成「換皮大砲」（弩砲 gun=ballista、彈種 bolt、迴旋 ports）、§2.2 武器演出（弦 anim=gun、零件 spin=gun）、§8 彈藥庫的桶可以換樣子（取用點照 store 輪流擺）；erina TASK-0693。§9 kind 表 `ram`、§9.11 撞角（關聯 meta kind=ram：艦長拿撞角當主武器、故意衝撞）、§10 一條；erina TASK-0693。§12.3 繩端改成匯入時算好寫進 .vxk（ropebind）、新順序；ame TASK-0716。造船 skill 入口與 §10：匯進遊戲前跑 ropecheck；ame TASK-0716。§12.1–12.3 已實作（物件 id、指名寫法、繩端照 ends 綁）；ame TASK-0715。§9.10.2 雙層槳帆船（dromon）：槳口開在船殼與舷牆、甲板下的槳手、照舷弧補傾角；§9.10 模型名字不能有 `/`；§10 一條；erina TASK-0693。§12 物件 id、關聯與掛點（規格草案）；ame TASK-0713／0715。§5 三角帆桁 `rig=lateen`＋`fore`、§9.3 甲板 meta `walk`（槳帆船的中央走道）、§9.10 槳的血量在道具設定頁；apex-one。§9.10.1 實際怎麼加槳（皇家號）、§2.2 缺的砲種或裝備做成通用部件；apex-one。§9.10 槳 kind=oar；ame TASK-0691。§9.5 金屬部件一覽：照拿破崙號標成光滑金屬、§10 一條；kaguya。2026-10-17 §6 ratlines 改用橫繩組、繩梯 climb；§9、§9.3 繩梯也是通道；§10；basecamp TASK-0678。2026-10-15 §4.6 整座轉的砲塔怎麼做、§9.6 蒸汽動力怎麼設；meadow。2026-10-14 §2.1、§9.4；ame。原稿 2026-10-12 憲法號 TASK-0478 階段一～三十三；basecamp）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份、照船的種類挑、共用工具程式
  - Sculpture_Blueprint_Build.md | Sculpture_Blueprint_Build | 照圖紙雕大型作品的通用流程（新圖紙、兩本帳、反向對照、verifier）
  - Sculpture.md | Sculpture | 雕刻指令本身（op、收費、觀測、外部模型、繩、帆、匯出）
---

# ⛵ 造船入口

> **一句話：第一步開單、把步驟表貼成驗收標準；之後每一步只讀那一行寫的文件，做完打勾，看下一個沒勾的那一行。**
> 規格本身在 `Ship_*` 那幾份（§ 編號照舊）；本檔只有入口與「§ 編號對照」。參數看 `senate cmd help sculpture`，⛔ 不抄參數表。

---

## 造船skill入口

造船是雕刻的一種。**照步驟走，到那一步才讀那一步的文件**（Tim 2026-10-26）—— 跟早安一樣：⛔ 不背、不先全部讀一遍，照單上下一個沒勾的那一行走。文件一律用 CLI 讀：`senate cmd doc --arg op=show --arg name=<名字>`（大檔照提示分段讀到最後一行）。

1. **先讀步驟表**：`senate cmd doc --arg op=show --arg name=Ship_Steps` —— 怎麼開單、照船的種類（帆／槳／蒸汽、舷側砲／砲塔、木／鐵甲、改裝船）挑步驟、每一步讀哪幾份、有哪些共用工具程式可以拿來改。
2. **步驟 1 開單**：把步驟表貼成驗收標準（新船 `op=create`；單已經有了 ⇒ 接在原本的驗收標準後面 `op=update`）。
3. **每一步**：只讀那一行括號裡的文件、照做、照那份文件的讀數與反向對照驗 ⇒ `task op=check` 帶 `expect_text` 打勾 ⇒ 不帶 `criteria_index` 的 `op=check` 印出下一步。
4. **續造一艘船**：先照雕刻 skill 的續作規則用 CLI 讀那件作品的書卡、心得與 TODO，再看單上下一步。

一定要記得的幾件（每一步都適用；細節在各份文件）：
- 雕刻的委託、收費、子作品、續作規則：`senate cmd skill --arg op=show --arg name=scp-sculpture`。
- ⭐ **儘量用已有的元件**（Tim 2026-10-14）：砲、火藥桶、小艇、舵輪、羅經櫃、絞盤、燈、格柵、錨，別的船做過的就當外部模型引用（可以縮放），⛔ 不要重做；先跑 `senate cmd sculpture --arg op=work --arg sub=components`。船首像、船尾雕飾這種個性裝飾每艘自己做。查不到的砲種或裝備 ⇒ 做成新的通用部件（`Ship_Guns` §2.2）。
- ⭐ 匯進遊戲之前跑 `work sub=ropecheck`；匯進之後看匯入檢查（`shipcheck`）與樓梯自檢 ✗ 0（`Ship_Game`、`Ship_Decks` §9.3）。
- 範例船：憲法號 `basecamp-frigate-constitution-01m`（從龍骨照圖紙造）、收穫號 `calli-cosecha-01m`（一套從線形到舵錨的產生程式，notes 每階段一節）；其他船的產生程式在各自作品的 `design/` 底下（共用工具表在 `Ship_Steps` §5）。
- 現有的 op 不夠用時，照雕刻 skill 最後一條擴充引擎。

---

## § 編號對照（舊的「造船規格 §N」住哪一份）

Blockside 的匯入檢查、程式註解、各艘船的 notes 寫的「造船規格 §9.12」這種編號照舊有效 —— 拆出去的每一份**保留原本的 § 標題與編號**，照下表找那一份、再找那個 §。

| § | 內容 | 住哪 |
|---|---|---|
| §0 | 適用範圍 | `Ship_Setup` |
| §1 | 座標、比例、方向 | `Ship_Setup` |
| §2、§2.1 | 作品怎麼拆；儘量用已有的元件 | `Ship_Setup` |
| §2.2 | 缺的砲種或裝備：做成新的通用部件（換皮大砲、武器演出、砲種表的通用鍵） | `Ship_Guns` |
| §3 | 建議的建造順序 | `Ship_Setup` |
| §4、§4.1–§4.4（含 §4.2.1、§4.2.2） | 砲：一門一件子作品、擺上船、後座、砲身與砲座拆兩件、上下轉、固定點、砲具與砲索 | `Ship_Guns` |
| §4.5–§4.8 | 迴旋砲、整座轉的砲塔、跟著砲轉的裝甲、砲塔層 | `Ship_Turrets` |
| §5 | 桅與帆桁 | `Ship_Masts` |
| §6、§7 | 繩、帆 | `Ship_Rigging` |
| §8（前半＋火藥從艙口吊上去） | 彈藥庫與火藥桶 | `Ship_Magazine` |
| §8（後半） | 模型的低面數畫法 render=lathe／pole／shell／wheel | `Ship_Render` |
| §9（總表） | 給遊戲的 meta 與匯出：kind 總表 | `Ship_Game` |
| §9.1 | 帆、繩、旗的 meta | `Ship_Rigging` |
| §9.2、§9.3 | 空艙房；甲板與通道（梯子、樓梯自檢） | `Ship_Decks` |
| §9.4 | 匯進遊戲：成品壓縮、匯入檢查 | `Ship_Game` |
| §9.5 | 材質（鐵甲、混合材質、平滑反光、玻璃） | `Ship_Materials` |
| §9.6 | 蒸汽動力 | `Ship_Steam` |
| §9.7 | 門與艙蓋 | `Ship_Fittings` |
| §9.8、§9.8.1 | 照參數轉（spin）；舵與舵輪、操舵鏈 | `Ship_Helm` |
| §9.9 | 裝飾 deco=1 | `Ship_Fittings` |
| §9.10、§9.10.1、§9.10.2 | 槳 | `Ship_Oars` |
| §9.11 | 撞角 | `Ship_Fittings` |
| §9.12 | 砲門蓋板 | `Ship_Portlids` |
| §9.13 | 廚房 | `Ship_Fittings` |
| §9.14 | 被打掉怎麼演（blow） | `Ship_Fittings` |
| §9.15 | 揚弾機、自動裝彈機 | `Ship_Turrets` |
| §10 | 換一艘船時的檢查表 | `Ship_Game` |
| §11 | 改裝船 | `Ship_Refit` |
| §12、§12.1–§12.5 | 物件 id、關聯的寫法、掛點（可換零件） | `Ship_Game` |

- ⛔ 改規格時改 `Ship_*` 那一份，本檔只改對照表（新加的 § 加一列）。新的一節放進最接近的那一份、沿用那一章的編號往下加。
