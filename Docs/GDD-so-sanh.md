# Đối chiếu game với GDD "Đại chiến Công Thành Tam Quốc"

Ngày đối chiếu: 2026-11-12. Chỉ đọc code, chưa sửa gì.
Nguồn: Google Doc `14Eoa_sOp5X539B0nAP-JlQSmXar1Hig_e6hBSWEfZF4` (bản export text cùng ngày).
Đường dẫn viết tắt: `RP/` = `Assets/Scripts/RecoilPathfinding/`, `ABD` = `RP/RecoilAutoBattleDemo.cs`, `SA/` = `Assets/StreamingAssets/`.

Ký hiệu: ✅ ĐÚNG · ⚠️ KHÁC · ❌ THIẾU · ❓ KHÔNG RÕ

## Tóm tắt: những khác biệt lớn nhất

| # | Vấn đề | GDD | Game hiện tại |
|---|---|---|---|
| 1 | **Thành chỉ bị đánh khi phe thủ hết lính** | Thành/tháp nhận sát thương từ khi nhấn L | `blueMayAttackCastle = !HasAliveSoldier(_redTeam)` (ABD:2210-2222, kiểm lại lúc đạn trúng :3090). Còn giao tranh thì máu thành không tụt. |
| 2 | **Last Stand (phản công 30%)** | ≤30% HP, 1 lần/trận, 20 s, ×2 HP/Damage/AttackSpeed/MoveSpeed/Defense, không ×2 tầm, hết buff quy HP theo %; có UI riêng | ✅ Đã thêm 2026-11-12 (`RP/RecoilLastStand.cs`). Vẫn phụ thuộc #1: với luật hiện tại, lúc thành ≤30% phe thủ thường chỉ còn quân tiếp viện (quân sinh trong 20 s cũng được ×2). |
| 3 | **Tháp canh + cung thủ NPC trên thành** | Tự bắn địch trong tầm, tầm xa hơn cung binh; so máu tháp khi hòa | ❌ Không có "tower" trong code/scene. |
| 4 | **Bonus công trình** | Máy bắn đá, Voi có bonus damage lên thành/tháp | ✅ Đã thêm 2026-11-12: chỉ số "Bonus đánh công trình" trong CSV lính / menu Cấu hình lính (cột "Công trình (+%)"), mặc định Máy bắn đá +100 %, Voi +50 %; áp dụng cho đòn cận chiến, đạn và skill tướng lên thành (`RecoilUnitDamage.CalculateStructure`). |
| 5 | **Chuỗi thắng không lưu** | Lưu theo user, tính theo tuần, reset về 1 khi reset BXH tuần | ✅ Đã thêm 2026-11-12 (`RP/RecoilRankSync.cs`): khi người xem vào trận đọc `GET /api/rank/weekly/kill/{id}` làm chuỗi xuất phát (server chưa biết → 1); hết trận `POST /api/rank/update` với matchPoint = điểm trận, killCount = **thay đổi** chuỗi (âm khi thua). Reset tuần do server làm. Giả định: server cộng dồn killCount và nhận số âm. |
| 6 | **Video chiến thắng, video Top BXH** | Kết trận chiếu video phe thắng; có "Video Top BXH" | ❌ Không có VideoPlayer ở endgame; chỉ có thiên thạch nổ thành thua + cờ phe thắng. |
| 7 | **Ô top sau trận** | "Điểm cố định (+thưởng), chuỗi thắng (±), hạng (±)" | ✅ Đã thêm 2026-11-12: thẻ top 3 có 3 dòng, dòng BXH có cả 3 số. Hạng = hạng trên bảng **tuần · điểm** của rank server (đọc lúc vào trận và sau khi gửi kết quả); "—" khi server chưa trả. |
| 8 | **Tướng bị đảo phe** | Đội 1: Triệu Tử Long, Quan Vũ, Gia Cát Lượng, Lưu Bị. Đội 2: Hạ Hầu Đôn, Lữ Bố, Chu Du, Tào Tháo | ⚠️ Phe Đỏ (=1) đang nhận tướng của đội 2 (`SA/tiktok_army_settings.csv:8-11`). Cần chốt "đội 1" là phe nào. |
| 9 | **Ngưỡng ra tướng** | x10 quà (x5 Hạc Giấy) **hoặc** tích đủ 20 (10) | ✅ Đã thêm 2026-11-12: cột "Combo ra tướng" (Rosa/Perfume/Doughnut 10, Hạc Giấy 5) — một combo đạt mốc ra tướng ngay, song song với tích lũy 20/10 (`CheckTikTokHeroCombo`). |
| 10 | **Chọn thời gian trận** | Nút 5/10/15/20/Tùy chỉnh, [THÔNG SỐ LÍNH], [XÁC NHẬN] | ⚠️ Đồng hồ xoay giờ/phút ở sảnh, mặc định 10 phút, 0:00 = không giới hạn (`RP/RecoilClockDial.cs:116-120`). |
| 11 | **Phím K** | L vô hiệu nếu chưa nhấn K | ❌ Không có phím K. L chỉ cần: đã Start, đã xuất quân, mỗi phe ≥1 lính (ABD:2111-2116). |
| 12 | **Tầm đánh đo tâm–tâm** | Đo mép collider → mép collider (Voi) | ⚠️ `distancesq(pos, target) ≤ Range²` (ABD:343-392; `RP/RecoilCombatResolveSystem.cs:224-356`). |
| 13 | **Đội hình** | Khối theo loại, hệ số chiều ngang, khoảng cách 10-15 m / 6-10 m / 25-35 m; tướng đứng cùng khu loại quân | ⚠️ Lưới 13×13 tự do, mọi hàng trải hết `formationWidth`, hàng cách hàng 2 m; tướng luôn ở hàng `heroFormationLine`=2 giữa đội hình. |
| 14 | **ID Hạc Giấy** | 5659 | ✅ Đã sửa CSV về 5659 (2026-11-12). |

