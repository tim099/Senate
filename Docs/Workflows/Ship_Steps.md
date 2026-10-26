---
title: 造船步驟表 —— 第一步開單列好每一步，做完打勾、看下一步讀哪幾份文件
description: 造船照步驟走：開單時把步驟表貼成驗收標準，做到哪一步才讀那一步的文件（龍骨線形、甲板梯子、裝飾、桅、索具、砲、彈藥庫、舵、砲門蓋板、動力、給遊戲的 meta、交船）；照船的種類（帆／槳／蒸汽、舷側砲／砲塔、木／鐵甲、改裝船）挑要讀的；各艘船寫過、可以拿來改的共用工具程式
last_updated: 2026-10-26（新：Tim 2026-10-26「參考早安流程 把造船流程的一些細節拆成獨立文件 按造步驟建造到那一步(步驟一會先開task並列好每一步 完成後打勾 查看下一步)才去讀取對應細節(例如 龍骨 桅杆 裝飾 樓梯 然後可以根據動力分類(蒸汽動力只有做該類型船要讀)以及有哪些可以共用的工具程式等)」「指令其實都可以透過查詢文件指令 只是要確保知道每一步該看哪幾份文件」；同日步驟 7、8 補帆的兩格 —— 蓬特號收帆只被拉直、帆打不到）
target_audience: [AI_Agent]
related:
  - Sculpture_Shipbuilding.md | Sculpture_Shipbuilding | 造船入口與舊 § 編號對照
  - Sculpture_Blueprint_Build.md | Sculpture_Blueprint_Build | 照圖紙雕大型作品的通用流程（新圖紙、兩本帳、反向對照）
  - Task_Management.md | Task_Management | 開單、驗收標準、打勾
---

# 🧭 造船步驟表

> **一句話：第一步開單、把下面的步驟表貼成驗收標準；之後每一步只讀那一行寫的文件，做完打勾，看下一個沒勾的那一行。**
> 文件一律用 CLI 讀：`senate cmd doc --arg op=show --arg name=<名字>`；指令的參數 `senate cmd help sculpture`。⛔ 本檔不抄文件內容 —— 只寫「哪一步讀哪幾份」。
> 大檔（`Ship_Game`、`Sculpture`）照 CLI 的提示分段讀到最後一行。

## 1. 怎麼走（跟早安一樣：照 next 走，不用背）

1. **開單**（步驟 1）：照第 3 節的步驟表，刪掉不適用的那幾行（第 2 節挑），貼成驗收標準。
   - 新船 ⇒ `senate cmd task --arg op=create … --arg-file criteria=<檔>`。
   - 單已經有了（例：別人開好的船型單）⇒ 先讀單檔 `## 驗收標準` 整段，原封不動接上步驟表再 `op=update --arg-file criteria=`（整段覆寫，規則見 `Task_Management` §5）。
   - 一艘船分好幾張子單也行：每張子單的驗收標準放它那幾步。
2. **每一步**：只讀那一行括號裡的文件（`文件名 §節`），照做、照那份文件的讀數與反向對照驗；⛔ 不要先把全部文件讀一遍 —— 讀了用不到的會擠掉要用的。
3. **做完打勾**：`senate cmd task --arg op=check --arg persona=<我> --arg index=<N> --arg criteria_index=<k> --arg expect_text="步驟 <n>"`（`criteria_index` 是**未勾清單**的序號 ⇒ 一律帶 `expect_text`）。
4. **看下一步**：`senate cmd task --arg op=check --arg persona=<我> --arg index=<N>`（不帶 `criteria_index` ＝ 只印未勾清單、零寫入）⇒ 第一個「步驟 N」開頭的那一行就是下一步、括號裡是要讀的文件（單上原本就有的總驗收格會排在步驟前面、到最後才勾 —— 藍鯊號 TASK-0967 第一次照步驟走時量到的）。
5. 續造（隔天、compact 醒來）：先讀作品的書卡、notes、todo（`scp-sculpture` 的續作規則），再照第 4 點看下一步。

## 2. 照船的種類挑步驟

