---
title: 任務管理 —— 開單、動工、交付、驗收、收工
description: senate cmd task（寫入）與 senate cmd tasks（唯讀）的使用說明：什麼時候該開單、驗收標準怎麼寫、claim／commit 閉環／check／退回／resolve／wrapup 的流程，以及 CLI 擋不了、要靠自己判的規則。3~5 人小團隊的輕流程。
cmds: [task, tasks]
---

# 📋 任務管理

> **3~5 人的輕流程。** 規範太多會讓小事變大事。
> 參數表看 `senate cmd help task`／`senate cmd help tasks`，⛔ 本檔不抄。
> 寫入端是 Senate Server，**不需要 Unity Editor**。

## 1. 東西寫在哪

| 問題 | 是 ⇒ |
|---|---|
| 有第二個人在等這件事嗎？ | 開 Task（回答「到哪了」） |
| 別人接手需要知道為什麼、哪裡會咬人？ | 工作記憶 |
| 只有我自己要記得？ | 見叢（`senate cmd keys`） |
| 逐步讀數、怎麼量的 | commit 訊息 |
| 討論、找人 | 酒館 |

- ⛔ Task 不抄進見叢 —— 早安 brief 自己會撈「我涉及且未結」的單。
- ⛔ 「等某人決定」不寫見叢（別人讀不到），寫在單上。

## 2. 該不該開單（先問這個）

**用中發現的問題，預設是當場修掉、寫進 commit 訊息。** 只有三種才開單：

| 開單 | 因為 |
|---|---|
| 修不掉 | 缺前提、要等別的東西先做完 |
| 要別人決定 | 政策、跨人邊界、動到別人的檔 |
| 會再犯、而且沒有機械擋著 | 要有人排它 |

⇒ `op=create` 時填不出「誰在等」就別開 —— 沒人等的單不是任務，是備忘。

- **一件事一張單。** 改變數名、補註解、改幾行文件不開單；文件更新併進程式那張單當一個細項。
- **處置範圍 ≤ 開單時的症狀。** 旁邊看到的：順手能修的當場修（寫 commit）；同一個修法能解的擴充本單細項；修法不同的留言一行「不在本單射程」。
- 測試是被測那張單的細項，⛔ 不開探針單。唯一例外是被測的就是 Task 系統本身：探針帶 `--arg tags=probe`、不掛傘、當天 `resolve --arg status=cancelled`。
- 要求別人多量一格之前，先問「不量它，最壞會怎樣」—— 答不出具體損害就不要求。

## 3. 開單

```bash
senate cmd task --arg op=create --arg persona=<我> --arg title="<標題>" --arg-file criteria=<檔>
senate cmd task --arg op=create --arg persona=<我> --arg type=bug --arg title="<症狀>" --arg-file evidence=<檔>
```

- **多行內容一律 `--arg-file`**（`criteria`／`description`／`evidence`／`body`／`progress`／`why`）—— `--arg` 裡的 `\n` 是字面兩個字元，多格會擠成一格。
- **bug**：`evidence` 必填，放硬證（error／log／重現指令）＋**這個讀數怎麼拿到的**；標題寫症狀，不寫猜的原因。驗收自動帶兩格（① 重現讀數 ② 修正落盤）。拿不出硬證的摩擦改開 `type=improvement --arg tags=friction`。
- `severity` 是壞得多兇，`priority` 是多急排 —— 兩把尺。

### 驗收標準怎麼寫

**寫「什麼算做完」，不寫「怎麼做」。** 一項一行 `- [ ]`：

```
- [ ] 收工自動匯出跑完，章檔真的在 BookNotes/ 底下、內容不是空的
- [ ] 反向對照：不給 confirm ⇒ 一張都沒改
```

- ⭐ **反向對照那一行每次都值得寫** —— 少了它，永遠會通過的驗收跟真的驗收長得一樣。
- ⛔ 沒有 `- [ ]` 的驗收標準結構上簽不掉；⛔ 寫不到的條件（例如「由別人複驗」）也一樣。一個簽不掉的條件跟沒有條件，在看板上長得一樣。
- ⛔ 行號、函式名、參數不寫進驗收標準（那是 dev 筆記）。
- **只有單上指名 QA 才拆「① 交付／① 驗收」兩行**；沒有 QA 的單一行就好。
- 某一格只能由某人本人簽 ⇒ 那一行放 `[signer:<persona>]`（裸 `@persona` 不算）。
- 不做異源複驗：一人全包的單自己測過就結。需要第二條**路徑**（換工具／換客戶端／讀磁碟而不是讀回傳）的，開單人自己加一行。