---

## 2. Trình tự vận hành trận đấu

### Bước 1 – Chọn thời gian
- ⚠️ Không có nút 5/10/15/20/Tùy chỉnh; dùng đồng hồ xoay 0–11h59 (`RP/RecoilClockDial.cs:116-120,184`; `RP/RecoilMatchTimer.cs:35-39`).
- ⚠️ Không có [CHỌN THỜI GIAN TRẬN] / [XÁC NHẬN]; sảnh chỉ có Start (ghi giờ rồi load luôn) và Settings (ABD:5489-5492).
- ⚠️ Không có màn [THÔNG SỐ LÍNH] riêng; nằm trong Settings, tab "Setup lính / quà / tướng / Quân đầu game / chuột" (`RP/RecoilBattleTikTokSettings.cs:149-175`).
- ✅ Đồng hồ trận chỉ chạy sau khi hết đếm ngược L (`RP/RecoilBattleFormation.cs:316`; `RecoilMatchTimer.cs:29`).

### Bước 2 – Vào map (WAITING)
- ✅ Máu thành đặt lại đầy (ABD:2199-2200; `RP/RecoilRetryInPlace.cs:117`).
- ❌ Không có tháp canh.
- ✅ Quân/tướng/dữ liệu cũ bị xóa khi Retry/Home (`RecoilRetryInPlace.cs:56-131`).
- ⚠️ Nếu bật "Quân đầu game" (`prepareArmyBeforeCombat`, ABD:1321) thì đội quân ban đầu được đặt sẵn ngay khi load (`RecoilBattleFormation.cs:100-112`).
- ✅ AI không đánh trước trận: combat job chỉ chạy khi `_combatStarted` (ABD:2166-2171).
- ⚠️ Không có enum trạng thái WAITING/PREPARATION/COUNTDOWN/BATTLE/END; ghép từ các cờ `_lobbyStarted`, `_lobbyStarting`, `_startCountdownEndsAt`, `_combatStarted`, `_matchFinished`.

### Bước 3 – Chuẩn bị (PREPARATION)
- ✅ Nhận quà, triệu hồi, có hàng chờ, tối đa 1000 quân/phe trước trận (`RP/RecoilBattleTikTokEvents.cs:140-142,314-327`; `RP/RecoilWaitingArmy.cs:52-60`).
- ❓ "Đi từ cổng thành ra khu tập kết": quân sinh tại `blue/redSpawnTarget` (ABD:4936-4941) rồi đi tới ô; không có khái niệm "cổng".
- ✅ Tự tìm ô trong đội hình theo loại (`RecoilWaitingArmy.cs:88-161`).
- ⚠️ Không có "vạch xuất phát" tường minh; quân đứng yên vì đích là ô đội hình.
- ✅ Không tấn công, không nhận sát thương; đồng hồ đứng yên; giữ bao lâu cũng được (HUD "Đã vào hàng — bấm L", `RecoilWaitingArmy.cs:187`).

