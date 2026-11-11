using System;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    /// <summary>
    /// Places the pieces of the lobby (home) screen of the Recoil battle demo: drag to move, drag a corner to scale,
    /// align / mirror against another piece, or type the transform. Edits stay here until "Lưu".
    /// </summary>
    public sealed class CongThanhLobbyLayoutTool : EditorWindow
    {
        private const string LobbyScenePath = "Assets/Scenes/new.unity";
        private const string ClockPrefabPath = "Assets/_Game/UI/Clock/Đồng hồ.prefab";
        private const float SnapDistance = 6f; // design pixels
        private const int ControlHash = 0x4C4F4259;
        // The first pieces are stored in RecoilLobbyLayout; TimeButton (last) is stored in the clock.
        private const int LayoutCount = 6;

        // Order the pieces are drawn in, back to front (matches OnGUI in RecoilAutoBattleDemo).
        private static readonly LobbyPieceId[] DrawOrder =
        {
            LobbyPieceId.Title, LobbyPieceId.StartButton, LobbyPieceId.SettingsButton,
            LobbyPieceId.LoadingBar, LobbyPieceId.ErrorText, LobbyPieceId.TimeButton, LobbyPieceId.Clock
        };
        private static readonly LobbyPieceId[] AllIds = (LobbyPieceId[])Enum.GetValues(typeof(LobbyPieceId));
        private static readonly string[] Names =
        {
            "Logo tiêu đề", "Nút Bắt đầu", "Nút Settings", "Đồng hồ", "Thanh tải", "Thông báo lỗi", "Nút TIME"
        };
        // Same names as the fields of RecoilAutoBattleDemo.lobbyLayout, in AllIds order.
        private static readonly string[] FieldNames =
        {
            "title", "startButton", "settingsButton", "clock", "loadingBar", "errorText"
        };

        private enum DragKind { None, Move, Corner }

        /// <summary>The TIME button as a layer of the clock: its center in clock space, scale and turn.</summary>
        private sealed class TimeLayer
        {
            public Vector2 position;
            public float scale;
            public float rotation;
            public Vector2 size;
            public Sprite sprite;
        }

        private TimeLayer _time;
        private Vector2 _clockCenter;

        private RecoilAutoBattleDemo _demo;
        private Scene _scene;
        private readonly LobbyPiece[] _pieces = new LobbyPiece[AllIds.Length];
        private Texture2D _background, _titleImage, _startImage, _settingsImage;
        private Sprite _clockFace;
        private float _titleAspect = 3f;
        private LobbyPieceId _selected = LobbyPieceId.Title;
        private LobbyPieceId _alignTarget = LobbyPieceId.StartButton;
        private bool _dirty;
        private string _status;
        private Vector2 _scroll;

        private DragKind _drag;
        private Vector2 _dragStartMouse, _dragStartPosition, _dragStartScale;
        private float _dragStartRadius;
        private bool _snapX, _snapY;

        [MenuItem("Game Tools/Công Thành/Lobby/Chỉnh giao diện sảnh")]
        private static void Open()
        {
            var window = GetWindow<CongThanhLobbyLayoutTool>("Giao diện sảnh");
            window.minSize = new Vector2(480f, 640f);
            window.Show();
        }

        private void OnEnable()
        {
            Load();
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        private void OnDisable()
        {
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (_dirty) return;
            Load();
            Repaint();
        }

        // ---------------------------------------------------------------- GUI

        private void OnGUI()
        {
            HandleKeys();
            EditorGUILayout.HelpBox(
                "Kéo logo, nút hoặc đồng hồ trong khung để dời; kéo góc để scale. Mũi tên bàn phím dời 1 px (Shift: 10 px). " +
                "Đường xanh = đang căn giữa / thẳng hàng. Khung là 1920×1080, luôn căn giữa màn hình.",
                MessageType.Info);

            if (_demo == null)
            {
                EditorGUILayout.HelpBox("Chưa có RecoilAutoBattleDemo trong scene đang mở.", MessageType.Warning);
                if (GUILayout.Button("Mở " + LobbyScenePath))
                    RunSafely(() =>
                    {
                        if (EditorApplication.isPlayingOrWillChangePlaymode)
                            throw new InvalidOperationException("Thoát Play Mode trước.");
                        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                        EditorSceneManager.OpenScene(LobbyScenePath, OpenSceneMode.Single);
                        Load();
                    });
                DrawStatus();
                return;
            }

            EditorGUILayout.LabelField("Đang sửa", _scene.name + (_dirty ? "   (chưa lưu)" : string.Empty),
                EditorStyles.miniBoldLabel);

            var width = position.width - 24f;
            var area = GUILayoutUtility.GetRect(0f, 0f, GUILayout.Width(width), GUILayout.Height(width * 9f / 16f));
            DrawAndHandlePreview(area);

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                DrawPieceChooser();
                DrawSelectedFields();
                _scroll = scroll.scrollPosition;
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Lưu vào new.unity", GUILayout.Height(30f)))
                    EditorApplication.delayCall += () => RunSafely(Save);
                if (GUILayout.Button("Tải lại", GUILayout.Width(90f), GUILayout.Height(30f)))
                    Load();
            }
            DrawStatus();
        }

        private void DrawPieceChooser()
        {
            EditorGUILayout.LabelField("Chọn phần tử", EditorStyles.boldLabel);
            var index = Array.IndexOf(AllIds, _selected);
            var picked = GUILayout.SelectionGrid(index, Names, 3);
            if (picked >= 0 && picked != index)
                Select(AllIds[picked]);
        }

        private void Select(LobbyPieceId id)
        {
            _selected = id;
            if (_alignTarget == _selected) _alignTarget = AllIds.First(other => other != _selected);
        }

        private void DrawSelectedFields()
        {
            var id = _selected;
            var piece = _pieces[(int)id];
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(Names[(int)id], EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var position = EditorGUILayout.Vector2Field("Vị trí (px, gốc trên-trái)", piece.position);
            var scale = piece.scale;
            if (CanScale(id))
                scale = EditorGUILayout.Vector2Field("Scale", piece.scale);
            var rotation = piece.rotation;
            if (CanRotate(id))
                rotation = EditorGUILayout.FloatField("Xoay (độ)", piece.rotation);
            if (id == LobbyPieceId.TimeButton)
                EditorGUILayout.HelpBox("Nút TIME nằm trong đồng hồ: vị trí, scale, xoay là theo mặt đồng hồ." +
                    (_time == null ? " Không tìm thấy lớp \"Nút Time\" trong prefab đồng hồ." : string.Empty), MessageType.None);
            if (EditorGUI.EndChangeCheck())
                Commit(id, new LobbyPiece(position, new Vector2(Mathf.Max(.01f, scale.x), Mathf.Max(.01f, scale.y)), rotation));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Căn chỉnh", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Giữa ngang màn hình"))
                    MoveSelected(new Vector2(RecoilLobbyLayout.DesignWidth * .5f, piece.position.y));
                if (GUILayout.Button("Giữa dọc màn hình"))
                    MoveSelected(new Vector2(piece.position.x, RecoilLobbyLayout.DesignHeight * .5f));
            }

            var others = AllIds.Where(other => other != id).ToArray();
            var choice = Array.IndexOf(others, _alignTarget);
            if (choice < 0) choice = 0;
            choice = EditorGUILayout.Popup("So với", choice, others.Select(other => Names[(int)other]).ToArray());
            _alignTarget = others[choice];
            var target = _pieces[(int)_alignTarget];
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Đối xứng trái/phải"))
                    MoveSelected(new Vector2(RecoilLobbyLayout.DesignWidth - target.position.x, target.position.y));
                if (GUILayout.Button("Đối xứng trên/dưới"))
                    MoveSelected(new Vector2(target.position.x, RecoilLobbyLayout.DesignHeight - target.position.y));
                if (GUILayout.Button("Đối xứng qua tâm"))
                    MoveSelected(new Vector2(RecoilLobbyLayout.DesignWidth - target.position.x,
                        RecoilLobbyLayout.DesignHeight - target.position.y));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cùng hàng (y)")) MoveSelected(new Vector2(piece.position.x, target.position.y));
                if (GUILayout.Button("Cùng cột (x)")) MoveSelected(new Vector2(target.position.x, piece.position.y));
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Về mặc định (mục này)"))
                    ResetPiece(id);
                if (GUILayout.Button("Về mặc định tất cả"))
                {
                    for (var i = 0; i < LayoutCount; i++) ResetPiece(AllIds[i]);
                    ResetPiece(LobbyPieceId.TimeButton);
                }
            }
        }

        private void MoveSelected(Vector2 position)
        {
            var piece = _pieces[(int)_selected];
            piece.position = position;
            Commit(_selected, piece);
        }

        private void ResetPiece(LobbyPieceId id)
        {
            if (id != LobbyPieceId.TimeButton)
            {
                Commit(id, new RecoilLobbyLayout().Get(id));
                return;
            }
            if (_time == null) return;
            // Same default as the clock tool: the icon sits on the face center, unscaled, unturned.
            var fallback = CongThanhClockImageTool.LoadDefaultLayers()
                .FirstOrDefault(layer => layer.label == CongThanhClockImageTool.TimeButtonLabel);
            if (fallback == null) return;
            _time.scale = 1f;
            _time.rotation = 0f;
            _time.position = _clockCenter + fallback.position;
            SyncTimePiece();
            _dirty = true;
        }

        /// <summary>Writes a piece's transform: layout pieces are stored as they are, the TIME button goes into the clock.</summary>
        private void Commit(LobbyPieceId id, LobbyPiece value)
        {
            if (id == LobbyPieceId.TimeButton)
            {
                if (_time == null) return;
                SetTimeFromHome(value);
                SyncTimePiece();
            }
            else
            {
                _pieces[(int)id] = value;
            }
            _dirty = true;
        }

        /// <summary>The TIME button as it sits on the lobby: the clock's layer carried by the clock's place, scale and turn.</summary>
        private void SyncTimePiece()
        {
            if (_time == null) return;
            var clock = _pieces[(int)LobbyPieceId.Clock];
            var rendered = _clockCenter + RecoilClockImagePreview.Rotate((_time.position - _clockCenter) * _time.scale, _time.rotation);
            var offset = RecoilLobbyLayout.RotateCcw(new Vector2(rendered.x * clock.scale.x, -rendered.y * clock.scale.y), clock.rotation);
            _pieces[(int)LobbyPieceId.TimeButton] = new LobbyPiece(clock.position + offset,
                new Vector2(_time.scale * clock.scale.x, _time.scale * clock.scale.y), clock.rotation + _time.rotation);
        }

        /// <summary>Inverse of <see cref="SyncTimePiece"/>: stores a home transform back into the clock's layer.</summary>
        private void SetTimeFromHome(LobbyPiece home)
        {
            var clock = _pieces[(int)LobbyPieceId.Clock];
            var clockScale = new Vector2(Mathf.Max(.01f, clock.scale.x), Mathf.Max(.01f, clock.scale.y));
            _time.scale = Mathf.Max(.01f, home.scale.x / clockScale.x);
            _time.rotation = home.rotation - clock.rotation;
            var local = RecoilLobbyLayout.UnrotateCcw(home.position - clock.position, clock.rotation);
            var rendered = new Vector2(local.x / clockScale.x, -local.y / clockScale.y);
            _time.position = _clockCenter + RecoilClockImagePreview.Rotate(rendered - _clockCenter, -_time.rotation) / _time.scale;
        }

        private static bool CanScale(LobbyPieceId id) => id != LobbyPieceId.ErrorText;

        private static bool CanRotate(LobbyPieceId id) =>
            id == LobbyPieceId.Title || id == LobbyPieceId.StartButton || id == LobbyPieceId.SettingsButton ||
            id == LobbyPieceId.Clock || id == LobbyPieceId.TimeButton;

        private Vector2 BoundsOf(LobbyPieceId id)
        {
            switch (id)
            {
                case LobbyPieceId.Title: return RecoilLobbyLayout.TitleBounds(_titleAspect);
                case LobbyPieceId.StartButton: return RecoilLobbyLayout.StartButtonBounds;
                case LobbyPieceId.SettingsButton: return RecoilLobbyLayout.SettingsButtonBounds;
                case LobbyPieceId.Clock: return RecoilLobbyLayout.ClockBounds;
                case LobbyPieceId.TimeButton: return _time != null ? _time.size : new Vector2(640f, 640f * 750f / 2097f);
                case LobbyPieceId.LoadingBar: return RecoilLobbyLayout.LoadingBarBounds;
                default: return RecoilLobbyLayout.ErrorTextBounds;
            }
        }

        private Texture2D TextureOf(LobbyPieceId id)
        {
            switch (id)
            {
                case LobbyPieceId.Title: return _titleImage;
                case LobbyPieceId.StartButton: return _startImage;
                case LobbyPieceId.SettingsButton: return _settingsImage;
                default: return null;
            }
        }

        // ---------------------------------------------------------------- preview

        private Vector2 HalfSize(LobbyPieceId id)
        {
            var piece = _pieces[(int)id];
            return Vector2.Scale(BoundsOf(id), new Vector2(Mathf.Abs(piece.scale.x), Mathf.Abs(piece.scale.y))) * .5f;
        }

        /// <summary>Design-space corners of a piece as drawn: its bounds at scale, turned about its position.</summary>
        private Vector2[] Corners(LobbyPieceId id)
        {
            var piece = _pieces[(int)id];
            var half = HalfSize(id);
            var rotation = CanRotate(id) ? piece.rotation : 0f;
            return new[]
            {
                piece.position + RecoilLobbyLayout.RotateCcw(new Vector2(-half.x, -half.y), rotation),
                piece.position + RecoilLobbyLayout.RotateCcw(new Vector2(half.x, -half.y), rotation),
                piece.position + RecoilLobbyLayout.RotateCcw(new Vector2(half.x, half.y), rotation),
                piece.position + RecoilLobbyLayout.RotateCcw(new Vector2(-half.x, half.y), rotation),
            };
        }

        private bool Contains(LobbyPieceId id, Vector2 design)
        {
            var piece = _pieces[(int)id];
            var local = RecoilLobbyLayout.UnrotateCcw(design - piece.position, CanRotate(id) ? piece.rotation : 0f);
            var half = HalfSize(id);
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y;
        }

        private void DrawAndHandlePreview(Rect area)
        {
            var fit = area.width / RecoilLobbyLayout.DesignWidth;
            SyncTimePiece();
            Vector2 ToGui(Vector2 design) => area.position + design * fit;
            Vector2 ToDesign(Vector2 gui) => (gui - area.position) / fit;
            var controlId = GUIUtility.GetControlID(ControlHash, FocusType.Passive, area);
            var e = Event.current;

            if (e.type == EventType.Repaint)
            {
                if (_background != null) GUI.DrawTexture(area, _background, ScaleMode.ScaleAndCrop);
                else EditorGUI.DrawRect(area, new Color(.08f, .09f, .11f));
                foreach (var id in DrawOrder) DrawPiece(id, ToGui);
                DrawGuides(area, ToGui);
                DrawSelection(_selected, ToGui);
            }

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !area.Contains(e.mousePosition)) break;
                    var design = ToDesign(e.mousePosition);
                    if (CanScale(_selected) && TryCornerAt(_selected, e.mousePosition, ToGui))
                    {
                        _drag = DragKind.Corner;
                        _dragStartScale = _pieces[(int)_selected].scale;
                        _dragStartRadius = Mathf.Max(1f, Vector2.Distance(design, _pieces[(int)_selected].position));
                    }
                    else
                    {
                        LobbyPieceId? hit = null;
                        foreach (var id in DrawOrder.Reverse())
                        {
                            if (!Contains(id, design)) continue;
                            hit = id;
                            break;
                        }
                        if (hit == null) break;
                        Select(hit.Value);
                        _drag = DragKind.Move;
                        _dragStartPosition = _pieces[(int)hit.Value].position;
                    }
                    _dragStartMouse = design;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId || _drag == DragKind.None) break;
                    var point = ToDesign(e.mousePosition);
                    var current = _pieces[(int)_selected];
                    if (_drag == DragKind.Corner)
                    {
                        var factor = Vector2.Distance(point, current.position) / _dragStartRadius;
                        current.scale = new Vector2(Mathf.Max(.01f, _dragStartScale.x * factor),
                            Mathf.Max(.01f, _dragStartScale.y * factor));
                    }
                    else
                    {
                        current.position = SnapPosition(_dragStartPosition + (point - _dragStartMouse), fit);
                    }
                    Commit(_selected, current);
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

        /// <summary>Snaps to the frame's center lines and to the other pieces' centers, like the guides of a layout editor.</summary>
        private Vector2 SnapPosition(Vector2 position, float fit)
        {
            var threshold = SnapDistance / fit;
            _snapX = _snapY = false;
            var others = AllIds.Where(id => id != _selected).Select(id => _pieces[(int)id].position).ToArray();
            var xs = others.Select(p => p.x).Append(RecoilLobbyLayout.DesignWidth * .5f);
            var ys = others.Select(p => p.y).Append(RecoilLobbyLayout.DesignHeight * .5f);
            var bestX = xs.OrderBy(x => Mathf.Abs(x - position.x)).First();
            if (Mathf.Abs(bestX - position.x) <= threshold)
            {
                position.x = bestX;
                _snapX = true;
            }
            var bestY = ys.OrderBy(y => Mathf.Abs(y - position.y)).First();
            if (Mathf.Abs(bestY - position.y) <= threshold)
            {
                position.y = bestY;
                _snapY = true;
            }
            return position;
        }

        private bool TryCornerAt(LobbyPieceId id, Vector2 mouse, Func<Vector2, Vector2> toGui)
        {
            foreach (var corner in Corners(id))
                if (Vector2.Distance(mouse, toGui(corner)) <= 9f) return true;
            return false;
        }

        private void DrawPiece(LobbyPieceId id, Func<Vector2, Vector2> toGui)
        {
            var piece = _pieces[(int)id];
            var fit = toGui(Vector2.right).x - toGui(Vector2.zero).x;
            var center = toGui(piece.position);
            var size = Vector2.Scale(BoundsOf(id), new Vector2(Mathf.Abs(piece.scale.x), Mathf.Abs(piece.scale.y))) * fit;
            var rotation = CanRotate(id) ? piece.rotation : 0f;
            var box = new Rect(center - size * .5f, size);
            var old = GUI.matrix;
            GUIUtility.RotateAroundPivot(RecoilLobbyLayout.GuiAngle(rotation), center);

            switch (id)
            {
                case LobbyPieceId.TimeButton:
                    if (_time != null && _time.sprite != null && _time.sprite.texture != null)
                        DrawSpriteFit(box, _time.sprite);
                    else
                        EditorGUI.DrawRect(box, new Color(.3f, .35f, .45f, .6f));
                    break;
                case LobbyPieceId.LoadingBar:
                    EditorGUI.DrawRect(box, new Color(.2f, .24f, .3f, .9f));
                    GUI.Label(box, "Thanh tải", CenteredStyle(Color.white));
                    break;
                case LobbyPieceId.ErrorText:
                    GUI.Label(box, "Thông báo lỗi", CenteredStyle(new Color(1f, .5f, .5f)));
                    break;
                case LobbyPieceId.Clock:
                    if (_clockFace != null && _clockFace.texture != null)
                    {
                        var source = _clockFace.textureRect;
                        var texture = _clockFace.texture;
                        GUI.DrawTextureWithTexCoords(FitRect(box, source.width / source.height), texture,
                            new Rect(source.x / texture.width, source.y / texture.height,
                                source.width / texture.width, source.height / texture.height), true);
                    }
                    else
                    {
                        EditorGUI.DrawRect(box, new Color(.3f, .35f, .45f, .6f));
                    }
                    break;
                default:
                    var image = TextureOf(id);
                    if (image != null)
                        GUI.DrawTexture(FitRect(box, image.width / (float)image.height), image);
                    else
                        EditorGUI.DrawRect(box, new Color(.3f, .35f, .45f, .6f));
                    break;
            }
            GUI.matrix = old;
        }

        private static void DrawSpriteFit(Rect box, Sprite sprite)
        {
            var source = sprite.textureRect;
            var texture = sprite.texture;
            GUI.DrawTextureWithTexCoords(FitRect(box, source.width / source.height), texture,
                new Rect(source.x / texture.width, source.y / texture.height,
                    source.width / texture.width, source.height / texture.height), true);
        }

        private void DrawSelection(LobbyPieceId id, Func<Vector2, Vector2> toGui)
        {
            var corners = Corners(id).Select(c => (Vector3)toGui(c)).ToArray();
            var previous = Handles.color;
            Handles.color = Color.yellow;
            Handles.DrawAAPolyLine(2f, corners[0], corners[1], corners[2], corners[3], corners[0]);
            if (CanScale(id))
                foreach (var corner in corners)
                    GUI.DrawTexture(new Rect(corner.x - 5f, corner.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            Handles.color = previous;
        }

        private void DrawGuides(Rect area, Func<Vector2, Vector2> toGui)
        {
            if (_drag != DragKind.Move) return;
            var previous = Handles.color;
            Handles.color = new Color(.3f, .9f, 1f, .9f);
            var piece = _pieces[(int)_selected];
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
            if (e.type != EventType.KeyDown) return;
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
            MoveSelected(_pieces[(int)_selected].position + delta);
            e.Use();
            Repaint();
        }

        private static GUIStyle CenteredStyle(Color color)
        {
            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            style.normal.textColor = color;
            return style;
        }

        private static Rect FitRect(Rect box, float aspect)
        {
            if (aspect <= 0f || box.height <= 0f) return box;
            var size = box.width / box.height > aspect
                ? new Vector2(box.height * aspect, box.height)
                : new Vector2(box.width, box.width / aspect);
            return new Rect(box.center - size * .5f, size);
        }

        // ---------------------------------------------------------------- load / save

        private static RecoilAutoBattleDemo FindDemo(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            return Resources.FindObjectsOfTypeAll<RecoilAutoBattleDemo>()
                .FirstOrDefault(demo => demo.gameObject.scene == scene && !EditorUtility.IsPersistent(demo));
        }

        private void Load()
        {
            _demo = null;
            _dirty = false;
            _drag = DragKind.None;
            _status = null;
            var demo = FindDemo(SceneManager.GetActiveScene());
            if (demo == null) demo = FindDemo(SceneManager.GetSceneByPath(LobbyScenePath));
            if (demo == null) return;

            _demo = demo;
            _scene = demo.gameObject.scene;
            var serialized = new SerializedObject(demo);
            var layout = serialized.FindProperty("lobbyLayout");
            for (var i = 0; i < LayoutCount; i++)
            {
                var piece = layout.FindPropertyRelative(FieldNames[i]);
                _pieces[i] = new LobbyPiece(piece.FindPropertyRelative("position").vector2Value,
                    piece.FindPropertyRelative("scale").vector2Value, piece.FindPropertyRelative("rotation").floatValue);
            }
            _background = serialized.FindProperty("lobbyBackgroundImage").objectReferenceValue as Texture2D;
            _titleImage = serialized.FindProperty("lobbyTitleImage").objectReferenceValue as Texture2D;
            _startImage = serialized.FindProperty("lobbyStartButtonImage").objectReferenceValue as Texture2D;
            _settingsImage = serialized.FindProperty("lobbySettingsButtonImage").objectReferenceValue as Texture2D;
            _titleAspect = _titleImage != null && _titleImage.height > 0
                ? (float)_titleImage.width / _titleImage.height : 3f;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClockPrefabPath);
            var clock = prefab != null ? prefab.GetComponent<RecoilClockImagePreview>() : null;
            _clockFace = clock != null
                ? clock.Layers.FirstOrDefault(layer => layer != null &&
                    layer.label == RecoilClockImagePreview.FaceLayerLabel)?.sprite
                : null;
            LoadTimeLayer(clock);
            SyncTimePiece();
        }

        private void LoadTimeLayer(RecoilClockImagePreview clock)
        {
            _time = null;
            if (clock == null) return;
            var layers = clock.Layers;
            _clockCenter = RecoilClockImagePreview.GetRotationCenter(layers);
            var layer = layers.FirstOrDefault(candidate => candidate != null &&
                candidate.label == CongThanhClockImageTool.TimeButtonLabel && string.IsNullOrEmpty(candidate.attachTo));
            if (layer == null) return;
            _time = new TimeLayer
            {
                position = layer.position,
                scale = layer.scale,
                rotation = layer.rotation,
                size = layer.size,
                sprite = layer.sprite,
            };
        }

        private void Save()
        {
            if (_demo == null) throw new InvalidOperationException("Chưa có RecoilAutoBattleDemo.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Thoát Play Mode trước.");

            var serialized = new SerializedObject(_demo);
            serialized.Update();
            Undo.RecordObject(_demo, "Chỉnh giao diện sảnh");
            var layout = serialized.FindProperty("lobbyLayout");
            for (var i = 0; i < LayoutCount; i++)
            {
                var piece = layout.FindPropertyRelative(FieldNames[i]);
                piece.FindPropertyRelative("position").vector2Value = _pieces[i].position;
                piece.FindPropertyRelative("scale").vector2Value = _pieces[i].scale;
                piece.FindPropertyRelative("rotation").floatValue = _pieces[i].rotation;
            }
            serialized.ApplyModifiedProperties();

            var timeNote = SaveTimeButton();
            var clockNote = PlaceClockInScene();
            EditorSceneManager.MarkSceneDirty(_scene);
            if (!EditorSceneManager.SaveScene(_scene))
                throw new InvalidOperationException("Không lưu được " + _scene.path);
            _dirty = false;
            _status = "Đã lưu giao diện sảnh vào " + _scene.path + ".\n" + clockNote + "\n" + timeNote;
        }

        /// <summary>Writes the TIME layer into the clock prefab, and into the test scene the prefab is rebuilt from.</summary>
        private string SaveTimeButton()
        {
            if (_time == null) return "Không có nút TIME để lưu.";
            var prefab = PrefabUtility.LoadPrefabContents(ClockPrefabPath);
            try
            {
                var preview = prefab.GetComponentInChildren<RecoilClockImagePreview>(true);
                if (preview == null) throw new InvalidOperationException("Prefab đồng hồ không có RecoilClockImagePreview.");
                WriteTimeLayer(preview);
                PrefabUtility.SaveAsPrefabAsset(prefab, ClockPrefabPath, out var saved);
                if (!saved) throw new InvalidOperationException("Không lưu được prefab " + ClockPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefab);
            }

            var testPath = CongThanhClockImageTool.TestScenePath;
            if (!File.Exists(testPath)) return "Đã lưu nút TIME vào prefab.";
            var scene = SceneManager.GetSceneByPath(testPath);
            var opened = !scene.IsValid() || !scene.isLoaded;
            if (!opened && scene.isDirty)
                return "Đã lưu nút TIME vào prefab; scene test đang có thay đổi chưa lưu nên chưa ghi vào. Hãy Ctrl+S scene test rồi lưu lại.";
            if (opened) scene = EditorSceneManager.OpenScene(testPath, OpenSceneMode.Additive);
            try
            {
                var preview = CongThanhClockImageTool.FindPreview(scene);
                if (preview != null)
                {
                    WriteTimeLayer(preview);
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            }
            return "Đã lưu nút TIME vào prefab và scene test.";
        }

        private void WriteTimeLayer(RecoilClockImagePreview preview)
        {
            var serialized = new SerializedObject(preview);
            serialized.Update();
            var layers = serialized.FindProperty("layers");
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                if (layer.FindPropertyRelative("label").stringValue != CongThanhClockImageTool.TimeButtonLabel ||
                    !string.IsNullOrEmpty(layer.FindPropertyRelative("attachTo").stringValue)) continue;
                layer.FindPropertyRelative("position").vector2Value = _time.position;
                layer.FindPropertyRelative("scale").floatValue = _time.scale;
                layer.FindPropertyRelative("rotation").floatValue = _time.rotation;
                layer.FindPropertyRelative("visible").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                preview.RebuildImages();
                return;
            }
            throw new InvalidOperationException("Đồng hồ không có lớp \"" + CongThanhClockImageTool.TimeButtonLabel + "\".");
        }

        /// <summary>Moves the clock's holder in the lobby canvas now, so the scene shows the saved place before Play.</summary>
        private string PlaceClockInScene()
        {
            var canvas = new SerializedObject(_demo).FindProperty("lobbyClockCanvas").objectReferenceValue as Canvas;
            var clock = canvas != null ? canvas.GetComponentInChildren<RecoilClockImagePreview>(true) : null;
            var holder = clock != null ? clock.transform.parent as RectTransform : null;
            if (holder == null) return "Chưa có đồng hồ ở sảnh: chạy \"Thêm / cập nhật đồng hồ ở sảnh\" trước.";

            var piece = _pieces[(int)LobbyPieceId.Clock];
            Undo.RecordObject(holder, "Chỉnh giao diện sảnh");
            holder.anchorMin = holder.anchorMax = new Vector2(.5f, .5f);
            holder.pivot = new Vector2(.5f, .5f);
            holder.sizeDelta = Vector2.zero;
            holder.anchoredPosition = RecoilLobbyLayout.CanvasPosition(piece.position);
            holder.localScale = new Vector3(piece.scale.x, piece.scale.y, 1f);
            holder.localRotation = Quaternion.Euler(0f, 0f, piece.rotation);
            return "Đồng hồ ở sảnh đã đặt theo vị trí mới.";
        }

        private void RunSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _status = exception.Message;
                Debug.LogException(exception);
            }
            Repaint();
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }
    }
}