| 船有這個 | 加讀／加做 | 沒有就 |
|---|---|---|
| 帆（每艘都有，除了純槳船、純蒸汽艦） | 步驟 7、8 | 刪 7、8（桅、索具） |
| 舷側砲 | 步驟 9 `Ship_Guns` | 刪 9、12 |
| 迴旋砲、砲塔、砲塔層、揚弾機 | 步驟 9 再讀 `Ship_Turrets` | 只讀 `Ship_Guns` |
| 弩砲、投石機、希臘火（換皮大砲） | 步驟 9 `Ship_Guns` §2.2（不是大砲的武器、武器演出） | — |
| 封閉砲甲板（舷側砲門開在船殼上） | 步驟 12 `Ship_Portlids`（**要做**：沒蓋板的砲口開局就是進水口） | 刪 12（露天甲板的砲、從舷牆頂上過的砲不做蓋板） |
| 槳 | 步驟 13 `Ship_Oars` | — |
| 蒸汽（主機、鍋爐、煙囪、軸、螺旋槳） | 步驟 13 `Ship_Steam`；金屬部件再讀 `Ship_Materials` | — |
| 鐵甲、銅皮、金屬部件、混合材質 | 步驟 13 `Ship_Materials` | 全船木頭 ⇒ 不用標（遊戲預設橡木） |
| 撞角（要故意衝撞的） | 步驟 13 `Ship_Fittings` §9.11 | 壓斷槳的 spur／éperon 不算撞角 |
| 改裝船（同級艦、改型：原船當一條關聯） | 先讀 `Ship_Refit`，步驟 3–4 換成「遮罩與拿掉的部件」，其他只留改到的那幾步 | — |

⇒ 每一行步驟後面寫的是**所有船都要讀的**；只有某一類才讀的寫在這張表，開單時把它併進那一行的括號裡。

## 3. 步驟表（開單時照抄、刪掉不適用的）

```text
- [ ] 步驟 1 開單、列步驟：照船的種類挑好步驟、貼成驗收標準（讀：Ship_Steps）
- [ ] 步驟 2 開工前：座標與比例、作品怎麼拆、查 sub=components 能用的現有元件、建船殼（任務作品）與組裝件、都記 meters_per_voxel（讀：Ship_Setup §0–§2.1；委託與收費看 scp-sculpture）
- [ ] 步驟 3 找資料與新圖紙：主尺寸、線形（龍骨、艏艉柱、剖面）、甲板高度、砲門、桅帆表，每個數字標出處或設計決定；產生程式重跑逐位元組相同（讀：Sculpture_Blueprint_Build §1–§4、Ship_Setup §3）
- [ ] 步驟 4 船殼落格、砲門、塗裝、船尾窗：結果帳缺 0／多 0／色差 0、1 元件、空腔 0、鏡像差 0、反向對照（讀：Sculpture_Blueprint_Build §5–§8）
- [ ] 步驟 5 甲板、梯子、艙房：每層甲板子作品＋meta（kind=deck、level、floor、thick）、每層有梯子通到、梯子四個點、要的話空艙房（讀：Ship_Decks）
- [ ] 步驟 6 裝飾：船首（喙、船首像）、船尾（窗、陽台、船名）、窗的玻璃；能隱藏的裝飾做成模型標 deco=1（讀：Ship_Fittings §9.9、Ship_Materials「窗的玻璃」）
- [ ] 步驟 7 桅與帆桁：每根桅一件子作品、每根帆桁一件子作品＋外部模型（pivot、meta kind=yard）、`yard` 的值照遊戲怎麼轉挑（方帆的下帆桁 ⛔ 不標 boom）、三角帆桁 rig=lateen（讀：Ship_Masts）
- [ ] 步驟 8 索具、帆、旗：channels、靜索、ratlines 用橫繩組（下桅標繩梯 climb=1）、帆角綁帆桁模型、帆／旗／帆自己的繩標 meta；帆裝三段各張哪幾面排成表（收帆、戰鬥帆都至少一面；單帆 set 1）；ropes_broken 0、sails_broken 0（讀：Ship_Rigging）
- [ ] 步驟 9 砲：外部模型、退到零碰撞、砲口在船殼外、meta gun= 在遊戲砲種表、固定點齊；要的話砲具與砲索；缺的砲種做成通用部件（讀：Ship_Guns）
- [ ] 步驟 10 彈藥庫與甲板物件：彈藥庫裡一定放火藥桶、小艇、絞盤、羅經櫃、門與艙蓋、廚房或裝飾（讀：Ship_Magazine、Ship_Fittings §9.7／§9.13／§9.14）
- [ ] 步驟 11 舵、舵輪、錨：舵是模型（kind=rudder＋stock-top＋spin）、舵輪＋座（kind=wheel＋spin）、錨用共用元件、錨鏈孔固定點（讀：Ship_Helm；錨看 Ship_Game §9 表的 anchor 那一列）
- [ ] 步驟 12 砲門蓋板：封閉砲甲板每個砲門一片（kind=portlid、lid_of、P 舷 rotate 180）；⚠ lid_of 是砲的物件 id ⇒ 先做步驟 14 的匯入＋admin.py ids，回來做蓋板，再 reimport（讀：Ship_Portlids）
- [ ] 步驟 13 動力與材質：（帆船沒有這一步就刪掉；槳讀 Ship_Oars、蒸汽讀 Ship_Steam、鐵甲與金屬部件讀 Ship_Materials、撞角讀 Ship_Fittings §9.11）
- [ ] 步驟 14 給遊戲的 meta 與匯入：kind=ship waterline_z、ropecheck、匯出 vxk、匯進 Blockside（暫存副本或自己的 worktree）、admin.py ids、shipcheck ✗ 0、樓梯自檢 ✗ 0；新做的桶、槳、舵輪這類元件要不要標 render（讀：Ship_Game §9／§9.4／§12、Ship_Render）
- [ ] 步驟 15 交船：換一艘船時的檢查表逐條看過、notes 寫下給遊戲那側填 geo 的高度、跟遊戲那側對 checksum（讀：Ship_Game §10）
```

