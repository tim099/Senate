---
title: 造船：舵與舵輪（照轉向轉）、操舵鏈
description: 照參數轉的子物件（spin）、每艘船都要配舵與舵輪、舵輪座、舵從格子改成模型、操舵鏈（helm_share、tiller、steer）
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
  - Ship_Oars.md | Ship_Oars | 槳（槳帆船）
  - Ship_Portlids.md | Ship_Portlids | 砲門蓋板
  - Ship_Game.md | Ship_Game | 給遊戲的 meta 總表、匯進 Blockside、物件 id 與掛點、交船檢查表
  - Ship_Refit.md | Ship_Refit | 改裝船（原船當一條關聯、只存差）
---

# ⛵ 造船：舵與舵輪（照轉向轉）、操舵鏈

> **什麼時候讀**：步驟 11（舵、舵輪、錨）。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

### 9.8 照參數轉的子物件：舵、舵輪（模型 meta `spin`；TASK-0640）

Tim 2026-10-16：「跟隨船隻轉彎方向去轉舵輪&舵(參考螺旋槳跟船速關聯)」「綁定某項參數&某個子物件的旋轉關聯(meta設定)」「舵面本身應該要有上限 舵輪可以沒有(可以轉超過一圈)」。
通用的綁法：模型 meta 寫 `spin` 就綁，**遊戲不看種類名**；沒寫 ⇒ 不轉（跟以前一樣）。

| meta | 意思 |
|---|---|
| `spin` | 來源參數：`rudder`（舵角 −1..1，左舵 ＝ 往左舷轉為正）｜`speed`（航速，節；倒退為負）｜`rpm`（螺旋槳轉速：自己是動力的輸出物件 ⇒ 自己的，否則這艘第一個螺旋槳的） |
| `spin_mode` | `angle`（預設）：角度 ＝ `spin_k` × 參數｜`rate`：每秒轉 `spin_k` × 參數 度（一直轉；rpm × 6 ＝ 每秒幾度） |
| `spin_k` | 倍率（度／參數單位；預設 1）；負的 ＝ 反著轉 |
| `spin_min`／`spin_max` | 上下限（度）；**沒寫 ＝ 那一頭無上限**（舵輪可以轉超過一圈） |
| `spin_lag` | 跟上的時間常數（秒；預設 0.3；0 ＝ 立刻）；angle 模式才用 |

- **轉軸**（照順序找第一個有的）：模型的固定點 `spin-a` → `spin-b`（中心 ＝ spin-a）｜meta `spin_pivot_x／y／z` ＋ `spin_axis_x／y／z`（組裝件座標，跟舵的 hinge_* 同一種寫法）｜舵的 meta `hinge_*` ＋ `axis_*`（§9 rudder 那一列，舵不用另外寫軸）｜固定點 `hub-aft` → `hub-fore`｜固定點 `hub` ＋ meta `axis_*`（螺旋槳）。
- **方向**：繞轉軸照右手定則（格座標 x 往船頭、y 往右舷、z 往上）。舵的軸朝上 ⇒ 正轉 ＝ 後緣擺向左舷 ＝ 左舵 ⇒ `spin_k` 寫正的；舵輪的軸朝船頭 ⇒ 正轉 ＝ 輪頂往左舷（「往要去的方向轉輪」）。
- **只是畫面**：轉了不影響碰撞、不改戰局；被打飛時擺回 0°。⛔ 掛著擺的小艇、門不收（它們自己在轉）。螺旋槳寫了 `spin` ⇒ 照這一套；沒寫 ⇒ 照 §9.6（遊戲照推力算轉速）。
- 匯入檢查：來源不認得、找不到轉軸、上下限顛倒、寫在小艇或門上 ⇒ ⚠；讀數那一行列出每個會轉的模型。
- ⚠ 還沒做：舵柄上的繩不跟著舵轉（滿舵時繩頭會離開舵柄）。

#### 9.8.1 每艘船都要配：舵與舵輪（Tim 2026-10-16：「要配置舵與舵輪」）

遊戲只轉**模型**（外部模型，`work sub=model`），⛔ 不轉關聯、更不轉船殼裡的格子。