### Bước 5 – Nhấn L → COUNTDOWN → BATTLE
- ⚠️ L cần: đã Start, `_lobbyStarting`, không đang load, mỗi phe ≥1 lính sống (ABD:2111-2116).
- ❌ Phím K không tồn tại. Phím đang có: C (ẩn trỏ), TAB (đổi HUD), R/B (ép đỏ/xanh thắng ngay, ABD:2101-2102), P (tốc độ ×10, `RP/RecoilSimulationClock.cs:60`).
- ⚠️ Đếm 3-2-1 có số lớn + tiếng trống nhưng không có chữ "BATTLE START" (`RecoilBattleFormation.cs:298-311`).
- ✅ Hết đếm thì đồng hồ chạy, quân xuất phát theo sóng, gặp địch thì đánh (`RecoilBattleFormation.cs:313-323`; `RP/RecoilCombatLaunch.cs`).
- ⚠️ Thành chỉ bị đánh khi phe thủ hết lính (ABD:2210-2222) — xem Tóm tắt #1.
- ❌ Không có tháp, không có Last Stand.
- ✅ Vẫn nhận quà trong trận.

### Mục 6 – Quân sinh sau khi L
- ❓/⚠️ Sinh theo hàng 10 lính tại điểm spawn rồi đi theo lane về phía địch (`RP/RecoilBattleSpawnStream.cs:14-23`; ABD:4144-4149). Không có "trong thành → khối đội hình → qua cổng".

---

## 3–4. Đội hình trước trận

Cấu hình: `SA/recoil_initial_army.csv` (= mặc định `RP/RecoilInitialArmySettings.cs:107-122`). Lưới X = cột (0 giữa, X>0 đối xứng), Y = hàng (0 = hàng đầu). Mã loại: 0 Cung, 1 Dân binh, 2 Giáo, 3 Kỵ, 4 Máy bắn đá, 5 Pháp sư, 6 Voi, 7 Bộ binh.

Thực tế: Y0 Voi giữa, Giáo X2, Kỵ X4 · Y1 Dân binh X1, Bộ binh X3 · Y2 Cung · Y3 Pháp sư · Y4 Máy bắn đá (3 máy).

| Yêu cầu GDD | Trạng thái | Ghi chú |
|---|---|---|
| Voi ở giữa tuyến hai | ⚠️ | Voi ở giữa hàng đầu Y0 |
| Bộ + Voi là lõi phía trước | ⚠️ | Bộ ở Y1, chia 2 khối bên, không chung lõi với Voi |
| Kỵ hai cánh | ✅ | X4 ngoài cùng, nhưng chung hàng với Voi/Giáo |
| Giáo làm tuyến trước liền | ⚠️ | Chỉ 2 khối X2 ở Y0 (GDD cũng tự mâu thuẫn: sơ đồ vẽ Giáo sau lõi) |
| Hệ số chiều ngang ×1.7-2.0 / ×1.2-1.4 / ×0.7-0.9 / ×0.8-1.0 | ❌ | Mọi hàng trải hết `formationWidth` (390 m, thu theo map ≈ 85 m; Settings.cs:334,356); trong hàng chia theo số quân × pitch (`RP/RecoilInitialArmyPlanner.cs:145-154`) |
| Cung cách Giáo 10-15 m | ⚠️ | Hàng cách hàng `clearance*2` = 2 m (Planner.cs:171; `formationUnitSpacing`=1) |
| Pháp sư trong đội hình Cung, cách 6-10 m | ⚠️ | Hàng Y3 riêng, cách ~2 m |
| Máy bắn đá gần thành nhất, cách tuyến trước 25-35 m | ✅/❌ | Hàng cuối ✅; không có tham số 25-35 m |
| Tướng đứng cùng khu loại quân | ⚠️ | Mọi tướng ở hàng `heroFormationLine`=2 giữa đội hình (`RP/RecoilWaitingHeroes.cs:12-66`), không xét loại |
| Quân mới: ra cổng → nhóm cùng loại → ô trống gần nhất → đi → chờ | ⚠️ | Nhóm theo loại ✅; nhưng mỗi quân mới gọi `ReplanWaitingArmy` dựng lại cả đội hình, gán ô theo thứ tự danh sách (hàng đầu trước), không theo ô gần nhất; quân đã đứng có thể bị dời (`RecoilWaitingArmy.cs:88-124`; Settings.cs:397-418) |
| Menu chỉnh đội hình | ✅ | "QUÂN ĐẦU GAME" lưới 13×13, đối xứng, lưu CSV (Settings.cs:182-320); không chỉnh được khoảng cách (m) hay hệ số chiều ngang |