- 步驟可以再切細（例：步驟 4 切成「線形落格」「砲門與塗裝」兩行），⛔ 不要合併成一行「造好船」—— 合併了就看不出下一步該讀哪份。
- 括號裡的文件就是那一步**全部**要讀的；做到一半發現要查別的（例：步驟 9 擺砲撞到甲板高度）⇒ 去讀那一份，讀完照舊回到這一步。
- 某一步做不了（等別人、等拍板）⇒ 單上留言「判定、憑據、球在誰」，⛔ 不要跳過去勾後面的。

## 4. 文件地圖（每份講什麼）

| 文件 | 內容（原本的 § 編號照舊） | 哪一步讀 |
|---|---|---|
| `Ship_Setup` | §0 適用範圍、§1 座標比例方向、§2 作品怎麼拆、§2.1 用已有的元件、§3 建造順序 | 2、3 |
| `Sculpture_Blueprint_Build` | 照圖紙的通用流程：找圖校準、新圖紙（plan.json＋出處）、遮罩落子、兩本帳、反向對照、verifier、過程圖與日記 | 3、4 |
| `Ship_Decks` | §9.2 空艙房、§9.3 甲板與通道（梯子、繩梯、樓梯自檢、第一人稱爬得出來） | 5 |
| `Ship_Fittings` | §9.7 門與艙蓋、§9.9 裝飾、§9.11 撞角、§9.13 廚房、§9.14 被打掉怎麼演 | 6、10、13 |
| `Ship_Masts` | §5 桅與帆桁 | 7 |
| `Ship_Rigging` | §6 繩、§7 帆、§9.1 帆繩旗的 meta | 8 |
| `Ship_Guns` | §2.2 缺的砲種做成通用部件、§4.1–§4.4 砲（擺法、後座、砲身砲座、固定點、砲索） | 9 |
| `Ship_Turrets` | §4.5–§4.8 迴旋砲、砲塔、裝甲子模組、砲塔層；§9.15 揚弾機、自動裝彈機 | 9（只有那類船） |
| `Ship_Magazine` | §8 彈藥庫與火藥桶、火藥從艙口吊上去 | 10 |
| `Ship_Helm` | §9.8 照參數轉（spin）、§9.8.1 舵與舵輪、操舵鏈 | 11 |
| `Ship_Portlids` | §9.12 砲門蓋板 | 12 |
| `Ship_Oars` | §9.10 槳（皇家號、持火號的做法） | 13（槳帆船） |
| `Ship_Steam` | §9.6 蒸汽動力 | 13（蒸汽艦） |
| `Ship_Materials` | §9.5 材質、平滑與反光、混合材質、窗的玻璃 | 6、13 |
| `Ship_Render` | §8 後半：模型的低面數畫法（render=lathe／pole／shell／wheel） | 14 |
| `Ship_Game` | §9 kind 總表與匯出、§9.4 匯進遊戲、§12 物件 id 與掛點、§10 換一艘船時的檢查表 | 14、15；查某個 kind 的鍵隨時讀 §9 |
| `Ship_Refit` | §11 改裝船 | 改裝船才讀 |
| `Sculpture` | 雕刻指令本身（op、觀測、關聯、外部模型、繩、帆、匯出） | 用到哪個 op 查哪一節 |

## 5. 共用工具程式（拿來改，⛔ 不要從零寫）

產生程式都在作品的 `design/` 底下（`Valhalla/Sculpture/works/<作品>/design/`）。複製過去改時檔頭寫「出處：<作品>/design/<檔>」。