| 部件 | 必要條件 | spin meta（照抄） |
|---|---|---|
| 舵 | 模型 `kind=rudder`＋`hinge_x／y／z`、`axis_x／y／z`（舵繞的那條線，組裝件座標）、`max_deg`、`turn_axis=hinge`；舵子作品登記固定點 `stock-top`（舵頭） | `{"spin":"rudder","spin_k":<max_deg>,"spin_min":-<max_deg>,"spin_max":<max_deg>,"spin_lag":0.3}` —— 軸用舵本來的 hinge／axis，不用另外寫 |
| 舵輪 | 模型 `kind=wheel`＋固定點 `hub-aft`、`hub-fore`（輪軸的兩端；共用的舵輪元件已經有） | `{"spin":"rudder","spin_k":540,"spin_lag":0.6}` —— 軸自動取 hub-aft → hub-fore；⚠ 540° ＝ 滿舵一舷一圈半，是挑的，不是史料 |

- 寫法（meta 不限作者 —— 遊戲用的資訊，誰都能補；Tim 2026-10-16「直接修改吧 不影響作品本身」）：
  `senate cmd sculpture --arg op=work --arg sub=meta --arg part=model --arg work=<模型所在的作品> --arg name=<模型名> --arg 'meta={…}'`。
  模型在甲板子作品裡（部件清單路徑「甲板-spar/舵輪」）⇒ `work` 填那條關聯的作品、`name` 填斜線後面那一段。
- 舵輪的軸也可以自己給（夜隼號、風暴角號的寫法）：`spin_pivot_x／y／z`（axle 固定點）＋`spin_axis_x／y／z`（朝船頭 1,0,0）。
- 讀回：`senate cmd sculpture --arg op=export --arg format=parts --arg work=<組裝件>`，每個 `kind=rudder`／`kind=wheel` 的模型 meta 裡都要有 `spin`。
- ⚠ **共用雙舵輪 `basecamp-frigate-constitution-01m-wheel` 只有輪、軸、鼓輪**：支柱與底座 2026-10-16 拆成 `-wheel-stand`（TASK-0640，跟著舵角轉的只有輪）⇒ 只掛輪 ⇒ 輪浮在甲板上（松島號 2026-10-23，Tim 截圖「松島號的舵輪會浮空」）。
  掛輪的同時要掛一個座：擺法照抄輪那一條（at、pivot、scale 同一組）、meta `kind=wheel_stand`。帆船（憲法號、餘燼號、夜燈號…）用共用的木頭雙柱座 `-wheel-stand`；
  ⛔ 鋼鐵艦不要照抄那件（Tim「其他船的舵輪支架跟松島號不同」）—— 松島號自己做了鑄鐵座 `ridge-001-matsushima-01m-wheel-stand`（底板＋兩根收窄的立柱＋黃銅軸承蓋，`design/draft_wheel_stand.py`，`material=iron`），同一套座標（0.02 m／格、軸 y 52 z 55.5、露出來的軸 x 13–25、74–85）可以照它改。
  - **輪綁在座上**（Blockside 10_ship.js restOn，2026-10-24；Tim「舵輪座被打飛後 舵輪飄浮在空中還在」）：座被打飛，輪跟著一起飛（同一個方向、同一下）；座被斷開的那一團帶走，輪也跟著走。
    - 不用寫：舵輪沒寫 `rests_on` ⇒ 遊戲自動綁外框碰得到（外擴 1 格）的那一個 `kind=wheel_stand`（照上面「擺法照抄輪那一條」掛的座都碰得到；16 艘都量過）。
    - 要指定（座不在輪旁邊、或旁邊有好幾個座）⇒ 輪的模型 meta 寫 `rests_on=<座的物件 id>`（例 松島號 `rests_on=wheel_stand-1`）；規格表 `wheel` 的 refs 寫了 `rests_on → wheel_stand`（`admin.py ids` 認得、指錯匯入檢查報）。
    - ⛔ 以前遊戲只看輪腳下的甲板：座飛了、輪照樣浮在原地。

**舵還是關聯（獨立子作品）或船殼裡的格子 ⇒ 改成模型**（⚠ 改模型、關聯、格子**只有作者能做**；改完舵會跟其他船一樣能被打飛 —— 打飛後只剩調帆轉向）：

1. 舵在船殼格子裡 ⇒ 先把舵的格（舵葉、舵柱、舵柄）切成獨立子作品：子作品跟組裝件同框、at 0,0,0 最省事（`carvevox` 從船殼拿掉、`stampvox` 落進新子作品，兩份清單同一份格）。
2. 量鉸鏈：每一層 z 取舵葉朝船頭最遠那一格（前緣）⇒ 擬合直線 x ＝ a ＋ b·z（丟掉離線超過 1.5 格的層：頂上往船頭伸出去的舵柄）⇒ `axis ＝ normalize(b, 0, 1)`，`hinge ＝`（前緣格角的外包絡在最低那層的 x、舵的中線 y、最低那層 z）。
   這把尺拿四艘作者自己標的舵對過：潮墓號、收穫號、拿破崙號的鉸鏈都在作者值 ±0.4 格內、軸向差 ≤ 0.0007（夜隼號差 3 格 —— 它的 hinge 是舵柱中心線，不是前緣；兩種都轉得起來）。