Hằng số khác: tuyến đầu cách giữa map `FormationFrontGap` = 8 m, độ sâu tối đa 400 m (Settings.cs:424).

---

## 7–8. Quân, khắc chế, chỉ số

### Bộ chỉ số (8.x)
- ✅ Đủ 8 chỉ số + AttackType: MaxHealth, Damage, Range, AttackSpeed, MoveSpeed, Armor, SecondaryArmor (= P. Armor), AreaRadius, AttackType (`RP/RecoilUnitStats.cs:10-33`). Thêm: AreaEdgeEffectiveness, Salvo, Windup, target mask, priority.
- ✅ Lưu ở `SA/recoil_unit_stats.csv`, reload nóng (`RP/RecoilBattleTikTokSettings.cs:26-45`).
- ✅ Menu "CẤU HÌNH LÍNH" chỉnh 8 chỉ số, áp dụng hai phe (TikTokSettings.cs:299-367).
- ❌ Không chỉnh được AttackType trong menu (`DrawUnitAttackTypeControl` không được gọi, `RP/RecoilBattleCombatRoles.cs:31`); chỉ đổi qua CSV.
- ⚠️ Armor trong CSV là phân số 0-1 (menu hiện %). Giáp gần Cung = 0.0921 trông như nhập nhầm.
- ✅ AttackSpeed = đòn/giây, `AttackInterval = 1/AttackSpeed` (UnitStats.cs:17,32). Giá trị: Cung 0.8, Dân 1, Giáo 0.8, Kỵ 0.9, Máy 0.2, Pháp 0.5, Voi 0.4, Bộ 1.
- ✅ Range: Bộ 1.5, Giáo 2.3, Cung 14, Máy 25 (khớp ví dụ GDD); Dân 1.2, Kỵ 1.8, Voi 2.5, Pháp 12.
- ⚠️ Range đo tâm–tâm, không trừ bán kính collider (Voi gặp đúng vấn đề GDD cảnh báo).
- ⚠️ MoveSpeed: Kỵ 4 · Bộ/Dân 3.5 · Pháp 3.3 · Giáo 3.2 · Cung 3.0 · Máy 2.7 · Voi 2.5. Lệch GDD: Cung chậm hơn Giáo (GDD: Cung ngang Bộ, nhanh hơn Giáo); Voi chậm hơn Máy (GDD: Voi nhanh hơn Máy).

### Giáp và công thức sát thương (8.6, 8.7, 8.10)
- ✅ Hai loại giáp: Armor (gần) và SecondaryArmor (xa); đánh xa dùng giáp xa (UnitStats.cs:44-46).
- ✅ Công thức `round(raw × (1+Bonus) × (1−clamp(armor)))` (UnitStats.cs:72-79). Khác nhỏ: làm tròn số nguyên (21,85 → 22).
- ✅ Phân loại: Cung, Máy, Pháp = xa; Dân, Bộ, Giáo, Kỵ, Voi = gần.

| Loại | Giáp gần | Giáp xa |
|---|---|---|
| Cung | 0.0921 | 0.05 |
| Dân binh | 0 | 0 |
| Giáo | 0.15 | 0.10 |
| Kỵ | 0.5 | 0.15 |
| Máy bắn đá | 0.05 | 0.10 |
| Pháp sư | 0.05 | 0.15 |
| Voi | 0.35 | 0.25 |
| Bộ binh | 0.2 | 0.10 |

