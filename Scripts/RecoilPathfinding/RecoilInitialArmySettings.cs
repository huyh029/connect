using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [Serializable]
        private sealed class InitialArmyBlock
        {
            public int X, Y;
            public int Type = -1;
            public int Count = 5;
        }

        [Serializable]
        private sealed class InitialArmyLayout
        {
            public bool Enabled;
            public List<InitialArmyBlock> Blocks = new();
        }

        private InitialArmyLayout _initialArmy;
        private bool _initialArmySettingsOpen, _initialArmyPickingType;
        private int _initialArmySelected = -1;
        private Vector2 _initialArmyGridScroll, _initialArmyPropertyScroll;
        private float _initialArmyZoom = 1f;
        private string _initialArmyCountInput = "5", _initialArmyStatus = "";
        // Font Awesome Free Solid, in Blue Units By Type order.
        private static readonly string[] InitialArmyIconNames =
        {
            "bullseye", "khanda", "shield-halved", "chess-knight",
            "bomb", "book-open", "paw", "shield"
        };
        private Texture2D[] _initialArmyIcons;

        private Texture2D InitialArmyIcon(int type)
        {
            if (type < 0 || type >= InitialArmyIconNames.Length) return null;
            if (_initialArmyIcons == null)
            {
                _initialArmyIcons = new Texture2D[InitialArmyIconNames.Length];
                for (var i = 0; i < _initialArmyIcons.Length; i++)
                    _initialArmyIcons[i] = Resources.Load<Texture2D>("InitialArmyIcons/" + InitialArmyIconNames[i]);
            }
            return _initialArmyIcons[type];
        }

        private GUIContent InitialArmyUnitContent(int type) =>
            new GUIContent("  " + CharacterTypeLabels[type], InitialArmyIcon(type));
        private string InitialArmyPath => Path.Combine(Application.streamingAssetsPath, "recoil_initial_army.csv");

        private static string WriteInitialArmyCsv(InitialArmyLayout layout)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("# enabled=" + (layout.Enabled ? "1" : "0"));
            csv.AppendLine("Type,X,Y,Count");
            foreach (var block in layout.Blocks)
                if (block.Type >= 0)
                    csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3}", block.Type, Mathf.Abs(block.X), block.Y, block.Count));
            return csv.ToString();
        }

        private static InitialArmyLayout ReadInitialArmyCsv(string text)
        {
            var layout = new InitialArmyLayout { Enabled = true };
            var cells = new HashSet<Vector2Int>();
            var header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("# enabled="))
                {
                    var value = line.Substring(10);
                    if (value != "0" && value != "1") throw new InvalidDataException("enabled phải là 0 hoặc 1.");
                    layout.Enabled = value == "1";
                    continue;
                }
                if (line.StartsWith("#")) continue;
                if (!header)
                {
                    if (line != "Type,X,Y,Count") throw new InvalidDataException("CSV cần cột Type,X,Y,Count.");
                    header = true;
                    continue;
                }
                var parts = line.Split(',');
                if (parts.Length != 4) throw new InvalidDataException("Dòng CSV cần đủ 4 cột: " + line);
                var numbers = new int[4];
                for (var i = 0; i < 4; i++)
                    if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                        throw new InvalidDataException("Giá trị CSV không hợp lệ: " + line);
                var block = new InitialArmyBlock { Type = numbers[0], X = numbers[1], Y = numbers[2], Count = numbers[3] };
                if (block.Type < 0 || block.Type >= CharacterTypeCount || block.X < 0 || block.X > 6 ||
                    block.Y < 0 || block.Y > 12 || block.Count < 1 || block.Count > 5000 ||
                    !cells.Add(new Vector2Int(block.X, block.Y)))
                    throw new InvalidDataException("Loại quân, điểm lưới hoặc số lượng không hợp lệ/trùng: " + line);
                layout.Blocks.Add(block);
            }
            if (!header) throw new InvalidDataException("Thiếu tiêu đề CSV đội hình.");
            return layout;
        }
        private static InitialArmyLayout CreateDefaultInitialArmyLayout()
        {
            var layout = new InitialArmyLayout { Enabled = true };
            // Store one half; noncentral blocks are reflected across the longitudinal axis.
            void Add(int type, int x, int y) => layout.Blocks.Add(
                new InitialArmyBlock { Type = type, X = x, Y = y, Count = 5 });
            Add(6, 0, 0); // Elephants at the center of the front line.
            Add(2, 2, 0); // Spearmen protect the center.
            Add(3, 4, 0); // Cavalry on the outer front flanks.
            Add(1, 1, 1); // Militia and infantry behind the front.
            Add(7, 3, 1);
            foreach (var x in new[] { 0, 2, 4 }) Add(0, x, 2); // Archer line.
            foreach (var x in new[] { 0, 2, 4 }) Add(5, x, 3); // Mage line.
            foreach (var x in new[] { 0, 2 }) Add(4, x, 4); // Siege at the rear.
            return layout;
        }
        private void EnsureInitialArmyLayout()
        {
            if (_initialArmy != null) return;
            _initialArmy = CreateDefaultInitialArmyLayout();
            try
            {
                if (File.Exists(InitialArmyPath))
                    _initialArmy = ReadInitialArmyCsv(File.ReadAllText(InitialArmyPath));
                _initialArmy.Blocks ??= new List<InitialArmyBlock>();
                _initialArmy.Blocks.RemoveAll(b => b == null || b.X < -6 || b.X > 6 || b.Y < 0 || b.Y > 12 ||
                    b.Type < 0 || b.Type >= CharacterTypeCount || b.Count < 1 || b.Count > 5000);
                if (_initialArmy.Blocks.Count == 0)
                {
                    var enabled = _initialArmy.Enabled;
                    _initialArmy = CreateDefaultInitialArmyLayout();
                    _initialArmy.Enabled = enabled;
                }
                var cells = new HashSet<Vector2Int>();
                var duplicates = _initialArmy.Blocks.RemoveAll(b =>
                {
                    b.X = Mathf.Abs(b.X);
                    return !cells.Add(new Vector2Int(b.X, b.Y));
                });
                if (duplicates > 0)
                    _initialArmyStatus = "Đã gộp điểm trái/phải từ cấu hình cũ; giữ cụm đầu tiên ở mỗi cặp.";
            }
            catch (Exception e) { _initialArmyStatus = "Không đọc được đội hình: " + e.Message; }
        }

        private int CustomInitialTypeCount(int type)
        {
            EnsureInitialArmyLayout();
            if (!_initialArmy.Enabled) return -1;
            var count = 0;
            foreach (var block in _initialArmy.Blocks)
                if (block.Type == type) count += block.Count * (block.X == 0 ? 1 : 2);
            return count;
        }

        private void CancelUnassignedInitialArmyBlocks()
        {
            if (_initialArmy == null) return;
            var selected = _initialArmySelected >= 0 && _initialArmySelected < _initialArmy.Blocks.Count
                ? _initialArmy.Blocks[_initialArmySelected] : null;
            _initialArmy.Blocks.RemoveAll(b => b.Type < 0);
            _initialArmySelected = selected != null ? _initialArmy.Blocks.IndexOf(selected) : -1;
            if (_initialArmySelected < 0) _initialArmyPickingType = false;
        }
        private void SelectInitialArmyBlock(int index)
        {
            var selected = _initialArmy.Blocks[index];
            _initialArmy.Blocks.RemoveAll(b => b.Type < 0 && b != selected);
            _initialArmySelected = _initialArmy.Blocks.IndexOf(selected);
            index = _initialArmySelected;
            _initialArmyCountInput = _initialArmy.Blocks[index].Count.ToString();
            _initialArmyPickingType = false;
            GUI.FocusControl(null);
        }

        private void DrawInitialArmySettings()
        {
            EnsureInitialArmyLayout();
            var left = SetupNavigationWidth + 28f;
            var area = new Rect(left, 60f, Screen.width - left - 20f, Screen.height - 80f);
            GUI.Box(area, GUIContent.none);
            DrawSettingsTitle(new Rect(area.x + 14, area.y + 6, area.width - 28, 30), "QUÂN ĐẦU GAME");
            _initialArmy.Enabled = GUI.Toggle(new Rect(area.x + 12, area.y + 42, 250, 28),
                _initialArmy.Enabled, "Dùng đội hình tùy chỉnh");
            GUI.Label(new Rect(area.x + 12, area.y + 74, area.width - 24, 26),
                "Vị trí tương đối • Tự cân đối theo số quân • Đối xứng trái/phải");
            var propertyWidth = Mathf.Min(270f, area.width * .37f);
            var gridView = new Rect(area.x + 12, area.y + 136, area.width - propertyWidth - 36,
                Mathf.Max(80, area.height - 204));
            GUI.Label(new Rect(gridView.x, area.y + 105, 70, 24), "Thu phóng");
            _initialArmyZoom = GUI.HorizontalSlider(new Rect(gridView.x + 82, area.y + 114,
                Mathf.Max(40, gridView.width - 90), 18), _initialArmyZoom, .65f, 1.8f);
            var cell = Mathf.Min(44f, (gridView.width - 18f) / 14f, (gridView.height - 18f) / 14f) * _initialArmyZoom;
            var content = new Rect(0, 0, 14 * cell, 14 * cell);
            _initialArmyGridScroll = GUI.BeginScrollView(gridView, _initialArmyGridScroll, content);
            var mouse = Event.current.mousePosition;
            EnsureHealthFillRoundTexture();
            var centerColor = GUI.color;
            GUI.color = new Color(1f, .8f, .3f, .7f);
            GUI.DrawTexture(new Rect(6.5f * cell, .5f * cell, 1f, 12f * cell), Texture2D.whiteTexture);
            GUI.color = centerColor;
            for (var row = 0; row <= 12; row++)
            for (var col = 0; col <= 12; col++)
            {
                var node = new Vector2((col + .5f) * cell, (row + .5f) * cell);
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, .48f);
                if (col < 12) GUI.DrawTexture(new Rect(node.x, node.y, cell, 1), Texture2D.whiteTexture);
                if (row < 12) GUI.DrawTexture(new Rect(node.x, node.y, 1, cell), Texture2D.whiteTexture);
                var index = _initialArmy.Blocks.FindIndex(b => Mathf.Abs(b.X) == Mathf.Abs(col - 6) && b.Y == row);
                var hit = new Rect(node.x - cell * .45f, node.y - cell * .45f, cell * .9f, cell * .9f);
                var radius = Mathf.Min(index >= 0 ? 7f : 3f, cell * .2f);
                var teamColor = new Color(1f, .65f, .08f);
                GUI.color = index >= 0
                    ? (_initialArmy.Blocks[index].Type < 0 ? Color.yellow : teamColor) : new Color(1f, 1f, 1f, .85f);
                var icon = index >= 0 ? InitialArmyIcon(_initialArmy.Blocks[index].Type) : null;
                if (icon != null)
                {
                    var size = Mathf.Min(28f * _initialArmyZoom, cell * .7f);
                    GUI.DrawTexture(new Rect(node.x - size * .5f, node.y - size * .5f, size, size),
                        icon, ScaleMode.ScaleToFit, true);
                }
                else
                    GUI.DrawTexture(new Rect(node.x - radius, node.y - radius, radius * 2, radius * 2), _healthFillRoundTexture);
                GUI.color = old;
                if (index >= 0 && _initialArmySelected == index)
                    GUI.Box(new Rect(node.x - cell * .42f, node.y - cell * .42f, cell * .84f, cell * .84f), GUIContent.none);
                if (GUI.Button(hit, GUIContent.none, GUIStyle.none))
                {
                    if (index < 0)
                    {
                        _initialArmy.Blocks.Add(new InitialArmyBlock { X = Mathf.Abs(col - 6), Y = row });

                        index = _initialArmy.Blocks.Count - 1;
                    }
                    SelectInitialArmyBlock(index);
                }
            }
            GUI.EndScrollView();


            var property = new Rect(gridView.xMax + 12, gridView.y, propertyWidth, gridView.height);
            GUI.Box(property, GUIContent.none);
            GUILayout.BeginArea(new Rect(property.x + 10, property.y + 8, property.width - 20, property.height - 16));
            _initialArmyPropertyScroll = GUILayout.BeginScrollView(_initialArmyPropertyScroll);
            GUILayout.Label("Thuộc tính", SettingsAttributeLabelStyle);
            if (_initialArmySelected < 0 || _initialArmySelected >= _initialArmy.Blocks.Count)
                GUILayout.Label("Click một điểm trên lưới để thêm quân.");
            else
            {
                var block = _initialArmy.Blocks[_initialArmySelected];
                if (SettingsGUILayoutButton(block.Type < 0 ? new GUIContent("+ Thêm loại quân") : InitialArmyUnitContent(block.Type),
                    GUILayout.Height(36)))
                    _initialArmyPickingType = !_initialArmyPickingType;
                if (_initialArmyPickingType)
                    for (var type = 0; type < CharacterTypeCount; type++)
                        if (SettingsGUILayoutButton(InitialArmyUnitContent(type), GUILayout.Height(30)))
                        { block.Type = type; _initialArmy.Enabled = true; _initialArmyPickingType = false; }
                if (block.Type >= 0)
                {
                    var unitIcon = InitialArmyIcon(block.Type);
                    if (unitIcon != null)
                        GUILayout.Label(unitIcon, GUILayout.Height(64), GUILayout.Width(64));
                    GUILayout.Space(8);
                    GUILayout.Label(block.X == 0 ? "Số quân đầu game" : "Số quân mỗi cụm (2 cụm đối xứng)");
                    _initialArmyCountInput = GUILayout.TextField(_initialArmyCountInput, 5);
                    if (int.TryParse(_initialArmyCountInput, out var count) && count >= 1 && count <= 5000)
                        block.Count = count;
                    else GUILayout.Label("Nhập số từ 1 đến 5000.");
                }
                GUILayout.Space(12);
                if (SettingsGUILayoutButton(new GUIContent("Xóa điểm này"), GUILayout.Height(30)))
                { _initialArmy.Blocks.RemoveAt(_initialArmySelected); _initialArmySelected = -1; }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            if (SettingsButton(new Rect(area.x + 12, area.yMax - 57, 140, 34),
                new GUIContent("Lưu đội hình"), GUI.skin.button))
            {
                try
                {
                    if (_initialArmySelected >= 0 && _initialArmySelected < _initialArmy.Blocks.Count &&
                        _initialArmy.Blocks[_initialArmySelected].Type >= 0 &&
                        (!int.TryParse(_initialArmyCountInput, out var value) || value < 1 || value > 5000))
                        throw new InvalidDataException("Số quân phải từ 1 đến 5000.");
                    if (_initialArmy.Enabled && !_initialArmy.Blocks.Exists(b => b.Type >= 0))
                        throw new InvalidDataException("Hãy thêm ít nhất một loại quân.");
                    var total = 0;
                    foreach (var block in _initialArmy.Blocks)
                        if (block.Type >= 0) total += block.Count * (block.X == 0 ? 1 : 2);
                    if (_initialArmy.Enabled && total > 10000)
                        throw new InvalidDataException("Tối đa 10000 quân đầu game mỗi bên.");
                    Directory.CreateDirectory(Application.streamingAssetsPath);
                    CancelUnassignedInitialArmyBlocks();
                    File.WriteAllText(InitialArmyPath, WriteInitialArmyCsv(_initialArmy));
                    _initialArmyStatus = "Đã lưu. Áp dụng cho trận tiếp theo.";
                }
                catch (Exception e) { _initialArmyStatus = e.Message; }
            }
            if (SettingsButton(new Rect(area.x + 164, area.yMax - 57, 150, 34),
                new GUIContent("Đội hình mặc định"), GUI.skin.button))
            {
                _initialArmy = CreateDefaultInitialArmyLayout();
                _initialArmySelected = -1;
                _initialArmyPickingType = false;
                _initialArmyCountInput = "5";
                _initialArmyStatus = "Đã đặt mặc định. Nhấn Lưu đội hình để giữ lại.";
                GUI.FocusControl(null);
            }
            GUI.Label(new Rect(area.x + 326, area.yMax - 57, Mathf.Max(40, area.width - 338), 50), _initialArmyStatus);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                !property.Contains(Event.current.mousePosition))
                CancelUnassignedInitialArmyBlocks();
        }


        [SerializeField, Min(.15f), Tooltip("Khoảng trống (m) giữa hai lính cạnh nhau trong đội hình tập kết đầu trận; " +
                                          "giữa hai loại lính cách gấp đôi.")]
        private float formationUnitSpacing = 1f;
        [SerializeField, Min(1), Tooltip("Mỗi hàng của đội hình tập kết có khoảng chừng này lính (mỗi loại lính chia " +
                                         "thành các hàng đều nhau, hàng nào cũng trải hết chiều ngang).")]
        private int formationUnitsPerLine = 50;
        [SerializeField, Range(2, 10), Tooltip("Lúc tập kết, tướng đứng ở hàng thứ mấy của đội hình (2 = ngay sau hàng lính " +
                                               "đầu tiên).")]
        private int heroFormationLine = 2;
        [SerializeField, Min(4f), Tooltip("Chiều rộng (m) của đội hình tập kết mỗi phe, ngang so với hướng hai phe tiến vào " +
                                         "nhau. Tự thu lại cho vừa bề rộng map thật (map hiện tại khoảng 85 m). Chiều sâu tự ra theo số lính.")]
        private float formationWidth = 390f;

        private bool TryBuildCustomInitialArmy(out string error, int[] runtimeCounts = null)
        {
            EnsureInitialArmyLayout();
            error = null;
            if (!_initialArmy.Enabled && runtimeCounts == null) return true;
            runtimeCounts = runtimeCounts == null ? PreparedTypeCounts(true) : LimitToTeamCapacity(runtimeCounts);
            _preparedBluePositions.Clear();
            _preparedRedPositions.Clear();
            var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            var axis = BattleAxis();
            var lateral = Vector3.Cross(Vector3.up, axis);
            var rows = BuildFormationRows(runtimeCounts, out var widest);
            if (rows.Count == 0) { error = "Hãy thêm loại quân vào đội hình."; return false; }

            // One wide rectangle, formationWidth across the battle axis, laid out like the formation config.
            // Spots that cannot hold a unit (the castle, water, off the map) are skipped and the lines go on further
            // back, so the army spills around the castle instead of stopping at its wall. A spot is used only when it
            // is free on both sides, so the two armies stay mirror images.
            // Never wider than the ground on both sides of the middle line (the real map is far narrower than the
            // spawn bounds), or the outer blocks would find no place at all.
            var width = Mathf.Max(widest, Mathf.Min(formationWidth, UsableWidthAcross(middle, lateral)));
            var placements = new List<RecoilInitialArmyPlanner.Placement>();
            var projectedBlue = new Dictionary<(float, float), (Vector3 Blue, Vector3 Red)>();
            bool CanPlace(float cross, float depth, float radius)
            {
                var blue = middle + lateral * cross - axis * depth;
                var red = middle + lateral * cross + axis * depth;
                if (!TryProjectInitialArmyPoint(blue, true, radius, out var bp) ||
                    !TryProjectInitialArmyPoint(red, false, radius, out var rp)) return false;
                projectedBlue[(cross, depth)] = (bp, rp);
                return true;
            }
            _waitingPositionTypes.Clear();
            // With heroes on the field, a strip behind the first row stays free for their line.
            var heroGap = WaitingHeroCount() > 0 ? HeroOccupiedRadius * 2f + .5f : 0f;
            if (!RecoilInitialArmyPlanner.Place(rows, width, formationUnitSpacing, formationUnitsPerLine,
                    FormationFrontGap, FormationMaxDepth, CanPlace, placements, heroGap, heroFormationLine - 1,
                    out _heroGapStart))
            {
                _preparedBluePositions.Clear();
                _preparedRedPositions.Clear();
                _preparedPositionsBuilt = false;
                error = "Không đủ vùng đi được cho số quân này. Hãy giảm tổng quân.";
                return false;
            }
            foreach (var placement in placements)
            {
                var spot = projectedBlue[(placement.Cross, placement.Depth)];
                _waitingPositionTypes.Add(placement.Type);
                _preparedBluePositions.Add(spot.Blue);
                _preparedRedPositions.Add(spot.Red);
            }
            SortPreparedPositionsByType();
            _preparedPositionsBuilt = true;
            return true;
        }

        /// <summary>
        /// Soldiers spawn type by type (type 0 first) and take the prepared positions in order, so the positions are
        /// grouped by type; within a type they stay front line first.
        /// </summary>
        private void SortPreparedPositionsByType()
        {
            var order = new List<int>(_waitingPositionTypes.Count);
            for (var i = 0; i < _waitingPositionTypes.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                var byType = _waitingPositionTypes[a].CompareTo(_waitingPositionTypes[b]);
                return byType != 0 ? byType : a.CompareTo(b);
            });
            var types = new List<int>(order.Count);
            var blue = new List<Vector3>(order.Count);
            var red = new List<Vector3>(order.Count);
            foreach (var i in order)
            {
                types.Add(_waitingPositionTypes[i]);
                blue.Add(_preparedBluePositions[i]);
                red.Add(_preparedRedPositions[i]);
            }
            _waitingPositionTypes.Clear(); _waitingPositionTypes.AddRange(types);
            _preparedBluePositions.Clear(); _preparedBluePositions.AddRange(blue);
            _preparedRedPositions.Clear(); _preparedRedPositions.AddRange(red);
        }

        // Where the heroes' strip behind the first row starts (distance behind the middle), and how deep it is; -1 = none.
        private float _heroGapStart = -1f;
        private float HeroGapDepth => HeroOccupiedRadius * 2f + .5f;

        /// <summary>Heroes alive on the bigger side (both sides share one formation layout).</summary>
        private int WaitingHeroCount()
        {
            int Count(List<Soldier> team)
            {
                var count = 0;
                foreach (var soldier in team)
                    if (soldier != null && !soldier.Retired && soldier.HeroId != 0 && IsAlive(soldier)) count++;
                return count;
            }
            return Mathf.Max(Count(_blueTeam), Count(_redTeam));
        }

        // Distance of the opening formation behind the middle, and how far back it may go.
        private const float FormationFrontGap = 8f, FormationMaxDepth = 400f;

        /// <summary>
        /// The formation config's rows (front to back) with its blocks in place, sized for <paramref name="counts"/>
        /// (units per type for one side). <paramref name="widest"/> is the widest unit's diameter.
        /// </summary>
        private List<RecoilInitialArmyPlanner.Row> BuildFormationRows(int[] counts, out float widest)
        {
            static List<RecoilInitialArmyPlanner.ConfigBlock> Convert(List<InitialArmyBlock> blocks)
            {
                var result = new List<RecoilInitialArmyPlanner.ConfigBlock>();
                foreach (var block in blocks)
                    if (block != null && block.Type >= 0 && block.Type < CharacterTypeCount)
                        result.Add(new RecoilInitialArmyPlanner.ConfigBlock
                            { Type = block.Type, X = Mathf.Abs(block.X), Y = block.Y, Count = block.Count });
                return result;
            }
            var radii = new float[CharacterTypeCount];
            widest = 0f;
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                radii[type] = .5f;
                for (var team = 0; team < 2; team++)
                {
                    var template = FindTemplateForType(team == 0, type);
                    if (template != null && template.TryGetComponent<RecoilNavMeshPathAgent>(out var agent))
                        radii[type] = Mathf.Max(radii[type], agent.MovementSettings.BodyRadius);
                }
                if (type < counts.Length && counts[type] > 0) widest = Mathf.Max(widest, radii[type] * 2f);
            }
            return RecoilInitialArmyPlanner.BuildRows(Convert(_initialArmy.Blocks),
                Convert(CreateDefaultInitialArmyLayout().Blocks), counts, radii);
        }

        /// <summary>
        /// Width of the map across the battle axis that is usable on both sides of <paramref name="middle"/>: twice the
        /// shorter distance from the middle to the map's edge along <paramref name="lateral"/>, less a small margin.
        /// Uses the terrain map's real extent when there is one, else the spawn bounds.
        /// </summary>
        private float UsableWidthAcross(Vector3 middle, Vector3 lateral)
        {
            float minX = SpawnMinX, maxX = SpawnMaxX, minZ = SpawnMinZ, maxZ = SpawnMaxZ;
            if (terrainMap != null && terrainMap.HasValidData)
            {
                minX = Mathf.Max(minX, terrainMap.WorldOrigin.x);
                minZ = Mathf.Max(minZ, terrainMap.WorldOrigin.z);
                maxX = Mathf.Min(maxX, terrainMap.WorldOrigin.x + terrainMap.WorldSize.x);
                maxZ = Mathf.Min(maxZ, terrainMap.WorldOrigin.z + terrainMap.WorldSize.y);
            }
            // Distance from the middle to the box edge going +lateral and -lateral.
            static float Reach(float from, float direction, float min, float max) =>
                Mathf.Abs(direction) < 1e-4f ? float.MaxValue
                    : direction > 0f ? (max - from) / direction : (min - from) / direction;
            var forward = Mathf.Min(Reach(middle.x, lateral.x, minX, maxX), Reach(middle.z, lateral.z, minZ, maxZ));
            var backward = Mathf.Min(Reach(middle.x, -lateral.x, minX, maxX), Reach(middle.z, -lateral.z, minZ, maxZ));
            return Mathf.Max(4f, 2f * Mathf.Min(forward, backward) - 2f);
        }

        private bool TryProjectInitialArmyPoint(Vector3 point, bool blue, float radius, out Vector3 projected)
        {
            projected = default;
            if (point.x < SpawnMinX + radius || point.x > SpawnMaxX - radius ||
                point.z < SpawnMinZ + radius || point.z > SpawnMaxZ - radius ||
                !TryGetGroundHeight(point, out var height)) return false;
            point.y = height;
            if (!pathService.TrySamplePosition(point, 2f, GetSpawnAreaMask(blue), out projected)) return false;
            var horizontal = new Vector2(projected.x - point.x, projected.z - point.z);
            return horizontal.sqrMagnitude <= .0001f;
        }
    }
}