3. 舵子作品登記 `stock-top`（最高那層、舵頭那一格）：`work sub=anchor --arg work=<舵子作品> --arg name=stock-top --arg at=x,y,z`。
4. 組裝件拿掉舵的關聯、改掛成模型（同一件子作品、原位）：
   `work sub=unlink --arg name=舵`；
   `work sub=model --arg name=舵 --arg source_work=<舵子作品> --arg pivot=<P> --arg at=<P＋關聯的 at> --arg scale=1 --arg 'meta={"kind":"rudder","hinge_x":…,"hinge_y":…,"hinge_z":…,"axis_x":…,"axis_y":0,"axis_z":…,"max_deg":35,"turn_axis":"hinge","spin":"rudder","spin_k":35,"spin_min":-35,"spin_max":35,"spin_lag":0.3}'`
   （P 取鉸鏈在舵子作品座標裡的點；關聯原本 at 0,0,0 ⇒ at ＝ pivot ＝ P）。
5. 遊戲那側重匯之後：`__game.lostModels(0)` 不能有舵（開局就掉 ＝ stock-top 找不到船尾）；匯入檢查「轉動」那一行列出舵與舵輪。

**操舵鏈（TASK-0777；遊戲照 meta 算舵效，⛔ 不照名字）**：遊戲把操舵當成動力的一條鏈 —— 舵令 ⇒ 舵輪／舵柄（＋舵索）⇒ 舵；舵效 ＝ 每支舵「份量 × 鏈」相加（羅盤上方 ☸）。

| 要標的 | meta | 沒標會怎樣 |
|---|---|---|
| 舵（每一支） | `kind=rudder`（上表）；多支舵要分份量 ⇒ `helm_share`（數字，沒寫 ⇒ 平分：兩支壞一支掉一半） | 沒有舵模型 ⇒ 遊戲在船尾中線放一支看不見的舵（完好度照船尾的格子），匯入檢查 ⚠ |
| 舵輪 | `kind=wheel`（上表）；只推某一支舵 ⇒ `rudder=<那支舵的模型名>`（沒寫 ⇒ 推每一支） | 名字像舵輪（wheel／舵輪）卻沒有 meta ⇒ 匯入檢查 ✗：打掉不影響舵效 |
| 舵柄（獨立模型才標；舵柄畫在舵上就不用） | `kind=tiller`，其他同舵輪 | 舵柄畫在舵上 ⇒ 鏈上沒有操作端那一節，打掉舵才算 |
| 舵索、導索器（可選） | 模型 meta `steer=1`（＋`rudder=` 同上）；跟舵輪算同一節、**串聯**：斷一個 ＝ 舵輪沒了 | 不算進鏈（舵索斷了不影響） |

- 舵輪（舵柄）被打掉 ⇒ 舵效損失寫實 40%、舵被打掉 ⇒ 損失 100%，兩樣相乘；娛樂再 × 0.5（預設：80%、50%、40%）。兩舷都有動力的船（槳、兩舷螺旋槳）另外照舵令差動，不經過舵輪：舵沒了照樣轉。
- 槳的轉向力臂照槳葉固定點 `blade`（§9.10）—— 沒登記 ⇒ 照槳座，差動弱 2.6 倍左右。
- 19 型的現況（2026-10-21，Blockside dev 161e4a2 量，`sh.models`）：
  - 舵：19 型都是模型、都有 `stock-top`；兩支舵的是 dromon、punt（`rudder-S`／`rudder-P`，平分）。
  - 舵輪：15 型有、都有 meta `kind=wheel`。沒有舵輪的 4 型（reale、fusta、dromon、punt）舵柄畫在舵上 ⇒ 不用補；要讓舵柄能單獨被打掉 ⇒ 切成獨立模型標 `kind=tiller`。
  - 以前照名字被誤認成舵輪的（現在是一般模型，⛔ 不用改）：憲法號、炎羽號的「wheel座」（`wheel_stand`）、潮墓號的 `wheel-fairlead-1`／`-2`（`fairlead`；要算進舵索那一節 ⇒ 補 `steer=1`）。
