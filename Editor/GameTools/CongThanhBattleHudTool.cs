using System;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    /// <summary>
    /// Places the battle screen's match timer and score pool ("BỂ ĐIỂM" + the points): drag to move, drag a corner to
    /// scale, align, or type the transform. While playing, every change shows in the running battle at once; "Lưu"
    /// (outside Play Mode) writes it to the scene.
    /// </summary>
    public sealed class CongThanhBattleHudTool : EditorWindow
    {
        private const string BattleScenePath = "Assets/Scenes/new.unity";
        private const string FieldName = "battleHudLayout";
        private const float PreviewHeight = 260f; // design px of the top strip shown
        private const float SnapDistance = 5f;     // design px
        private const int ControlHash = 0x48554442;

        private static readonly BattleHudPieceId[] Ids = { BattleHudPieceId.Timer, BattleHudPieceId.PoolTitle, BattleHudPieceId.PoolScore };
        private static readonly string[] Names = { "Thời gian", "Chữ BỂ ĐIỂM", "Số điểm" };
        private static readonly string[] Fields = { "timer", "poolTitle", "poolScore" };
        private static readonly string[] Samples = { "09:58", "BỂ ĐIỂM", "1,234,567" };

        private enum DragKind { None, Move, Corner }

        // Serialized so edits survive entering / leaving Play Mode (the window outlives the domain reload).
        [SerializeField] private BattleHudPiece[] _pieces;
        [SerializeField] private bool _dirty;
        [SerializeField] private int _selected;
        [SerializeField] private bool _moveGroup;
        [SerializeField] private int _alignTarget = 1;

        private RecoilAutoBattleDemo _demo;
        private string _status;
        private Vector2 _scroll;
        private DragKind _drag;
        private Vector2 _dragStartMouse, _dragStartCenter;
        private BattleHudPiece[] _dragStartPieces;
        private float _dragStartScale, _dragStartRadius;
        private bool _snapX, _snapY;
        private GUIStyle _textStyle;

        [MenuItem("Game Tools/Công Thành/Battle/Chỉnh UI thời gian + bể điểm")]
        private static void Open()
        {
            var window = GetWindow<CongThanhBattleHudTool>("UI thời gian + bể điểm");
            window.minSize = new Vector2(480f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            if (_pieces == null || _pieces.Length != Ids.Length || !_dirty) Load();
            else _demo = FindDemo();
            EditorSceneManager.activeSceneChangedInEditMode += OnSceneChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorSceneManager.activeSceneChangedInEditMode -= OnSceneChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnSceneChanged(Scene previous, Scene next)
        {
            if (!_dirty) Load();
            else _demo = FindDemo();
            Repaint();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            _demo = FindDemo();
            // Entering Play: show the edits being worked on in the running battle straight away.
            if (change == PlayModeStateChange.EnteredPlayMode && _dirty) PushLive();
            Repaint();
        }

        // ---------------------------------------------------------------- GUI

        private void OnGUI()
        {
            if (_demo == null) _demo = FindDemo();
            HandleKeys();
            EditorGUILayout.HelpBox(
                "Kéo để dời, kéo góc vàng để scale; mũi tên dời 1 px (Shift: 10). Đường xanh = đang thẳng hàng. " +
                "Đang Play thì chỉnh tới đâu trận đổi tới đó; thoát Play rồi bấm Lưu.",
                MessageType.Info);

            if (_demo == null || _pieces == null)
            {
                EditorGUILayout.HelpBox("Chưa có RecoilAutoBattleDemo trong scene đang mở.", MessageType.Warning);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button("Mở " + BattleScenePath))
                        RunSafely(() =>
                        {
                            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                            EditorSceneManager.OpenScene(BattleScenePath, OpenSceneMode.Single);
                            Load();
                        });
                DrawStatus();
                return;
            }

            EditorGUILayout.LabelField("Đang sửa", _demo.gameObject.scene.name +
                (EditorApplication.isPlaying ? "  (đang Play: xem trực tiếp trong trận)" : string.Empty) +
                (_dirty ? "   (chưa lưu)" : string.Empty), EditorStyles.miniBoldLabel);

            var width = position.width - 24f;
            var area = GUILayoutUtility.GetRect(0f, 0f, GUILayout.Width(width),
                GUILayout.Height(width * PreviewHeight / RecoilBattleHudLayout.DesignWidth));
            DrawAndHandlePreview(area);

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                EditorGUI.BeginChangeCheck();
                _selected = GUILayout.Toolbar(Mathf.Clamp(_selected, 0, Ids.Length - 1), Names);
                _moveGroup = EditorGUILayout.ToggleLeft("Kéo cả cụm (dời cả ba cùng lúc)", _moveGroup);
                if (EditorGUI.EndChangeCheck()) _drag = DragKind.None;
                DrawSelectedFields();
                _scroll = scroll.scrollPosition;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button("Lưu vào scene", GUILayout.Height(30f)))
                        EditorApplication.delayCall += () => RunSafely(Save);
                if (GUILayout.Button("Tải lại", GUILayout.Width(90f), GUILayout.Height(30f)))
                    Load();
            }
            DrawStatus();
        }

        private void DrawSelectedFields()
        {
            var piece = _pieces[_selected];
            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            var position = EditorGUILayout.Vector2Field("Vị trí (tâm; x từ trái khung 1920, y từ mép trên)", piece.position);
            var scale = EditorGUILayout.Slider("Scale", piece.scale, .2f, 4f);
            if (EditorGUI.EndChangeCheck())
            {
                if (_moveGroup) MoveAll(position - piece.position);
                Commit(_selected, new BattleHudPiece(_moveGroup ? _pieces[_selected].position : position, scale));
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Căn chỉnh", EditorStyles.boldLabel);
            if (GUILayout.Button(_moveGroup ? "Cả cụm vào giữa ngang màn hình" : "Giữa ngang màn hình"))
            {
                var dx = RecoilBattleHudLayout.DesignWidth * .5f - piece.position.x;
                if (_moveGroup) MoveAll(new Vector2(dx, 0f));
                else Commit(_selected, new BattleHudPiece(new Vector2(piece.position.x + dx, piece.position.y), piece.scale));
            }
            var others = Enumerable.Range(0, Ids.Length).Where(i => i != _selected).ToArray();
            var choice = Mathf.Max(0, Array.IndexOf(others, _alignTarget));
            choice = EditorGUILayout.Popup("So với", choice, others.Select(i => Names[i]).ToArray());
            _alignTarget = others[choice];
            var target = _pieces[_alignTarget];
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cùng cột (x)"))
                    Commit(_selected, new BattleHudPiece(new Vector2(target.position.x, piece.position.y), piece.scale));
                if (GUILayout.Button("Cùng hàng (y)"))
                    Commit(_selected, new BattleHudPiece(new Vector2(piece.position.x, target.position.y), piece.scale));
                if (GUILayout.Button("Đặt ngay trên"))
                    Commit(_selected, new BattleHudPiece(new Vector2(target.position.x,
                        target.position.y - (HalfHeight(_alignTarget, target) + HalfHeight(_selected, piece))), piece.scale));
                if (GUILayout.Button("Đặt ngay dưới"))
                    Commit(_selected, new BattleHudPiece(new Vector2(target.position.x,
                        target.position.y + HalfHeight(_alignTarget, target) + HalfHeight(_selected, piece)), piece.scale));
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var defaults = new RecoilBattleHudLayout();
                if (GUILayout.Button("Về mặc định (mục này)"))
                    Commit(_selected, defaults.Get(Ids[_selected]));
                if (GUILayout.Button("Về mặc định tất cả"))
                    for (var i = 0; i < Ids.Length; i++) Commit(i, defaults.Get(Ids[i]));
            }
        }

        private static float HalfHeight(int index, BattleHudPiece piece) =>
            RecoilBattleHudLayout.BoxOf(Ids[index]).y * Mathf.Max(.05f, piece.scale) * .5f;

        private void MoveAll(Vector2 delta)
        {
            for (var i = 0; i < _pieces.Length; i++)
                Commit(i, new BattleHudPiece(_pieces[i].position + delta, _pieces[i].scale));
        }

        private void Commit(int index, BattleHudPiece value)
        {
            value.scale = Mathf.Clamp(value.scale, .05f, 10f);
            _pieces[index] = value;
            _dirty = true;
            PushLive();
        }

        // ---------------------------------------------------------------- preview

        /// <summary>A piece's box in design pixels (top-left based), as the battle HUD draws it at full size.</summary>
        private Rect DesignRect(int index)
        {
            var piece = _pieces[index];
            var size = RecoilBattleHudLayout.BoxOf(Ids[index]) * Mathf.Max(.05f, piece.scale);
            return new Rect(piece.position - size * .5f, size);
        }

        private void DrawAndHandlePreview(Rect area)
        {
            var fit = area.width / RecoilBattleHudLayout.DesignWidth;
            Vector2 ToGui(Vector2 design) => area.position + design * fit;
            Vector2 ToDesign(Vector2 gui) => (gui - area.position) / fit;
            Rect ToGuiRect(Rect design) => new(ToGui(design.position), design.size * fit);
            var controlId = GUIUtility.GetControlID(ControlHash, FocusType.Passive, area);
            var e = Event.current;

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(area, new Color(.16f, .2f, .17f));
                GUI.BeginClip(area);
                DrawHealthBarGuides(fit);
                for (var i = 0; i < Ids.Length; i++)
                {
                    var rect = ToGuiRect(DesignRect(i));
                    DrawSampleText(new Rect(rect.position - area.position, rect.size), i, fit);
                }
                GUI.EndClip();
                DrawGuides(area, ToGui);
                for (var i = 0; i < Ids.Length; i++)
                    DrawOutline(ToGuiRect(DesignRect(i)), i == _selected ? Color.yellow :
                        _moveGroup ? new Color(1f, .9f, .4f, .5f) : new Color(1f, 1f, 1f, .25f), i == _selected);
            }

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !area.Contains(e.mousePosition)) break;
                    var design = ToDesign(e.mousePosition);
                    _drag = DragKind.None;
                    var selectedGui = ToGuiRect(DesignRect(_selected));
                    if (Corners(selectedGui).Any(c => Vector2.Distance(c, e.mousePosition) <= 9f))
                    {
                        _drag = DragKind.Corner;
                        _dragStartScale = _pieces[_selected].scale;
                        _dragStartRadius = Mathf.Max(1f, Vector2.Distance(design, _pieces[_selected].position));
                    }
                    else
                    {
                        var hit = -1;
                        for (var i = Ids.Length - 1; i >= 0 && hit < 0; i--)
                            if (DesignRect(i).Contains(design)) hit = i;
                        if (hit < 0) break;
                        _selected = hit;
                        _drag = DragKind.Move;
                        _dragStartCenter = _pieces[hit].position;
                        _dragStartPieces = (BattleHudPiece[])_pieces.Clone();
                    }
                    _dragStartMouse = design;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId || _drag == DragKind.None) break;
                    var point = ToDesign(e.mousePosition);
                    var current = _pieces[_selected];
                    if (_drag == DragKind.Corner)
                    {
                        current.scale = _dragStartScale * Vector2.Distance(point, current.position) / _dragStartRadius;
                        Commit(_selected, current);
                    }
                    else
                    {
                        var moved = SnapPosition(_dragStartCenter + (point - _dragStartMouse), fit);
                        var delta = moved - _dragStartCenter;
                        if (_moveGroup)
                            for (var i = 0; i < _pieces.Length; i++)
                                Commit(i, new BattleHudPiece(_dragStartPieces[i].position + delta, _pieces[i].scale));
                        else
                            Commit(_selected, new BattleHudPiece(moved, current.scale));
                    }
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    _drag = DragKind.None;
                    _snapX = _snapY = false;
                    e.Use();
                    Repaint();
                    break;
            }
        }

        private Vector2 SnapPosition(Vector2 position, float fit)
        {
            var threshold = SnapDistance / Mathf.Max(fit, .0001f);
            _snapX = _snapY = false;
            var free = Enumerable.Range(0, Ids.Length).Where(i => i != _selected && !_moveGroup).ToArray();
            var xs = free.Select(i => _pieces[i].position.x).Append(RecoilBattleHudLayout.DesignWidth * .5f);
            var bestX = xs.OrderBy(x => Mathf.Abs(x - position.x)).First();
            if (Mathf.Abs(bestX - position.x) <= threshold)
            {
                position.x = bestX;
                _snapX = true;
            }
            if (free.Length > 0)
            {
                var bestY = free.Select(i => _pieces[i].position.y).OrderBy(y => Mathf.Abs(y - position.y)).First();
                if (Mathf.Abs(bestY - position.y) <= threshold)
                {
                    position.y = bestY;
                    _snapY = true;
                }
            }
            return position;
        }

        /// <summary>The castle health bars, where the battle HUD puts them at full size, for reference.</summary>
        private static void DrawHealthBarGuides(float fit)
        {
            const float width = 1810f, center = 250f, gap = 18f, top = 25f;
            var left = (RecoilBattleHudLayout.DesignWidth - width) * .5f;
            var barWidth = (width - center) * .5f - 10f - gap;
            var leftBar = new Rect(left + 10f, top + 30f, barWidth, 66f);
            var rightBar = new Rect(RecoilBattleHudLayout.DesignWidth * .5f + center * .5f + gap, top + 30f, barWidth, 66f);
            EditorGUI.DrawRect(new Rect(leftBar.position * fit, leftBar.size * fit), new Color(.55f, .12f, .1f, .55f));
            EditorGUI.DrawRect(new Rect(rightBar.position * fit, rightBar.size * fit), new Color(.12f, .22f, .6f, .55f));
        }

        private void DrawSampleText(Rect rect, int index, float fit)
        {
            _textStyle ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            _textStyle.fontSize = Mathf.Max(1, Mathf.RoundToInt(RecoilBattleHudLayout.FontOf(Ids[index]) *
                                                                 Mathf.Max(.05f, _pieces[index].scale) * fit));
            var shadow = Mathf.Max(1f, 2f * _pieces[index].scale * fit);
            _textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + shadow, rect.y + shadow, rect.width, rect.height), Samples[index], _textStyle);
            _textStyle.normal.textColor = index == (int)BattleHudPieceId.PoolScore ? new Color(1f, .8f, .3f) : Color.white;
            GUI.Label(rect, Samples[index], _textStyle);
        }

        private static Vector2[] Corners(Rect rect) => new[]
        {
            new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
            new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax)
        };

        private static void DrawOutline(Rect rect, Color color, bool handles)
        {
            var previous = Handles.color;
            Handles.color = color;
            var c = Corners(rect);
            Handles.DrawAAPolyLine(handles ? 2f : 1f, c[0], c[1], c[2], c[3], c[0]);
            Handles.color = previous;
            if (!handles) return;
            var old = GUI.color;
            GUI.color = color;
            foreach (var corner in c)
                GUI.DrawTexture(new Rect(corner.x - 5f, corner.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void DrawGuides(Rect area, Func<Vector2, Vector2> toGui)
        {
            if (_drag != DragKind.Move) return;
            var previous = Handles.color;
            Handles.color = new Color(.3f, .9f, 1f, .9f);
            var piece = _pieces[_selected];
            if (_snapX)
            {
                var x = toGui(new Vector2(piece.position.x, 0f)).x;
                Handles.DrawAAPolyLine(1f, new Vector3(x, area.yMin), new Vector3(x, area.yMax));
            }
            if (_snapY)
            {
                var y = toGui(new Vector2(0f, piece.position.y)).y;
                Handles.DrawAAPolyLine(1f, new Vector3(area.xMin, y), new Vector3(area.xMax, y));
            }
            Handles.color = previous;
        }

        private void HandleKeys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || _pieces == null) return;
            var step = e.shift ? 10f : 1f;
            var delta = e.keyCode switch
            {
                KeyCode.LeftArrow => new Vector2(-step, 0f),
                KeyCode.RightArrow => new Vector2(step, 0f),
                KeyCode.UpArrow => new Vector2(0f, -step),
                KeyCode.DownArrow => new Vector2(0f, step),
                _ => Vector2.zero,
            };
            if (delta == Vector2.zero) return;
            if (_moveGroup) MoveAll(delta);
            else Commit(_selected, new BattleHudPiece(_pieces[_selected].position + delta, _pieces[_selected].scale));
            e.Use();
            Repaint();
        }

        // ---------------------------------------------------------------- load / save / live

        private static RecoilAutoBattleDemo FindDemo()
        {
            var active = SceneManager.GetActiveScene();
            var all = Resources.FindObjectsOfTypeAll<RecoilAutoBattleDemo>()
                .Where(demo => !EditorUtility.IsPersistent(demo) && demo.gameObject.scene.IsValid()).ToArray();
            return all.FirstOrDefault(demo => demo.gameObject.scene == active) ?? all.FirstOrDefault();
        }

        private void Load()
        {
            _status = null;
            _dirty = false;
            _drag = DragKind.None;
            _demo = FindDemo();
            _pieces = new BattleHudPiece[Ids.Length];
            var defaults = new RecoilBattleHudLayout();
            for (var i = 0; i < Ids.Length; i++) _pieces[i] = defaults.Get(Ids[i]);
            if (_demo == null) return;
            var layout = new SerializedObject(_demo).FindProperty(FieldName);
            if (layout == null) return;
            for (var i = 0; i < Ids.Length; i++)
            {
                var piece = layout.FindPropertyRelative(Fields[i]);
                _pieces[i] = new BattleHudPiece(piece.FindPropertyRelative("position").vector2Value,
                    piece.FindPropertyRelative("scale").floatValue);
            }
        }

        private void WriteTo(RecoilAutoBattleDemo demo, bool withUndo)
        {
            var serialized = new SerializedObject(demo);
            var layout = serialized.FindProperty(FieldName);
            if (layout == null)
                throw new InvalidOperationException("RecoilAutoBattleDemo chưa có ô \"" + FieldName + "\": chờ Unity biên dịch xong.");
            for (var i = 0; i < Ids.Length; i++)
            {
                var piece = layout.FindPropertyRelative(Fields[i]);
                piece.FindPropertyRelative("position").vector2Value = _pieces[i].position;
                piece.FindPropertyRelative("scale").floatValue = _pieces[i].scale;
            }
            if (withUndo) serialized.ApplyModifiedProperties();
            else serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>In Play Mode: the running battle shows the edit right away (lost on leaving Play unless saved).</summary>
        private void PushLive()
        {
            if (!EditorApplication.isPlaying || _demo == null || _pieces == null) return;
            try { WriteTo(_demo, false); }
            catch (Exception exception) { _status = exception.Message; }
        }

        private void Save()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Thoát Play Mode trước.");
            _demo = FindDemo();
            if (_demo == null) throw new InvalidOperationException("Chưa có RecoilAutoBattleDemo trong scene đang mở.");
            Undo.RecordObject(_demo, "Chỉnh UI thời gian + bể điểm");
            WriteTo(_demo, true);
            var scene = _demo.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Không lưu được " + scene.path);
            _dirty = false;
            _status = "Đã lưu vào " + scene.path + ".";
        }

        private void RunSafely(Action action)
        {
            try { action(); }
            catch (Exception exception)
            {
                _status = exception.Message;
                Debug.LogException(exception);
            }
            Repaint();
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
        }
    }
}
