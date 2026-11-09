---
title: 造船：彈藥庫與火藥桶
description: 彈藥庫子作品、火藥桶一定要放（遊戲的取用點）、桶可以換樣子（store）、火藥從艙口吊上去（hoist）
last_updated: 2026-10-26（從 Sculpture_Shipbuilding 拆出來，內文照搬、§ 編號照舊；Tim 2026-10-26「把造船流程的一些細節拆成獨立文件 按步驟建造到那一步才去讀取對應細節」）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份
  - Sculpture_Shipbuilding.md | Sculpture_Shipbuilding | 造船入口與舊 § 編號對照
  - Ship_Setup.md | Ship_Setup | 開工前 —— 座標與比例、作品怎麼拆、用已有的元件、建造順序
  - Ship_Guns.md | Ship_Guns | 砲 —— 一門一件子作品、擺上船退到零碰撞、固定點、砲具與砲索、缺的砲種做成通用部件
  - Ship_Turrets.md | Ship_Turrets | 迴旋砲、砲塔、裝甲子模組、砲塔層、揚弾機
  - Ship_Masts.md | Ship_Masts | 桅與帆桁
  - Ship_Rigging.md | Ship_Rigging | 繩、帆、旗與它們的 meta
  - Ship_Render.md | Ship_Render | 模型在遊戲裡的低面數畫法（render=lathe／pole／shell／wheel）
  - Ship_Decks.md | Ship_Decks | 甲板、梯子、空艙房（給遊戲的 meta）
  - Ship_Materials.md | Ship_Materials | 材質 —— 鐵甲、銅皮、混合材質、平滑與反光、窗的玻璃
  - Ship_Steam.md | Ship_Steam | 蒸汽動力 —— 主機、鍋爐、煙囪、軸、螺旋槳、蒸汽管
  - Ship_Fittings.md | Ship_Fittings | 門與艙蓋、裝飾、撞角、廚房、被打掉怎麼演
  - Ship_Helm.md | Ship_Helm | 舵與舵輪（照轉向轉）、操舵鏈
  - Ship_Oars.md | Ship_Oars | 槳（槳帆船）
  - Ship_Portlids.md | Ship_Portlids | 砲門蓋板
  - Ship_Game.md | Ship_Game | 給遊戲的 meta 總表、匯進 Blockside、物件 id 與掛點、交船檢查表
  - Ship_Refit.md | Ship_Refit | 改裝船（原船當一條關聯、只存差）
---

# ⛵ 造船：彈藥庫與火藥桶

> **什麼時候讀**：步驟 10（彈藥庫）。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

## 8. 彈藥庫與火藥桶（階段三十二）

- 位置照圖（憲法號 1849 圖：後 x 200–271、前 x 521–574），在水線下 —— ⚠ ame 量到對戰裡砲彈碰海面就停，打不到（本來就擺在打不到的地方）。
- 子作品（銅皮 168、松木地板 177、橡木壓條 136、門洞 5 格寬）；固定點 `blast`（房間空氣重心最近的一格）、`door`（門洞地板中線）。
- 火藥桶一件子作品（0.02 m）當模型放進去；上層木架的立柱要放在架板靠走道的兩角（桶是模型、不是格子 ⇒ 架板以格子來看會浮空，遊戲的連通判斷會讓它掉下去）。
- ⛔ **桶一定要放**（Tim 2026-10-15，維吉尼亞號）：遊戲照桶擺每層砲甲板的**取用點**（送藥手去拿火藥的地方）；彈藥庫只有關聯 meta、裡面沒有桶 ⇒ 一個取用點都沒有，砲打完砲旁藥箱那兩發就永遠停（維吉尼亞號 180 秒只開 12 發，看起來像淹水後沒火藥）。
  直接引用憲法號的 `basecamp-frigate-constitution-01m-powder-barrel`（scale 0.2、pivot 11,11,0、meta `kind=powder, powder_lb=100`），掛在**彈藥庫子作品**裡（遊戲照「彈藥庫關聯名/」認是哪一間的）。Blockside 的匯入檢查逐門砲查拿不拿得到火藥（沒有取用點、沒有送藥手、砲尾後面放不了藥箱 ⇒ ✗；取用點跟砲在走路格上不連通 ⇒ ⚠）。
- **桶可以換樣子**（Tim 2026-10-18：「彈藥箱可以有不同外觀(但實際上都能補彈)」）：彈藥庫裡模型 meta `kind=powder` 的都算火藥桶，樣子不限；遊戲的取用點（三個桶）輪流擺船上不同樣子的桶（最多三種）。
  ⛔ 樣子照模型 meta `store`（補給的種類）分，不照網格（同一種桶擺法不同、網格就不同）⇒ 要換樣子的桶標 `store`，沒標的全部算同一種。
  持火號：希臘火陶罐 ×4（`store=greekfire`）、弩箭箱 ×2（`store=bolt`）（`erina-dromon-01m-store-amphora`／`-store-boltbox`，0.05 m、scale 0.5、pivot ＝ 底面正中；產生程式 `design/draft_stores.py`）⇒ 取用點擺一罐一箱一罐。

- **火藥從艙口吊上去**（彈藥庫關聯 meta `hoist=<梯子的路徑>`，例 `梯子/turret`；TASK-0632，Tim 2026-10-16：「我看火藥直接裸露在頂層甲板上 原船設計是如何搬運火藥的」）：
  那座梯子的梯頭落在哪一層砲甲板，那一層的取用點就改成這座梯子 —— 桶擺在**梯腳那一層**（室內）旁一塊 9×9 的空地、送藥手在**梯頭旁**（艙口邊）取，⛔ 不再照彈藥庫在露天甲板找空地。
  梯子路徑 ＝ 梯子關聯的路徑＋`/`＋梯名（固定點 `<梯名>.foot／.head` 的那個名字）。梯腳找不到空地或梯頭找不到站位 ⇒ 照舊找（匯入檢查看得到取用點在哪）。梯子隨時能走（Tim：「讓樓梯隨時都能通行」）。
  莫尼特號：`彈藥庫` `hoist=梯子/turret` ⇒ 取用點 1 個（砲塔裡艙口旁，桶在住艙甲板梯腳）；180 秒對維吉尼亞號照樣持續開火（原船：火藥經甲板與砲塔地板的艙口吊進砲塔，Greene〈In the Monitor Turret〉）。
  - **好幾座梯子**（逗號分開，例 `hoist=ladders-1#forecastle,ladders-1#castle`）：每一層只用梯頭落在那一層甲板（甲板 meta 的 floor）上的那一座。高起的小平台（艏樓、木塔、艉樓）沒有自己的彈藥庫 ⇒ 每座平台的梯子都寫進去。
    ⛔ 不寫 ⇒ 那幾層照彈藥庫往船頭船尾找空地：高度範圍重疊的兩層會找到同一格（持火號：艏樓、木塔的取用點疊在木塔上，三個桶把木塔內側的走道堵死 ⇒ 匯入檢查 ⚠ 走路格不連通、艏樓弩砲的送藥手從木塔直線飛過去）。
- 封閉空腔：產生程式最後局部掃一次，≤ 50 格的封閉空腔填實、新格每一塊都要連到原本的實心。