| 用在哪一步 | 程式 | 做什麼 |
|---|---|---|
| 3 | `calli-cosecha-01m/design/lines.py`、`layout.py`、`draft_lines.py`、`draft_layout.py`（出自 `gura-tridente-01m/design/hull_lines.py`） | plan.json 的參數 ⇒ 每個 (x, z) 的半寬、船殼牆清單、甲板／砲門／桅帆配置、圖紙；**一整套從線形到砲、舵錨的產生程式**（收穫號 notes 每階段一節） |
| 3–14 | `calli-cosecha-01m/design/tools/wio.py` | 讀作品（CLI `export format=list`，可帶 mark、links）、讀清單、格子的鍵 |
| 4 | `calli-cosecha-01m/design/tools/stamp_lists.py` | 一串 `.vox.txt` 清單照順序 stampvox，每刀讀回 placed，對不上就停（`--from=N` 續跑） |
| 4–13 | `calli-cosecha-01m/design/tools/delta_apply.py`、`hull_expected.py` | 改了設計 ⇒ 作品對清單：多的帶顏色守門刻、少的貼、顏色不同的換；照清單組出「應有的船殼」 |
| 4 | `calli-cosecha-01m/design/tools/check_hull.py`、`check_stage3.py` | 結果帳（作品＝設計）、砲門開口裡有沒有實心（附正向／反向對照） |
| 5 | `calli-cosecha-01m/design/draft_decks.py`＋`tools/build_decks.py`、`check_decks.py` | 甲板板、橫樑、艙口、break、梯子、梯子點；建子作品、關聯、固定點 |
| 5 | `basecamp-ember-01m/design/tools/ladder_points.py` | 從扶手繩固定點算梯子 foot／lo／hi／head（不帶 `--apply` 只算） |
| 5、13 | `basecamp-ember-01m/design/tools/walkback.py`、`kotoko-napoleon-01m/design/draft_engine_access.py` | 第一人稱「走得到 − 走得回」的有向圖（掉得進去要爬得出來） |
| 5 | Blockside `src/decks.py`、`src/rooms.py` | 甲板 meta（floor、thick）⇒ tsv；空艙房候選與檢查 |
| 6 | `calli-cosecha-01m/design/draft_deco.py`、`basecamp-frigate-constitution-01m/design/draft_stern_glass.py` | 船首喙、船首像、船尾陽台、galleries、船名板；船尾窗玻璃 |
| 7 | `calli-cosecha-01m/design/draft_masts.py`＋`tools/build_masts.py` | 桅、桅樓、艏斜桅、帆桁（方帆桁縮放憲法號的元件、新做三角帆桁） |
| 8 | `calli-cosecha-01m/design/draft_rigging.py`＋`tools/rigging_apply.py` | channels、固定點、靜索、ratlines、帆、旗、meta |
| 9 | `calli-cosecha-01m/design/gunsim.py`、`draft_guns.py`、`basecamp-ember-01m/design/place_guns.py` | 砲退到零碰撞（格心＋八角）、追擊砲照角度搜、砲口在船殼外 |
| 9 | `calli-cosecha-01m/design/draft_gunfittings.py` | 船牆與甲板的環、breeching／tackle／train 砲索 |
| 9 | `calli-cosecha-01m/design/tools/crew_sim.py` | 照遊戲的站位規則模擬砲班、桅頂水手站不站得滿（shipcheck 之前先量） |
| 10 | `calli-cosecha-01m/design/draft_gear.py`、`basecamp-nightlamp-01m/design/draft_magazine.py` | 彈藥庫、火藥桶兩層架、小艇架、甲板上的模型（自動量 z） |
| 11 | `calli-cosecha-01m/design/draft_rudder_anchor.py` | 舵（沿艉柱、削前緣）、錨架、錨（照船殼切線搜位置） |
| 12 | `basecamp-frigate-constitution-01m/design/tools/portlids.py`（斜牆：`ame-virginia-01m/design/tools/portlids.py`） | 蓋板 draft → check → apply；船、哪幾層、用哪件蓋板寫在程式開頭的表 |
| 13 | `apex-one-reale-01m/design/draft_oars.py`、`erina-dromon-01m/design/draft_oars.py` | 槳的元件、擺法、讀數（舷外槳架／從船殼伸出去） |
| 13 | `kotoko-napoleon-01m/design/tools/split_gears.py`、`meadow-monitor-01m/design/gen_armor.py`、`trailhead-dingyuan-01m/design/mass.py` | 會轉的齒輪拆模型；砲塔裝甲；照材質表算重量帳 |
| 14 | Blockside `src/admin.py`（`import`／`reimport`／`ids`／`metasync`／`shipcheck`／`ladcheck`／`cache`）；雕刻 `work sub=ropecheck`、`sub=components` | 匯進遊戲、發 id、匯入檢查、樓梯自檢、繩端綁定檢查、查現有元件 |

- ⚠ 抄來的程式裡寫死的作品 id、persona、船名要全部換掉（收穫號的抄到藍鯊號時一次換 37 個檔，換完 grep 一次舊 id 是 0）。
- ⚠ 抄來的程式讀組裝件時要扣掉「本階段自己的件」，不然重產時把上一輪落過的當成船（收穫號階段五的坑，見它的 notes）。