Chỉ số khác ví dụ GDD (ví dụ chỉ minh họa): Damage Kỵ 28 (GDD 20), Giáo 22 (GDD 30); giáp gần Kỵ 0.5 (GDD 20%).

### AoE (8.8)
| Loại | CSV | GDD | |
|---|---|---|---|
| Voi | 3 | 2.5-3.5 | ✅ |
| Máy bắn đá | 5 | 4-6 | ✅ |
| Pháp sư | 4 | 3-5 | ✅ |
| Bộ, Dân, Giáo, Cung | 0 | 0 | ✅ |
| Kỵ | 0 | 0 hoặc 0.5 khi Charge | ❌ phần Charge |

- AoE Voi: tâm mục tiêu → tâm lính địch, không giảm dần (ABD:170-188). AoE đạn (Máy, Pháp): điểm nổ → mép thân (ABD:3439-3454).
- ✅ Sát thương đều trong vùng (đúng GDD hiện tại). Hạ tầng falloff đã có (`AreaEdgeEffectiveness`) nhưng ép = 1 (CombatRoles.cs:25-26), không có trong CSV/menu.

### Bảng khắc chế (8.9)
- ✅ Là bonus %, khớp từng ô bảng GDD (`RecoilUnitDamage.Bonus`, UnitStats.cs:48-70). Dùng ở mọi đường gây sát thương: cận chiến, đạn/AoE, skill tướng.
- ❓ Dân binh: không có bonus nào và không ai được bonus khi đánh Dân binh (chủ ý trong `RP/RecoilBattleCombatValidation.cs:1843-1845`). GDD nói "bị khắc chế bởi tất cả" nhưng không cho số.
- ❓ Tướng: CombatRole = Unknown (TikTokSettings.cs:363) → không nhận/gây bonus khắc chế.

---