## 4. 角色與動工

實際影響流程的只有兩個角色：

| 角色 | 影響 |
|---|---|
| `dev`（`design`／`sound`／`art` 同） | `claim` 把 `todo`／`backlog` 推成 `in_progress` |
| `qa` | 填了 ⇒ 只有他能 `check`；`commit` 只推到 `in_review`；別人 `resolve` 要帶 `qa_note` |
| `pm`／`reviewer` | 只是標籤，不改狀態。⚠ `pm` 不是 QA —— 有人管不等於有人驗 |

動工（會改 C#）就認領**並帶範圍**：

```bash
senate cmd task --arg op=claim --arg persona=<我> --arg index=<N> --arg scope="<這一場的最大範圍，絕對路徑>"
```

- 帶 `scope` ＝ 認領＋開 Coding 場＋綁單（已有場就補綁，不開第二場、不擴大範圍）；**開不了場就不認領**（零寫入）。
- 綁了單的場在單子進 `in_review`／`done` 後由 `senate cmd commit` 自動收。範圍判準 → `senate cmd help coding`。
- 不帶 `scope` 只是「記錄我在做這件事」。

## 5. 施工中

- **留言只留三件：判定、憑據、球在誰。** 過程寫 commit 訊息／工作記憶／酒館，⛔ 不把單子當工作日誌。
- **擴充驗收細項**：`op=update --arg-file criteria=` 是**整段覆寫**。先讀單檔裡 `## 驗收標準` 整段（含散文與小標），原封不動接上新的 `- [ ]` 再寫回，然後 `op=comment` 寫「為什麼加這幾格」。
  覆寫若讓散文歸零或砍掉一半以上會被擋（零寫入）；真的要縮帶 `--arg allow_shrink=1`。
- 只是要打勾 ⛔ 不走 update，走 `op=check`。
- `op=update` 不能推 `done`／`cancelled`（結單走 `resolve`），**也不發酒館通知**。

## 6. 交付：commit 閉環

`senate cmd commit` 的訊息裡**頂格**寫：

| trailer | 效果 |
|---|---|
| `Fixes TASK-<N>` | 有 QA ⇒ `in_review`；沒有 QA ⇒ `done`。有未解 blocker 不推 |
| `Refs TASK-<N>` | 只掛 sha，不動狀態 |

同一張同時寫兩者 ⇒ Fixes 優先。推單是 commit 的附帶效果：失敗只警告，commit 本身已落地。commit 參數 → `senate cmd help commit`。

## 7. 驗收與結單

```bash
senate cmd task --arg op=check --arg persona=<我> --arg index=<N>                     # dry-run：印未勾清單，零寫入
senate cmd task --arg op=check --arg persona=<我> --arg index=<N> --arg criteria_index=2 --arg expect_text="<那一行的前綴>"
```

- **打勾是簽名**：行尾接上署名與日期。誰能勾：有 QA ⇒ 只有 QA；沒有 QA ⇒ 參與者＋開單人；`[signer:]` 那格只有本人。
- `criteria_index` 是**未勾清單**的序號，別人勾掉任何一格就會位移 ⇒ 跨過一次 dry-run 才決定的，**一律帶 `expect_text`**。
- ⛔ 同一個人不要交付／驗收兩行都勾；真的只有一個人，就在 `resolve` 的 `note` 寫「我兼驗收，沒有第二人」。
- 勾不推狀態。

### 驗收不過 ⇒ 單子必須離開 `in_review`

```bash
senate cmd task --arg op=update --arg persona=<我> --arg index=<N> --arg status=in_progress
senate cmd task --arg op=comment --arg persona=<我> --arg index=<N> --arg-file body=<哪一格沒過、讀數、怎麼重現>
```

兩步是**同一個動作**：留言不是退回（停在 `in_review` 的單看起來像「還沒人來驗」，兩邊會互等）；而 `update` 不發通知，dev 靠那則留言才知道。
⛔ 施工中的瑕疵不另開 bug 單 —— `type=bug` 給已經交付出去的東西。

### 結單

```bash
senate cmd task --arg op=resolve --arg persona=<我> --arg index=<N> --arg status=done --arg note="<驗收讀數>" --arg confirm=1
```

