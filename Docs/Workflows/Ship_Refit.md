---
title: 造船：改裝船（原船當一條關聯、只存差）
description: 改裝船：原船整艘當一條關聯、link_hide 拿掉部件、link_carve 挖格子、新加部件
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
  - Ship_Magazine.md | Ship_Magazine | 彈藥庫與火藥桶
  - Ship_Render.md | Ship_Render | 模型在遊戲裡的低面數畫法（render=lathe／pole／shell／wheel）
  - Ship_Decks.md | Ship_Decks | 甲板、梯子、空艙房（給遊戲的 meta）
  - Ship_Materials.md | Ship_Materials | 材質 —— 鐵甲、銅皮、混合材質、平滑與反光、窗的玻璃
  - Ship_Steam.md | Ship_Steam | 蒸汽動力 —— 主機、鍋爐、煙囪、軸、螺旋槳、蒸汽管
  - Ship_Fittings.md | Ship_Fittings | 門與艙蓋、裝飾、撞角、廚房、被打掉怎麼演
  - Ship_Helm.md | Ship_Helm | 舵與舵輪（照轉向轉）、操舵鏈
  - Ship_Oars.md | Ship_Oars | 槳（槳帆船）
  - Ship_Portlids.md | Ship_Portlids | 砲門蓋板
  - Ship_Game.md | Ship_Game | 給遊戲的 meta 總表、匯進 Blockside、物件 id 與掛點、交船檢查表
---

# ⛵ 造船：改裝船（原船當一條關聯、只存差）

> **什麼時候讀**：只有改裝船（同級艦、改型）才讀；讀完照步驟表從步驟 5 起只做改的那幾步。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

## 11. 改裝船（原船當一條關聯、只存差；TASK-0656）

需求（Tim 2026-10-16）：把一艘船當成子作品去做改裝船，減少資料量 —— 像歷史上同一艘船的改型、或略有修改的同級艦；修改可以挖掉原船體上的格子、移除原有的子模組；只記錄差異（匯入時原船與改裝船一起匯入）。

| 步驟 | 做法 | 為什麼 |
|---|---|---|
| 1. 主作品 | 改裝船自己一件（照委託建）；**原船的組裝件整艘當一條關聯**：`name=原船`、`at=0,0,0`、`turn=0` | 原船改了（重造、修 bug），改裝船下一次匯出就跟著改；改裝船的雕刻庫只有它自己加的與覆寫 |
| 2. 拿掉 | 原船裡不要的部件：那條關聯的 `link_hide=路徑,…`（關聯或外部模型；路徑相對原船，例 `駕駛塔`、`甲板-main/艙蓋-前`） | 部件連同它的模型、固定點、meta 一起不在 ⇒ 遊戲那側不會照它排站位、找舵輪 |
| 3. 挖格子 | 開一件**跟原船一樣大**的遮罩子作品（`tags=sub`），在上面畫要挖掉的格 ⇒ `link_carve=<遮罩>` | 遮罩用原船的座標（框、甲板高度、肋位都照抄原船的產生程式）；補回來 ＝ 在遮罩上 carvevox |
| 4. 新加 | 新部件照一般的子作品做，關聯到改裝船（`at` 照原船的座標）；換色用 `recolor` | 改裝船的框可以比原船大（例：加高的桅）；原船的關聯放得進就好 |
| 5. 量 | 匯出 vxk 的關聯列「放 N、照遮罩挖掉 M」；部件外框少了拿掉的；匯入檢查 | 遊戲那側照一般的船匯入（存成差是遊戲打包的事，TASK-0657） |

- ⚠ 拿掉的部件如果被別的部件的 meta 指名（例：鍋爐 `funnel=煙囪`、彈藥庫 `hoist=梯子/turret`、裝甲 `mount=甲板-main/砲塔`）⇒ 那幾條在遊戲裡會對不上；要拿掉它就連指名它的一起拿掉或換掉。（之後改用 §12 的 id 寫法，路徑改名也不會斷。）
- ⚠ 原船裡的部件只能「拿掉／挖掉／換色」，不能搬位置、改 meta —— 要動就拿掉它、在改裝船裡放一份新的。
- 實例：哨兵號（`meadow-sentinel-01m`，2026-10-16）—— 原船莫尼特號組裝件，`link_hide=駕駛塔`、`link_carve=meadow-sentinel-01m-carve`；新加司令塔、速射砲台（見作品的 notes.md）。