## 9. Tướng
- ⚠️ Ngưỡng: chỉ tích lũy 20 (Rosa/Perfume/Doughnut) hoặc 10 (Hạc Giấy) mỗi user; không có đường combo x10/x5 (`RecoilBattleTikTokEvents.cs:31-39`).
- ⚠️ Đảo phe (xem Tóm tắt #8): Rosa Đỏ=Hạ Hầu Đôn/Xanh=Triệu Vân; Perfume Đỏ=Lữ Bố/Xanh=Quan Vũ; Doughnut Đỏ=Chu Du/Xanh=Gia Cát Lượng; Hạc Giấy Đỏ=Tào Tháo/Xanh=Lưu Bị.
- ✅ Đủ 8 tướng, gần/xa đúng GDD (`SA/recoil_hero_stats.csv`; tên ở `RP/RecoilBattleHeroSettings.cs:20`). Code ghi "Triệu Vân", GDD "Triệu Tử Long".
- ✅ Mỗi user một tướng/loại quà còn sống (TikTokEvents.cs:40-54).
- ✅ Trước L vào đội hình chờ, sau L ra như tiếp viện (ABD:4261-4264; TikTokEvents.cs:76-77).
- ✅ HP cao: MaxHealth = 10.000.000 cho cả 8 tướng → gần như không thể chết, nên chết/hồi sinh hầu như không xảy ra.
- ✅ Thanh máu, avatar, tên user, tên tướng trên đầu (ABD:5399-5441).
- ❓ VFX nhận diện riêng (aura): không thấy; chỉ có VFX skill.
- ✅ Kỹ năng riêng: 8 profile `Data/CharacterProperties/RecoilHeroSkills.asset`, ulti hồi 30 s (Lưu Bị, Tào Tháo 5 s); Lưu Bị Damage 200, tướng xa khác 35.
- ✅ Chết → hồi sinh sau `RespawnSeconds` = 30 s (ABD:5122; TikTokEvents.cs:90-103).

---

## 10–11. Thành, tháp canh, Last Stand
- ✅ Mỗi phe một thành có Castle HP (mặc định 100.000.000, chỉnh 1M–500M, CSV "Máu thành"; ABD:1220-1222,1429-1430). Thành cháy khi trúng đòn (`RP/RecoilCastleFire.cs`).
- ❌ Cung thủ NPC trên thành.
- ❌ Tháp canh.
- ✅ Bonus công trình (thêm 2026-11-12): `StructureBonus` trong `RecoilUnitCombatStats`, CSV cột "Bonus đánh công trình" (1 = +100 %), menu "Công trình (+%)"; mặc định Máy bắn đá 1, Voi 0.5, còn lại 0 (`RecoilUnitDamage.DefaultStructureBonus`). Tướng = 0 (không có cột trong CSV tướng). Không có giáp cho thành.
- ⚠️ Mọi loại lính, Quân sư và skill tướng đều đánh được thành (ABD:2365-2400; `RP/RecoilHeroSkills.cs:493-505`) **nhưng chỉ khi phe thủ hết lính sống**.
- ✅ Last Stand (`RP/RecoilLastStand.cs`, thêm 2026-11-12): ngưỡng `lastStandCastleFraction` = 30%, 1 lần/phe/trận, `lastStandSeconds` = 20 s, `lastStandMultiplier` = 2 cho HP / sát thương / tốc độ đánh / tốc độ chạy / phòng thủ (sát thương nhận ÷2), tầm giữ nguyên, hết buff giữ % máu; chỉ chạy khi `_combatStarted`. UI 16.3: banner "ĐỘI … PHẢN CÔNG!" + giây còn lại, thanh máu thành cháy, mép màn hình phía phe đó loé đỏ/xanh, tiếng trống.

---

## 12. Mapping quà
Nguồn đang chạy: `SA/tiktok_army_settings.csv` (Sample `RP/RecoilTikTokSample.cs` chỉ dùng khi chưa có CSV).

| Quà | GDD | Game | |
|---|---|---|---|
| 50 Tym | +2 Dân binh | Like cộng dồn theo user, đủ 50 → 2 Dân binh (CSV:2; TikTokEvents.cs:224-235) | ✅ |
| Follow | (ô trống; mục 7 ghi Dân binh = "Tap tay, Follow") | UnitType=-1, số lượng 0 (CSV:6) | ❌/❓ |
| Hoa Hồng 1 xu | +50 Bộ binh | CSV:7 | ✅ |
| Rosa 10 xu | +50 Cung binh | CSV:8 | ✅ |
| Perfume 20 xu | +20 Giáo binh | CSV:9 | ✅ |
| Doughnut 30 xu, 5879 | +20 Kỵ binh | CSV:10 | ✅ |
| Hạc Giấy 99 xu, 5659 | +10 Voi chiến | +10 Voi ✅ nhưng ID 8913 (CSV:11) | ⚠️ |
| Kính Râm 199 xu, 5509 | +4 Máy bắn đá | CSV:12 | ✅ |
| Corgi 299 xu, 6267 | +2 Pháp sư | CSV:13 | ✅ |
| Heo 10 xu, 8825 | random ½ số lượng, 40/34/20/4/2 % | Tỉ lệ đúng, CountMultiplier 0.5 làm tròn xuống → 25 Cung / 10 Giáo / 10 Kỵ / 5 Voi / 2 Máy; không cộng tiến độ tướng (TikTokEvents.cs:250-297) | ✅ |

- ⚠️ Nhận diện quà: theo ID → tên → giá xu (`RP/RecoilTikTokGiftMapping.cs:12-18`). Hoa Hồng/Rosa/Perfume không có ID, quà lạ trùng giá sẽ khớp nhầm (vd. quà 10 xu khác tên → Rosa).
- ⚠️ Combo x10 quân: Ngưỡng=1 nên x10 Rosa = 500 Cung binh (TikTokEvents.cs:233,241). GDD không nói rõ.

### Intro
- ❌ Không có intro Dân binh (clip index 1 trống cho cả hai phe, `Scenes/new.unity:6603,6612`).
- ⚠️ Intro lính: rộng ≤560 px×scale, cao ≤0.3 màn hình (≈¼ ✅, `RP/RecoilHeroIntro.cs:293-294`) nhưng **không giới hạn 2 s**: clip phát hết, mức an toàn max(10 s, clip+5 s) (HeroIntro.cs:118-119).
- ⚠️ Mỗi user/loại quân/phe chỉ intro một lần; hàng chờ tối đa 2, quá thì bỏ (HeroIntro.cs:25,92-93). GDD không có quy tắc này.
- ✅ Intro riêng từng tướng (8 clip), mỗi user/tướng/phe một lần.
- ❓ Nhiều tướng cùng lúc: GDD bỏ dở câu; code xếp hàng tuần tự theo phe, hai phe song song.

---

## 13. Bảng xếp hạng (điểm)
Code: `RP/RecoilMatchScoring.cs` (+ `RecoilMatchScoringValidation.cs` đã chạy đạt).
- ✅ Điểm cố định = xu × 50.
- ✅ Bể điểm = 20% tổng điểm cố định; chia top 5 toàn trận 40/25/15/12/8 %.
- ✅ Mỗi người bắt đầu 1 điểm thắng; đội thua giữ 60% (làm tròn); 40% mất đi chia top 5 đội thắng 30/24/20/16/10 % (làm tròn).
- ✅ Thưởng chuỗi thắng = điểm cố định × tỉ lệ theo chuỗi **sau** trận (5% → 60%, đúng 9 mốc).
- ✅ Nhiều trận cùng lúc tính riêng, cộng dồn delta.
- ❌ "Đặt tất cả điểm thắng vào tổng điểm chiến thắng của đội để hiện thông số": không có tổng chuỗi thắng của đội trên HUD.
- ✅ Lưu chuỗi thắng theo user / theo tuần (thêm 2026-11-12, `RP/RecoilRankSync.cs`): đọc chuỗi tuần từ rank server khi người xem vào trận (`RecoilMatchScoring.SetStartingStreak`), gửi kết quả cả trận sau khi chốt (matchPoint = điểm xếp hạng, killCount = chuỗi mới − chuỗi server, âm khi thua), chia gói 200 người, thử lại 3 lần. Người giả lập `sim-` không gửi (bật `rankServerSyncSimulatedUsers` để test). Reset tuần: server. Chưa kiểm chứng với server thật: cộng dồn hay ghi đè, có nhận killCount âm không.

### 13.4 BXH sau trận
- ✅ Tên tô màu đội (một sắc xanh/đỏ theo hash tên, `RP/RecoilUserNameColors.cs:13-43`).
- ✅ (2026-11-12) Thẻ top 3: "điểm cố định (+thưởng bể + chuỗi)" đếm lên, "Chuỗi thắng N (±Δ)" xanh/đỏ, "Hạng N (▲/▼ bậc)" xanh/đỏ (`DrawEndgameCardDetails`). Dòng BXH hai đội: cùng ba số trên một dòng, phần thưởng bể đếm lên khi xu bay tới.
- ✅ Hạng: hạng tuần·điểm trên rank server lúc người xem vào trận (`StartingRank`) và sau khi gửi kết quả (`EndingRank`, đọc lại bảng tuần; tối đa 3 người ngoài bảng được hỏi riêng). Không có mạng / server chưa trả → "—".
- ❌ 13.5 Video Top BXH.
- Bảng xếp hạng trong trận (nút góc dưới-trái): đọc rank server, bảng Tuần·Điểm mặc định (`RP/RecoilLeaderboardButton.cs`, `RP/RecoilRankServer.cs`).

---

## 14. VFX chiến đấu
| Nhóm | GDD | Game | |
|---|---|---|---|
| Cận chiến | slash trail, spark, hit flash, bụi, knockback | Chỉ animation tấn công + âm thanh (ABD:2398) | ❌ |
| Cận chiến | death animation / ragdoll | Animator "Death" + clip VAT Death; xác lún xuống, không ragdoll (`RP/RecoilVatAnimation.cs:63-84`) | ✅ một phần |
| Cung | kéo cung → bắn → tên bay thật → trúng | Đạn rời tay đúng lúc thả (`ProjectileReleaseDelay`), bay parabol `ArcHeight = max(1, d×0.18)`, sát thương lúc trúng | ✅ |
| Cung | VFX rõ đẹp | Không có VFX khi tên trúng (`RP/RecoilSiegeImpactEffects.cs:22` thoát nếu không phải đá) | ❌ |
| Máy bắn đá | parabol, explosion, bụi + đá vỡ, knockback, camera shake nhẹ | Parabol ✅; nổ "Meteor hit" (khói/tia/sóng/lửa) ✅; có khói nhưng không có mảnh đá ⚠️; knockback cao 3/xa 3/0.8 s ✅ (`RP/RecoilBattleEffects.cs:16-52`); camera shake chỉ ở endgame ❌ | ⚠️ |
| Pháp sư/Tướng | projectile, AoE, lightning/fire/meteor/shockwave | Quân sư: 3 cầu phép, vệt sáng, vòng phép, bùng lửa, sóng xung kích ✅; tướng: vùng tròn/nón/hộp, hút/đẩy/hất ✅; lightning/meteor riêng từng tướng ❓ (phụ thuộc prefab) | ✅ |
| Tướng | skill nổi bật hơn đòn thường | Ulti có cooldown, hiệu ứng riêng, knock-up | ✅ |

---

## 15. Điều kiện kết thúc
- ✅ Máu thành ≤ 0 → phe đó thua ngay (ABD:2405-2408, 3115-3118).
- ✅ (gián tiếp) Hết giờ so máu thành tuyệt đối; hai thành cùng máu tối đa nên tương đương so % (`RecoilMatchTimer.cs:52-53`).
- ❌ Hòa → so tổng máu tháp (không có tháp).
- ⚠️ Hòa tiếp → so **số** quân còn sống (kể cả tướng), không phải tổng sức mạnh; hòa hoàn toàn thì Xanh thắng (`RecoilMatchTimer.cs:54-64`).
- ✅ Khóa quân mới, dừng AI, hiện phe thắng (ABD:4068-4079; `RecoilEndgamePresentation.cs:288-296`).
- ⚠️ Cinematic chỉ quay thành thua (thiên thạch, nổ, rung), không quay thành thắng (`RecoilEndgamePresentation.cs:175-196`).

---

## 16. UX/UI
### 16.1 Trước trận — xem Bước 1.
### 16.2 HUD (mặc định "vitals HUD", TAB đổi sang HUD debug)
- ✅ Máu thành đỏ trái, xanh phải (ABD:5932-5953). ⚠️ Hiện số (N0) chứ không phải % (ABD:6210-6222).
- ✅ Đồng hồ giữa trên. ❌ Không có nhãn "BATTLE TIME"; không giới hạn giờ thì ẩn đồng hồ (`RecoilMatchTimer.cs:72`).
- ✅ "BỂ ĐIỂM" ở giữa (ABD:5975-5980).
- ⚠️ Top 1-3 mỗi phe xếp cột dọc sát mép, không phải hàng ngang dưới thanh máu (ABD:5994-6012).
- ✅ Nút BXH góc dưới-trái.
### 16.3 Last Stand — ✅ (xem mục 11).
### 16.4 Kết thúc
- ❌ Video chiến thắng (VideoPlayer chỉ dùng cho intro).
- ✅ BXH trận (hai cột đỏ/xanh, tối đa 100 người/bên, hoạt ảnh xu).
- ✅ Nút chơi lại: RETRY (tại chỗ) và HOME (`RP/RecoilEndgameButtons.cs:13-52`).

---

## 17. Luồng trạng thái
GDD: WAITING → PREPARATION → (L) COUNTDOWN → BATTLE → LAST STAND / SIEGE → END.
Game: Sảnh (đồng hồ + Start) → load / xếp quân đầu game → chuẩn bị (quà, vào hàng) → L → 3-2-1 → BATTLE → END.
Thiếu: bước K, tháp. (LAST STAND đã thêm 2026-11-12.)

---

## Điểm GDD cần chốt lại trước khi sửa
1. "Đội 1 / Đội 2" là phe Xanh hay Đỏ (quyết định chiều sửa bảng tướng).
2. Follow cho gì? (ô trống trong bảng quà; mục 7 gợi ý Dân binh).
3. Combo x10 quà: ra 10× số quân **và** tính 10 vào tiến độ tướng, hay chỉ một trong hai?
4. Dân binh "bị khắc chế bởi tất cả": bao nhiêu %?
5. Sơ đồ đội hình: Giáo ở tuyến đầu (chữ) hay sau lõi Bộ+Voi (hình)?
6. Thành bị đánh ngay khi giao tranh (GDD) hay chỉ khi phe thủ hết lính (hiện tại)? Last Stand chỉ có nghĩa với luật GDD.
7. "Tổng sức mạnh quân còn sống" khi hòa: tính theo công thức nào (HP? HP×Damage? xu?).