閘：不帶 `confirm=1` 只印不寫；`done` 時有未解 blocker 擋下（`cancelled` 放行）；有 QA 而你不是他 ⇒ 要帶 `qa_note`。

## 8. 收工（今天不做了）

```bash
senate cmd task --arg op=wrapup --arg persona=<我> --arg index=<N> --arg-file progress=<檔> [--arg-file why=<檔>]
```

- `progress` 固定三句：球在誰／今天推進了哪幾格／下一步從哪接。⛔ 不寫過程。狀態不動。
- `why`（卡在哪、試過什麼不行）寫進工作記憶，單子要先有 `memory_topic`（`op=update --arg memory_topic=`），否則擋下。寫入與 `senate cmd work-memory` 同一支（見 `Work_Memory`）；寫不成只是警告，進度已落盤。
- 這次上線動過、還開著、我有參與的單，沒收工會擋晚安下線；`senate cmd tasks --arg persona=<我> --arg wrapup=1` 預覽。要跳過走 `op=wrapup_skip --arg reason=`（理由留在單上）。
- 醒來接手：先讀自己單上的新留言，再開工。
- `memory_topic` 只給跨日、會換人接手的單綁；綁了之後 `senate cmd work-memory --arg op=read --arg topic=<t>` 會印主題卡 `key_docs` 指的權威文件（反向掛單 → `Work_Memory`）。
- 晚安 `goodnight-check` 會對帳（只印不改）：見叢殘留的 `[TASK-n]`、我涉及的未結單張數、逾期認領、記憶錨點斷鏈或久未更新，以及等一下 sleep 會因哪幾張沒收工而擋下。

## 9. 定期整理（掛在晚安對帳與看清單時，不另立儀式）

- **沒有人在等的單是關掉，不是合併**：`op=resolve --arg status=cancelled --arg note="沒有人在等，降為備忘" --arg confirm=1`。
- 合併只合「同一個修法能一次解掉」的（同根因／同一支檔／同一個要拍板的決定）；主題像但修法不同的不合。
- 逾期認領（`in_progress` 且 14 天沒動）：`op=sweep` 先看候選，`--arg confirm=1` 才釋放回 `todo`。
- **大項目才開傘**（跨好幾天、多人分頭做）：`type=epic`，子單 `op=link --arg op_link=subtask_of --arg target=<傘>`；傘開單當天綁 `memory_topic`；子單動工當天把傘推 `in_progress`；進度寫傘的 wrapup。排順序看 `blocked_by`，不看優先度。
  收傘：最後一張子單結掉 → `work-memory op=archive` → 傘 `op=update --arg memory_archived_commit=<sha>` → 傘 `resolve`。

## 10. 讀單

```bash
senate cmd tasks --arg status=open                 # 計數＋清單（status／type 可疊）
senate cmd tasks --arg persona=<我>                 # 多印「我參與幾張、開著幾張」（只是計數，不篩清單）
senate cmd tasks --arg index=<N>                   # 單張摘要（狀態、參與者、blocker、記憶主題、留言數）
```

- 驗收標準、描述、留言全文在單檔 `<data_root>/Tasks/tasks/<NNNN>.md`（`tasks` 第一行印 data_root），或看後台頁。
- 依參與者篩：後台頁。依 milestone／tag／epic 篩：沒有參數 ⇒ `--arg out_json=<檔>` 落完整資料自己篩。
- 後台頁 `senate ui --page tasks`：篩選（狀態／類型／參與者）、看驗收標準與描述、留言、推狀態；寫入同樣走 `task`。

## 11. 結果怎麼讀（`task`）

| exit | 意思 | 下一步 |
|---|---|---|
| 0 | 已寫（dry-run／冪等看 `🔢 wrote`） | 讀回傳檔 |
| 1 | 閘擋下，零寫入 | 原因在回傳檔 `## ❌ 失敗`／`## blocked` |
| 2 | 參數錯（打錯參數名會列出該 op 認得的鍵） | 修參數 |
| 6 | 確定沒寫 | 修好重跑是安全的 |
| 7 | **不知道**（送出了但等不到回執） | ⛔ 先 `senate cmd tasks --arg index=<N>` 回讀，別直接重打 |

寫完之後的酒館通知失敗只是警告 —— 單子已寫好，⛔ 別因此重打（留言會多一則、勾會多一格）；通知顯示「已排隊」也不要補發。
