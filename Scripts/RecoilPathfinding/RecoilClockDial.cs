using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RecoilPathfinding
{
    /// <summary>
    /// Lets the player set the time on the clock by turning its two number rings.
    /// <list type="bullet">
    /// <item>Click a number: its ring turns clockwise until that number sits at the indicator, and the hands run
    /// clockwise to the new time like a real clock (the hour hand moves 1/12 of the minute hand). Ring, hands and
    /// gears share one slow eased timeline, so the gears keep turning until the hands stop.</item>
    /// <item>Drag a ring: it follows the pointer; the hands follow the selected time live.</item>
    /// </list>
    /// Gears turn with every ring move (negative ratio = opposite direction) around their own centers; ring numbers
    /// stay upright. Drives the images built by <see cref="RecoilClockImagePreview"/>; the saved layout is never changed.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RecoilClockImagePreview))]
    public sealed class RecoilClockDial : MonoBehaviour
    {
        [Serializable]
        public struct GearLink
        {
            [Tooltip("Tên lớp bánh răng.")]
            public string label;
            [Tooltip("Số độ bánh răng quay cho mỗi 1° vòng quay. Âm = ngược chiều vòng, dương = cùng chiều.")]
            public float ratio;
        }

        [Header("Lớp")]
        [SerializeField] private string hourRingLabel = "Vòng giờ";
        [SerializeField] private string minuteRingLabel = "Vòng phút";
        [SerializeField] private string hourHandLabel = "Kim giờ";
        [SerializeField] private string minuteHandLabel = "Kim phút";
        [SerializeField, Tooltip("Lớp chỉ báo: bấm vào một số thì vòng xoay số đó tới chỉ báo.")]
        private string indicatorLabel = "Chỉ báo";
        [SerializeField, Tooltip("Bật: số dừng trên đường ngang qua tâm, ở phía có chỉ báo. Tắt: dừng đúng hướng của chỉ báo.")]
        private bool horizontalIndicator = true;
        [SerializeField, Tooltip("Góc xoay (độ) của hình kim khi kim chỉ số 12. Hình kim vẽ hướng sang phải thì là 90.")]
        private float handTwelveRotation = 90f;
        [SerializeField]
        private GearLink[] gears =
        {
            // Outer and inner-large gears turn against the ring; the inner-small gear turns with it.
            new GearLink { label = "Bánh răng ngoài", ratio = -1f },
            new GearLink { label = "Bánh răng trong lớn", ratio = -1f },
            new GearLink { label = "Bánh răng trong nhỏ", ratio = 1f },
        };

        [Header("Bấm vào số")]
        [SerializeField, Min(1f), Tooltip("Tốc độ vòng khi bấm số (độ/giây). Nhỏ hơn = chậm hơn.")]
        private float clickRingSpeed = 150f;
        [SerializeField, Min(1f), Tooltip("Tốc độ kim phút khi bấm số (độ/giây).")]
        private float clickMinuteHandSpeed = 1440f;
        [SerializeField, Min(0f), Tooltip("Mỗi lần bấm, vòng + kim + bánh răng chạy ít nhất chừng này giây...")]
        private float minClickDuration = .6f;
        [SerializeField, Min(0f), Tooltip("...và nhiều nhất chừng này giây.")]
        private float maxClickDuration = 2.5f;
        [SerializeField, Tooltip("Shortest: vòng và kim đi đường ngắn nhất, được quay ngược. " +
                                 "Clockwise: luôn quay theo chiều kim đồng hồ (kim có thể chạy vòng tới 12 tiếng).")]
        private ClickTurn clickTurn = ClickTurn.Shortest;

        public enum ClickTurn { Shortest, Clockwise }

        [Header("Kéo vòng")]
        [SerializeField, Min(0f), Tooltip("Di chuyển quá số pixel này khi đang giữ thì tính là kéo, không phải bấm.")]
        private float dragThreshold = 8f;
        [SerializeField, Min(1f), Tooltip("Kim bám theo vòng khi kéo sát cỡ nào (lớn hơn = sát hơn).")]
        private float handFollowSharpness = 18f;
        [SerializeField, Tooltip("Thả tay sau khi kéo thì vòng tự trượt về số gần chỉ báo nhất (có thể quay ngược) để kim chỉ đúng số.")]
        // Named differently from the old "snapAfterDrag" (default off) on purpose: scenes that saved that field as
        // false must not keep the snap disabled.
        private bool settleOnNumberAfterDrag = true;
        [SerializeField, Min(1f), Tooltip("Tốc độ trượt về số sau khi thả tay (độ/giây).")]
        private float snapSpeed = 45f;
        [SerializeField, Min(0f)] private float snapMinDuration = .2f;
        [SerializeField, Min(0f)] private float snapMaxDuration = .8f;
        [SerializeField, Min(0f), Tooltip("Thả tay khi vòng còn quay nhanh hơn mức này (độ/giây) thì vòng trôi tiếp theo đà tới số kế tiếp.")]
        private float flickSpeed = 40f;

        [Serializable]
        public struct RevealLayer
        {
            [Tooltip("Tên lớp.")]
            public string label;
            [Tooltip("Lúc còn ẩn lớp lệch góc này (độ); khi hiện ra lớp vừa nở vừa quay về đúng chỗ. " +
                     "Dương = lệch ngược chiều kim đồng hồ, nên lớp quay vào theo chiều kim đồng hồ.")]
            public float turn;
        }

        [Header("Ẩn sau mặt đồng hồ")]
        [SerializeField, Tooltip("Các lớp ẩn sau mặt đồng hồ, bấm vào mặt đồng hồ mới hiện ra (số trên vòng đi theo vòng).")]
        private RevealLayer[] revealLayers =
        {
            new RevealLayer { label = "Vòng giờ", turn = 180f },         // turns in clockwise
            new RevealLayer { label = "Vòng phút", turn = -180f },       // counter-rotates
            new RevealLayer { label = "Bánh răng ngoài", turn = 0f },    // turned by the gear train instead
            new RevealLayer { label = "Chỉ báo", turn = 90f },           // sweeps in from 12 o'clock
        };
        [SerializeField, Tooltip("Khi hiện ra / thu vào, các bánh răng (mục Gears) quay theo vòng giờ như lúc kéo vòng.")]
        private bool gearsFollowReveal = true;
        [SerializeField, Tooltip("Lúc bắt đầu các lớp trên đang ẩn sau mặt đồng hồ.")]
        private bool startHidden = true;
        [SerializeField, Tooltip("Đang hiện mà bấm mặt đồng hồ thì thu các lớp lại vào sau mặt.")]
        private bool faceClickHides = true;
        [SerializeField, Tooltip("Đang hiện mà bấm ra chỗ trống bên ngoài đồng hồ thì cũng thu gọn lại.")]
        private bool emptyClickHides = true;
        [SerializeField, Min(.01f), Tooltip("Thời gian hiện ra / thu vào (giây).")]
        private float revealDuration = .7f;

        [Header("Giờ ban đầu")]
        [SerializeField, Range(0, 11), Tooltip("Giờ nằm ở chỉ báo khi vào game (số trên vòng giờ).")]
        private int startHour;
        [SerializeField, Range(0, 59), Tooltip("Phút nằm ở chỉ báo khi vào game (số trên vòng phút). " +
                                              "Ở sảnh, giờ + phút đang chỉnh là thời lượng trận (0 giờ 0 phút = không giới hạn).")]
        private int startMinute = 10;

        [Header("Thu vào nút TIME")]
        [SerializeField, Tooltip("Lớp băng nút (ví dụ \"Nút Time\"). Lúc đóng, cả đồng hồ thu nhỏ khớp vào icon tròn của nút; " +
                                 "bấm vào nút hoặc mặt đồng hồ thì mặt đồng hồ nở ra rồi các vòng mới xoay ra. Để trống = không dùng.")]
        private string collapseButtonLabel = "Nút Time";
        [SerializeField, Tooltip("Tâm icon tròn trên hình nút, theo tỉ lệ tính từ góc trên-trái của hình.")]
        private Vector2 collapseIconCenter = new(.2157f, .562f);
        [SerializeField, Tooltip("Bán kính icon tròn trên hình nút, theo tỉ lệ chiều cao hình.")]
        private float collapseIconRadius = .165f;
        [SerializeField, Min(.01f), Tooltip("Thời gian mặt đồng hồ nở ra từ nút / thu về nút (giây).")]
        private float growDuration = .5f;

        [Header("Hover nút TIME")]
        [SerializeField, Min(1f), Tooltip("Rê chuột vào nút TIME lúc đồng hồ đang đóng: mặt đồng hồ thu gọn phóng to bấy nhiêu lần " +
                                         "quanh icon (nút TIME đứng yên).")]
        private float hoverFaceScale = 2.5f;
        [SerializeField, Min(.01f), Tooltip("Thời gian mặt đồng hồ phóng to / thu lại khi hover (giây).")]
        private float hoverDuration = .45f;
        [SerializeField, Min(0f), Tooltip("Lúc hover, kim phút quay bấy nhiêu độ mỗi giây theo chiều kim đồng hồ (kim giờ chậm hơn 12 lần). " +
                                         "Rời chuột thì mỗi kim quay nốt vòng, chậm dần và dừng đúng giờ.")]
        private float hoverHandSpeed = 720f;
        [SerializeField, Tooltip("Lúc hover, các bánh răng quay như khi vòng giờ được xoay với tốc độ này (độ/giây).")]
        private float hoverGearSpeed = 120f;

        [Header("Âm thanh")]
        [SerializeField, Tooltip("Tiếng cơ khí khi đồng hồ xoay (mở / đóng, kim, vòng, bánh răng). Chỉ dùng đoạn đầu " +
                                 "(Clank Max Duration giây), lặp liền mạch suốt lúc còn xoay và tắt khi ngừng.")]
        private AudioClip clankSound;
        [SerializeField, Range(0f, 1f), Tooltip("Âm lượng khi đồng hồ đang mở ra / thu vào.")]
        private float openCloseVolume = .85f;
        [SerializeField, Range(0f, 1f), Tooltip("Âm lượng khi kim / vòng / bánh răng quay nhanh nhất.")]
        private float gearClankVolume = .4f;
        [SerializeField, Min(1f), Tooltip("Kim phút quay nhanh cỡ này (độ/giây) thì tiếng bánh răng to hết mức Gear Clank Volume; " +
                                         "quay chậm hơn thì nhỏ dần.")]
        private float clankFullSpeed = 720f;
        [SerializeField, Range(0f, 1f), Tooltip("Âm lượng tiếng bánh răng khi kim gần như đứng yên, so với lúc kim quay nhanh nhất.")]
        private float clankSlowVolume = .25f;
        [SerializeField, Range(0f, 1f), Tooltip("Kim quay nhanh nhất thì tiếng bánh răng cao hơn chừng này (0.2 = cao hơn 20%).")]
        private float clankFastPitch = .2f;
        [SerializeField, Min(.05f), Tooltip("Chỉ dùng chừng này giây đầu của âm, lặp liền mạch (không nghỉ, không chồng) " +
                                           "suốt lúc đồng hồ còn xoay.")]
        private float clankMaxDuration = .12f;
        [SerializeField, Min(.01f), Tooltip("Thời gian tắt dần khi đồng hồ ngừng xoay (giây).")]
        private float clankStopFade = .08f;

        [Header("Sự kiện")]
        [SerializeField, Tooltip("Gọi khi một số dừng tại chỉ báo: (tên vòng, số).")]
        private UnityEvent<string, string> onNumberAligned = new();
        [SerializeField, Tooltip("Gọi khi kim dừng sau khi bấm số: (giờ 0–11, phút 0–59).")]
        private UnityEvent<int, int> onTimeSet = new();
        [SerializeField, Tooltip("Gọi khi bấm mặt đồng hồ: true = bắt đầu hiện, false = bắt đầu thu vào.")]
        private UnityEvent<bool> onRevealChanged = new();

        public UnityEvent<string, string> OnNumberAligned => onNumberAligned;
        public UnityEvent<int, int> OnTimeSet => onTimeSet;
        public UnityEvent<bool> OnRevealChanged => onRevealChanged;
        /// <summary>True once the rings are fully out from behind the face (they only take input then).</summary>
        public bool IsRevealed => _reveal >= 1f && _revealTarget >= 1f;

        /// <summary>
        /// Time chosen on the dial in minutes: hour ring × 60 + minute ring (0–719). A ring still turning to a number
        /// already counts as there, so a click just before reading it is not lost.
        /// </summary>
        public int SelectedTotalMinutes => _ready && _rings.Count > 0
            ? Mathf.RoundToInt(SelectedMinutes(TargetOf(_hourRing), TargetOf(_minuteRing))) % 720
            : StartMinutes;

        private int StartMinutes => Mathf.Clamp(startHour, 0, 11) * 60 + Mathf.Clamp(startMinute, 0, 59);

        /// <summary>
        /// True when <paramref name="screenPoint"/> is on a part of the clock that takes input: the face, or a ring
        /// once revealed. Lets other UI drawn under the clock ignore clicks meant for it.
        /// </summary>
        public bool ContainsScreenPoint(Vector2 screenPoint)
        {
            if (!_ready || _clock == null || !TryGetClockPoint(screenPoint, out var point)) return false;
            if (_face != null && (point - _center).magnitude <= _faceRadius) return true;
            if (OnCollapseButton(point)) return true;
            return IsRevealed && TryHit(point, out _, out _);
        }

        /// <summary>Shows (true) or tucks away (false) the reveal layers behind the clock face.</summary>
        public void SetRevealed(bool revealed)
        {
            var target = revealed ? 1f : 0f;
            if (_revealTarget == target) return;
            _revealTarget = target;
            onRevealChanged.Invoke(revealed);
        }

        /// <summary>An image that can be tucked behind the face: scaled toward the face center, turned and faded.</summary>
        private sealed class RevealItem
        {
            public RectTransform image;
            public Graphic graphic;
            public Color color;
            public Vector2 basePosition; // where the dial logic puts it when fully shown
            public float hiddenScale; // small enough to sit behind the face
            public float turn; // extra rotation while tucked away, unwinding to 0 as it comes out
            public bool turnsItself; // false for rings, ring numbers and gears: their own pose code applies the turn
            public float baseRotation; // for turnsItself items
            public int siblingIndex; // draw order in the saved layout
        }

        private sealed class Number
        {
            public RecoilClockImageLayer layer;
            public RectTransform image;
            public RevealItem reveal;
            public string text;
            public int value;
            public float baseAngle; // direction from the face center with the ring unrotated (deg, CCW from +x)
        }

        private sealed class Ring
        {
            public RecoilClockImageLayer layer;
            public RectTransform image;
            public RevealItem reveal;
            public readonly List<Number> numbers = new(); // ascending value = clockwise around the ring
            public float step, cycle; // e.g. 1 and 12 for hours, 5 and 60 for minutes
            public float offset; // degrees the player has added on top of the saved rotation
            public float radius, halfWidth; // hit band around the numbers
            public bool animating, animIsSnap;
            public float animDistance, animStartSpeed, animExponent, animApplied, animTime, animDuration;
            public Number animTarget;
            public float Speed => animating ? GlideSpeed(animDistance, animStartSpeed, animDuration, animTime, animExponent) : 0f;
            public float Rotation => layer.rotation + offset;
        }

        private sealed class Gear
        {
            public RectTransform image;
            public float baseRotation, spin, ratio;
        }

        private readonly List<Ring> _rings = new();
        private readonly List<Gear> _gears = new();
        private RecoilClockImagePreview _clock;
        private Ring _hourRing, _minuteRing;
        private RectTransform _hourHand, _minuteHand;
        private Camera _canvasCamera;
        private Vector2 _center;
        private float _indicatorAngle;
        private bool _ready;

        private readonly List<RevealItem> _revealItems = new();
        private readonly List<Transform> _layoutOrder = new(); // every clock image, in the saved draw order
        private RectTransform _face;
        private float _faceRadius;
        private float _clockRadius; // everything the open clock covers (face, rings, gears, indicator), from its center
        private float _reveal, _revealTarget; // 0 = behind the face, 1 = fully shown; _revealTarget = open target

        // Open progress 0..1 covers both stages: the face growing out of the TIME button, then the rings coming out.
        private float _open, _grow = 1f;
        private RectTransform _collapseButton;
        private Graphic _collapseButtonGraphic;
        private Color _collapseButtonColor;
        private Vector2 _collapseButtonBasePosition, _collapseButtonCenter, _collapseButtonHalfSize, _collapseIconPoint;
        private float _collapseButtonRotation, _collapsedScale = 1f;
        private bool _rootBaseCaptured;
        private float _hoverAmount; // 0 = resting, 1 = fully hovered (eased when drawn)
        private readonly HandSpin _hoverMinuteSpin = new(), _hoverHourSpin = new();
        private float _hoverGearDrive, _hoverGearSpeedNow; // gear drive in ring degrees (positive = clockwise)
        private Vector2 _rootBasePosition;
        private Vector3 _rootBaseScale = Vector3.one;
        private bool _revealInitialized, _behindFace, _pressedFace;

        // Hands, as clockwise degrees from 12.
        private float _hourAngle, _minuteAngle;
        private bool _handAnimating;
        private float _handFrom, _handDistance, _handStartSpeed, _handExponent, _handTime, _handDuration; // minutes, 12-hour dial
        private float HandSpeed => _handAnimating
            ? GlideSpeed(_handDistance, _handStartSpeed, _handDuration, _handTime, _handExponent)
            : 0f;

        private Ring _active;
        private Number _pressedNumber;
        private Vector2 _pressScreen;
        private float _lastPointerAngle;
        private float _dragSpeed; // smoothed ring speed while dragging (deg/s, positive = counter-clockwise)
        private bool _dragging;

        private void Start() => _ready = TryInit();

        private void Update()
        {
            if (!_ready || CacheBroken())
            {
                _ready = TryInit();
                if (!_ready) return;
            }

            HandlePointer();
            var deltaTime = Time.unscaledDeltaTime;
            foreach (var ring in _rings)
                AnimateRing(ring, deltaTime);
            UpdateCollapseHover(deltaTime); // before the hands, so their hover spin shows this frame
            UpdateHands(deltaTime);
            TrackHandSpeed(deltaTime);
            UpdateReveal(deltaTime);
            UpdateClankAudio(deltaTime);
        }

        private void OnDisable()
        {
            // Hidden or flown off the lobby: no sound may keep going on its own.
            if (_clankSource != null) _clankSource.Stop();
        }

        // ---------------------------------------------------------------- setup

        private bool TryInit()
        {
            // The first time the dial shows its start time; a rebuild keeps whatever had been chosen.
            var showMinutes = _ready && _rings.Count > 0 ? SelectedTotalMinutes : StartMinutes;
            _rings.Clear();
            _gears.Clear();
            _hourRing = _minuteRing = null;
            _active = null;
            _handAnimating = false;
            // The clip is not preloaded by its import settings; load it now so the first clank is not late.
            if (clankSound != null && clankSound.loadState == AudioDataLoadState.Unloaded)
                clankSound.LoadAudioData();
            _clock = GetComponent<RecoilClockImagePreview>();
            var layers = _clock.Layers;
            var root = _clock.ClockRoot;
            if (layers == null || root == null)
                return false;

            _center = _clock.RotationCenter;
            var canvas = root.GetComponentInParent<Canvas>();
            _canvasCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

            _hourRing = BuildRing(layers, root, hourRingLabel);
            _minuteRing = BuildRing(layers, root, minuteRingLabel);
            if (_hourRing != null) _rings.Add(_hourRing);
            if (_minuteRing != null) _rings.Add(_minuteRing);
            // Hit tests go front to back.
            _rings.Sort((a, b) => b.layer.order.CompareTo(a.layer.order));

            foreach (var link in gears)
            {
                var gearIndex = IndexOf(layers, link.label);
                var image = gearIndex >= 0 ? FindImage(root, link.label) : null;
                if (image == null)
                {
                    Debug.LogWarning($"[RecoilClockDial] Không tìm thấy bánh răng \"{link.label}\" đang hiển thị.", this);
                    continue;
                }
                // Gears spin in place: move the pivot to the gear's own center without moving the image.
                var layer = layers[gearIndex];
                image.pivot = new Vector2(.5f, .5f);
                image.anchoredPosition = _center +
                                         RecoilClockImagePreview.Rotate((layer.position - _center) * layer.scale, layer.rotation);
                image.localRotation = Quaternion.Euler(0f, 0f, layer.rotation);
                _gears.Add(new Gear { image = image, baseRotation = layer.rotation, ratio = link.ratio });
            }

            _hourHand = FindHand(layers, root, hourHandLabel);
            _minuteHand = FindHand(layers, root, minuteHandLabel);
            _indicatorAngle = ComputeIndicatorAngle(layers);
            ShowTime(showMinutes);
            SetHands(SelectedMinutes());
            InitReveal(layers, root);
            foreach (var ring in _rings)
                ApplyRingPose(ring); // InitReveal poses them too, but not when there is no face to hide behind
            return _rings.Count > 0;
        }

        /// <summary>Turns the rings so the indicator reads <paramref name="totalMinutes"/>: hours on the hour ring, minutes on the minute ring.</summary>
        private void ShowTime(int totalMinutes)
        {
            totalMinutes = (totalMinutes % 720 + 720) % 720;
            if (_hourRing != null) PutValueAtIndicator(_hourRing, totalMinutes / 60);
            if (_minuteRing != null) PutValueAtIndicator(_minuteRing, totalMinutes % 60);
        }

        /// <summary>Inverse of <see cref="ValueAtIndicator"/>: sets the ring's offset so <paramref name="value"/> sits at the indicator.</summary>
        private void PutValueAtIndicator(Ring ring, float value)
        {
            var numbers = ring.numbers; // ascending values
            var count = numbers.Count;
            value = Mathf.Repeat(value, ring.cycle);
            var k = count - 1; // below the first value: between the last number and the first
            for (var i = 0; i < count; i++)
                if (numbers[i].value <= value) k = i;
            var from = numbers[k];
            var to = numbers[(k + 1) % count];
            var along = Mathf.Repeat(value - from.value, ring.cycle) / Mathf.Max(ring.step, .0001f);
            var span = Mathf.Repeat(from.baseAngle - to.baseAngle, 360f); // clockwise from -> to
            var atIndicator = from.baseAngle - along * span;
            ring.offset = Mathf.DeltaAngle(0f, _indicatorAngle - atIndicator - ring.layer.rotation);
        }

        private Ring BuildRing(RecoilClockImageLayer[] layers, RectTransform root, string label)
        {
            var ringIndex = IndexOf(layers, label);
            var image = ringIndex >= 0 ? FindImage(root, label) : null;
            if (image == null)
            {
                Debug.LogWarning($"[RecoilClockDial] Không tìm thấy vòng \"{label}\" đang hiển thị.", this);
                return null;
            }

            var ring = new Ring { layer = layers[ringIndex], image = image };
            var radiusSum = 0f;
            var maxHalfDiagonal = 0f;
            for (var i = 0; i < layers.Length; i++)
            {
                if (RecoilClockImagePreview.FindParentIndex(layers, i) != ringIndex) continue;
                var numberImage = FindImage(root, layers[i].label);
                var text = NumberText(layers[i].label, label);
                if (numberImage == null || !int.TryParse(text, out var value)) continue;
                var fromCenter = (ring.layer.position + layers[i].position - _center) * ring.layer.scale;
                ring.numbers.Add(new Number
                {
                    layer = layers[i],
                    image = numberImage,
                    text = text,
                    value = value,
                    baseAngle = Mathf.Atan2(fromCenter.y, fromCenter.x) * Mathf.Rad2Deg
                });
                radiusSum += fromCenter.magnitude;
                maxHalfDiagonal = Mathf.Max(maxHalfDiagonal,
                    (layers[i].size * (layers[i].scale * ring.layer.scale)).magnitude * .5f);
            }
            if (ring.numbers.Count < 2)
            {
                Debug.LogWarning($"[RecoilClockDial] Vòng \"{label}\" chưa có số gắn vào (chạy tách số trong tool trước).", this);
                return null;
            }

            ring.numbers.Sort((a, b) => a.value.CompareTo(b.value));
            ring.step = ring.numbers[1].value - ring.numbers[0].value;
            ring.cycle = ring.numbers[ring.numbers.Count - 1].value + ring.step;
            ring.radius = radiusSum / ring.numbers.Count;
            ring.halfWidth = maxHalfDiagonal * 1.2f;
            return ring;
        }

        private RectTransform FindHand(RecoilClockImageLayer[] layers, RectTransform root, string label)
        {
            var image = IndexOf(layers, label) >= 0 ? FindImage(root, label) : null;
            if (image == null)
                Debug.LogWarning($"[RecoilClockDial] Không tìm thấy kim \"{label}\" đang hiển thị.", this);
            return image;
        }

        private bool CacheBroken()
        {
            foreach (var ring in _rings)
            {
                if (ring.image == null) return true;
                foreach (var number in ring.numbers)
                    if (number.image == null) return true;
            }
            foreach (var gear in _gears)
                if (gear.image == null) return true;
            return false;
        }

        // ---------------------------------------------------------------- input

        private void HandlePointer()
        {
            var pointer = Pointer.current;
            if (pointer == null) return;
            var screen = pointer.position.ReadValue();

            // Pressing alone does not stop anything, so quick successive clicks keep the motion flowing.
            // The rings only take input once fully out from behind the face; the face itself toggles them.
            if (pointer.press.wasPressedThisFrame && TryGetClockPoint(screen, out var pressPoint))
            {
                if (IsRevealed && TryHit(pressPoint, out var ring, out var number))
                {
                    _active = ring;
                    _pressedNumber = number;
                    _pressScreen = screen;
                    _dragging = false;
                }
                else if (_face != null && ((pressPoint - _center).magnitude <= _faceRadius || OnCollapseButton(pressPoint)))
                {
                    _pressedFace = true;
                    _pressScreen = screen;
                }
                else if (emptyClickHides && _face != null && _revealTarget >= 1f &&
                         (pressPoint - _center).magnitude > _clockRadius)
                {
                    SetRevealed(false); // a click on empty space folds the open clock away
                }
            }

            if (_pressedFace)
            {
                if (pointer.press.isPressed)
                {
                    if ((screen - _pressScreen).sqrMagnitude > dragThreshold * dragThreshold)
                        _pressedFace = false; // moved away: not a click on the face
                    return;
                }
                _pressedFace = false;
                if (_revealTarget < 1f) SetRevealed(true);
                else if (faceClickHides) SetRevealed(false);
                return;
            }
            if (_active == null) return;

            if (pointer.press.isPressed)
            {
                if (!_dragging && (screen - _pressScreen).sqrMagnitude > dragThreshold * dragThreshold &&
                    TryGetClockPoint(screen, out var dragStart))
                {
                    // A real drag takes the ring over; the hands go back to following the rings.
                    _dragging = true;
                    _active.animating = false;
                    _handAnimating = false;
                    _lastPointerAngle = AngleOf(dragStart);
                    _dragSpeed = 0f;
                }
                else if (_dragging && TryGetClockPoint(screen, out var dragPoint))
                {
                    var angle = AngleOf(dragPoint);
                    var delta = Mathf.DeltaAngle(_lastPointerAngle, angle);
                    Turn(_active, delta);
                    _lastPointerAngle = angle;
                    var deltaTime = Time.unscaledDeltaTime;
                    if (deltaTime > 0f)
                        _dragSpeed = Mathf.Lerp(_dragSpeed, delta / deltaTime, 1f - Mathf.Exp(-20f * deltaTime));
                }
                return;
            }

            // Released (also covers a press and release within the same frame).
            if (!_dragging && _pressedNumber != null)
                ClickNumber(_active, _pressedNumber);
            else if (_dragging && settleOnNumberAfterDrag)
                SnapAfterDrag(_active, _dragSpeed);
            _active = null;
            _pressedNumber = null;
            _dragging = false;
        }

        /// <summary>
        /// Ring turns to bring the number to the indicator (shortest way, or always clockwise); the hands run to the
        /// new time with it. A click while things are still moving re-aims them from their current speed, so
        /// nothing stutters.
        /// </summary>
        private void ClickNumber(Ring ring, Number number)
        {
            var shortest = clickTurn == ClickTurn.Shortest;
            var current = number.baseAngle + ring.Rotation;
            float ringDelta;
            if (shortest)
            {
                ringDelta = Mathf.DeltaAngle(current, _indicatorAngle);
            }
            else
            {
                // Clockwise = decreasing angle. A number already at (or a hair past) the indicator only gets
                // nudged onto it, not sent round a full lap.
                var remaining = Mathf.Repeat(current - _indicatorAngle, 360f);
                ringDelta = remaining > 359.95f ? 360f - remaining : -remaining;
            }

            var target = SelectedMinutes(ring == _hourRing ? number : TargetOf(_hourRing),
                ring == _minuteRing ? number : TargetOf(_minuteRing));
            var from = CurrentHandMinutes();
            var handDelta = shortest ? ShortestMinutes(from, target) : ForwardMinutes(from, target);

            var ringSpeed = ring.Speed;
            var handSpeed = HandSpeed;
            if (!shortest)
            {
                // Clockwise only: something turning fast whose new stop is just ahead would have to brake to a
                // dead stop, so let it go round once more and settle (ring: one more lap; hands: 12 more hours).
                var estimate = ClickDuration(ringDelta, handDelta);
                if (ringSpeed < 0f && TooAbrupt(ringDelta, ringSpeed, estimate, 40f))
                    ringDelta -= 360f;
                var handsEstimate = estimate;
                foreach (var other in _rings)
                    if (other.animating && other != ring)
                        handsEstimate = Mathf.Max(handsEstimate, other.animDuration - other.animTime);
                if (handSpeed > 0f && TooAbrupt(handDelta, handSpeed, handsEstimate, 20f))
                    handDelta += 720f;
            }

            var wanted = ClickDuration(ringDelta, handDelta);
            if (Mathf.Abs(ringDelta) < .01f && Mathf.Abs(handDelta) < .01f && ringSpeed == 0f && handSpeed == 0f)
                wanted = 0f;

            // Shortest: carry the current speed whichever way it points; if the new stop is behind, the glide eases
            // off, turns round smoothly and comes back. Clockwise: never reverse, so only same-direction speed carries.
            var ringStart = shortest ? ringSpeed : SameDirection(ringSpeed, ringDelta);
            StartRingTurn(ring, number, ringDelta, ringStart, wanted,
                shortest ? 2f : GlideExponent(ringDelta, ringStart, wanted));

            // The hands stop together with the last ring still turning (so the gears stop with them).
            var handsWanted = 0f;
            foreach (var other in _rings)
                if (other.animating)
                    handsWanted = Mathf.Max(handsWanted, other.animDuration - other.animTime);
            var handStart = shortest ? handSpeed : SameDirection(handSpeed, handDelta);
            _handFrom = from;
            _handDistance = handDelta;
            _handStartSpeed = handStart;
            _handExponent = shortest ? 2f : GlideExponent(handDelta, handStart, handsWanted);
            _handTime = 0f;
            _handDuration = handsWanted;
            _handAnimating = true;
        }

        /// <summary>
        /// After a drag, glide the ring onto a number so it never rests between two (either direction).
        /// Let go while still turning and it keeps its momentum to the next number ahead instead of braking back.
        /// </summary>
        private void SnapAfterDrag(Ring ring, float releaseSpeed)
        {
            var speed = Mathf.Abs(releaseSpeed) >= flickSpeed ? releaseSpeed : 0f;
            Number target = null;
            var delta = 0f;
            if (speed == 0f)
            {
                target = NearestToIndicator(ring);
                delta = Mathf.DeltaAngle(target.baseAngle + ring.Rotation, _indicatorAngle);
            }
            else
            {
                // Numbers ahead in the direction of travel, nearest first; skip any too close to stop at gently.
                var direction = Mathf.Sign(speed);
                var best = float.MaxValue;
                foreach (var number in ring.numbers)
                {
                    var ahead = Mathf.Repeat(direction * (_indicatorAngle - number.baseAngle - ring.Rotation), 360f);
                    if (ahead < .01f) ahead += 360f;
                    if (TooAbrupt(direction * ahead, speed, SnapDuration(ahead), flickSpeed) || ahead >= best) continue;
                    best = ahead;
                    target = number;
                    delta = direction * ahead;
                }
                if (target == null)
                {
                    target = NearestToIndicator(ring);
                    delta = Mathf.DeltaAngle(target.baseAngle + ring.Rotation, _indicatorAngle);
                    speed = 0f;
                }
            }
            var startSpeed = SameDirection(speed, delta);
            var duration = SnapDuration(delta);
            StartRingTurn(ring, target, delta, startSpeed, duration, GlideExponent(delta, startSpeed, duration), true);
        }

        private float SnapDuration(float delta) =>
            Mathf.Clamp(Mathf.Abs(delta) / snapSpeed, snapMinDuration, Mathf.Max(snapMinDuration, snapMaxDuration));

        // ---------------------------------------------------------------- rings and gears

        private static void StartRingTurn(Ring ring, Number number, float distance, float startSpeed, float duration,
            float exponent, bool snap = false)
        {
            ring.animIsSnap = snap;
            ring.animTarget = number;
            ring.animDistance = distance;
            ring.animStartSpeed = startSpeed;
            ring.animExponent = exponent;
            ring.animApplied = 0f;
            ring.animTime = 0f;
            ring.animDuration = duration;
            ring.animating = true;
        }

        private void AnimateRing(Ring ring, float deltaTime)
        {
            if (!ring.animating) return;
            ring.animTime += deltaTime;
            var position = GlidePosition(ring.animDistance, ring.animStartSpeed, ring.animDuration, ring.animTime,
                ring.animExponent);
            Turn(ring, position - ring.animApplied);
            ring.animApplied = position;
            if (ring.animTime < ring.animDuration) return;
            ring.animating = false;
            onNumberAligned.Invoke(ring.layer.label, ring.animTarget.text);
            // A click reports the time when its hands stop; a drag settles here.
            if (ring.animIsSnap && !_handAnimating)
            {
                var minutes = Mathf.RoundToInt(SelectedMinutes()) % 720;
                onTimeSet.Invoke(minutes / 60, minutes % 60);
            }
        }

        /// <summary>Adds <paramref name="delta"/> degrees (positive = counter-clockwise) to a ring and its gears.</summary>
        private void Turn(Ring ring, float delta)
        {
            if (delta == 0f) return;
            ring.offset = Mathf.Repeat(ring.offset + delta, 360f);
            ApplyRingPose(ring);
            foreach (var gear in _gears)
            {
                gear.spin = Mathf.Repeat(gear.spin + gear.ratio * delta, 360f);
                ApplyGearPose(gear);
            }
            if (_gears.Count > 0) AddGearTravel(delta);
        }

        // ---------------------------------------------------------------- sound

        private AudioSource _clankSource;
        private float _handSpeed; // minute hand as drawn, degrees per second (smoothed)
        private float _lastShownMinuteAngle;
        private bool _handSpeedPrimed;
        private float _lastClockMotionTime = -10f; // last time a hand, ring or gear actually turned
        private AudioClip _clankLoop, _clankLoopFrom;
        private float _clankLoopLength;
        private bool _clankWrapsByHand; // the clip could not be cut: loop its first part by seeking back

        /// <summary>Something on the clock is turning right now (hands, rings, gears, or the open / close motion).</summary>
        private bool ClockTurning => _open != _revealTarget || Time.unscaledTime - _lastClockMotionTime < .06f;

        /// <summary>0..1: how fast the hands turn, relative to <see cref="clankFullSpeed"/>.</summary>
        private float HandSpeed01 => Mathf.Clamp01(_handSpeed / Mathf.Max(clankFullSpeed, 1f));

        /// <summary>
        /// Loudness right now: the open / close level while the clock opens or folds, otherwise by how fast the hands
        /// turn (square root: loudness is heard roughly logarithmically, so middling speeds are already clear).
        /// </summary>
        private float ClankVolumeNow
        {
            get
            {
                var turning = gearClankVolume * Mathf.Lerp(clankSlowVolume, 1f, Mathf.Sqrt(HandSpeed01));
                return _open != _revealTarget ? Mathf.Max(openCloseVolume, turning) : turning;
            }
        }

        /// <summary>
        /// One voice, looping the first <see cref="clankMaxDuration"/> seconds of the sound without a gap while the
        /// clock turns; louder and a bit higher the faster the hands go; fades out as soon as nothing turns.
        /// </summary>
        private void UpdateClankAudio(float deltaTime)
        {
            var turning = ClockTurning && clankSound != null;
            if (turning && (_clankSource == null || !_clankSource.isPlaying))
                StartClankLoop();
            if (_clankSource == null || !_clankSource.isPlaying) return;

            var fade = Mathf.Max(clankStopFade, .01f);
            var target = turning ? ClankVolumeNow : 0f;
            // Same rate both ways, so a pause shorter than the fade just dips instead of cutting out.
            _clankSource.volume = Mathf.MoveTowards(_clankSource.volume, target,
                Mathf.Max(gearClankVolume, openCloseVolume) * deltaTime / fade);
            _clankSource.pitch = 1f + clankFastPitch * HandSpeed01;
            if (!turning && _clankSource.volume <= 0f)
            {
                _clankSource.Stop();
                return;
            }
            if (_clankWrapsByHand)
            {
                // Still on the uncut sound: switch to the gap-free loop as soon as its samples are loaded.
                if (clankSound.loadState == AudioDataLoadState.Loaded && ClankLoopClip() is var loop && loop != clankSound)
                {
                    _clankSource.clip = loop;
                    _clankSource.Play();
                }
                else if (_clankSource.time >= _clankLoopLength) _clankSource.time = 0f;
            }
        }

        private void StartClankLoop()
        {
            if (!isActiveAndEnabled) return;
            if (_clankSource == null)
            {
                _clankSource = gameObject.AddComponent<AudioSource>();
                _clankSource.playOnAwake = false;
                _clankSource.spatialBlend = 0f; // UI sound
            }
            var clip = ClankLoopClip();
            if (clip == null) return;
            _clankSource.clip = clip;
            _clankSource.loop = true;
            _clankSource.volume = ClankVolumeNow;
            _clankSource.pitch = 1f + clankFastPitch * HandSpeed01;
            _clankSource.Play();
        }

        /// <summary>
        /// The first <see cref="clankMaxDuration"/> seconds of <see cref="clankSound"/> as their own clip, without the
        /// silence before and after the sound (so the loop has no pause), with a few milliseconds faded at both ends so
        /// the loop seam does not click. Falls back to the whole clip (looped back
        /// by seeking) if its samples cannot be read.
        /// </summary>
        private AudioClip ClankLoopClip()
        {
            var length = Mathf.Min(clankMaxDuration, clankSound.length);
            // A fallback (samples not loaded yet) is tried again later; a cut loop is kept.
            if (_clankLoopFrom == clankSound && Mathf.Approximately(_clankLoopLength, length) && _clankLoop != null)
                return _clankLoop;

            _clankLoopFrom = clankSound;
            _clankLoopLength = length;
            if (_clankLoop != null) Destroy(_clankLoop);
            _clankLoop = null;
            _clankWrapsByHand = true;
            if (clankSound.loadState != AudioDataLoadState.Loaded) clankSound.LoadAudioData();
            if (clankSound.loadType != AudioClipLoadType.DecompressOnLoad ||
                clankSound.loadState != AudioDataLoadState.Loaded)
                return clankSound;

            var channels = clankSound.channels;
            var frequency = clankSound.frequency;
            var all = new float[clankSound.samples * channels];
            if (!clankSound.GetData(all, 0)) return clankSound;
            // Skip the silence before the first sound (a few ms early), so turning sounds at once.
            var peak = 0f;
            foreach (var sample in all) peak = Mathf.Max(peak, Mathf.Abs(sample));
            var first = 0;
            while (first < clankSound.samples && Mathf.Abs(all[first * channels]) < peak * .05f) first++;
            first = Mathf.Max(0, first - Mathf.RoundToInt(.005f * frequency));
            // ...and the silence after the last one: looped, it would be a pause on every turn.
            var last = clankSound.samples - 1;
            while (last > first && Mathf.Abs(all[last * channels]) < peak * .05f) last--;
            last = Mathf.Min(clankSound.samples - 1, last + Mathf.RoundToInt(.01f * frequency));
            var frames = Mathf.Clamp(Mathf.FloorToInt(length * frequency), 1, last - first + 1);
            var data = new float[frames * channels];
            System.Array.Copy(all, first * channels, data, 0, data.Length);
            var edge = Mathf.Min(frames / 4, Mathf.RoundToInt(.006f * frequency));
            for (var i = 0; i < edge; i++)
            {
                var gain = (float)i / edge;
                for (var c = 0; c < channels; c++)
                {
                    data[i * channels + c] *= gain;
                    data[(frames - 1 - i) * channels + c] *= gain;
                }
            }
            _clankLoop = AudioClip.Create(clankSound.name + " (vòng lặp)", frames, channels, frequency, false);
            _clankLoop.SetData(data, 0);
            _clankWrapsByHand = false;
            return _clankLoop;
        }

        private void OnDestroy()
        {
            if (_clankLoop != null) Destroy(_clankLoop);
        }

        /// <summary>
        /// How fast the minute hand turns: following the rings or opening. The extra spin while the TIME button is
        /// hovered is left out, so hovering makes no sound.
        /// </summary>
        private void TrackHandSpeed(float deltaTime)
        {
            var shown = _minuteAngle;
            if (_handSpeedPrimed && deltaTime > 0f)
            {
                var speed = Mathf.Abs(Mathf.DeltaAngle(_lastShownMinuteAngle, shown)) / deltaTime;
                _handSpeed = Mathf.Lerp(_handSpeed, speed, 1f - Mathf.Exp(-12f * deltaTime));
                if (speed > 3f) _lastClockMotionTime = Time.unscaledTime; // ignores the hands' last tiny settling drift
            }
            _lastShownMinuteAngle = shown;
            _handSpeedPrimed = true;
        }

        /// <summary>The rings or the hover drive turned the gears: the clock is turning, so the sound keeps going.</summary>
        private void AddGearTravel(float degrees)
        {
            if (Mathf.Abs(degrees) > .0001f) _lastClockMotionTime = Time.unscaledTime;
        }

        /// <summary>How far the opening / closing turn is driving the gears right now (see <see cref="ApplyGearPose"/>).</summary>
        private float RevealGearDrive => gearsFollowReveal && _hourRing != null ? RevealTurn(_hourRing.reveal) : 0f;

        /// <summary>Ring image and its numbers at the ring's rotation (plus any reveal turn); numbers stay upright.</summary>
        private void ApplyRingPose(Ring ring)
        {
            var rotation = ring.Rotation + RevealTurn(ring.reveal);
            ring.image.localRotation = Quaternion.Euler(0f, 0f, rotation);
            foreach (var number in ring.numbers)
            {
                Place(number.reveal, number.image,
                    RecoilClockImagePreview.GetAttachedCenter(ring.layer, number.layer.position, rotation, _center));
                number.image.localRotation = Quaternion.Euler(0f, 0f,
                    number.layer.rotation + (number.layer.keepUpright ? 0f : rotation));
            }
        }

        /// <summary>Gears are driven by the rings, including the hour ring's turn while it comes out or tucks away.</summary>
        private void ApplyGearPose(Gear gear)
        {
            var revealDrive = gearsFollowReveal && _hourRing != null ? RevealTurn(_hourRing.reveal) : 0f;
            // The hover drive turns them as if the hour ring went clockwise (negative rotation).
            gear.image.localRotation = Quaternion.Euler(0f, 0f,
                gear.baseRotation + gear.spin + gear.ratio * (revealDrive - _hoverGearDrive));
        }

        /// <summary>Ring value at the indicator, interpolated between neighbouring numbers (e.g. 2.5 hours).</summary>
        private float ValueAtIndicator(Ring ring)
        {
            var atIndicator = _indicatorAngle - ring.Rotation; // in the ring's unrotated frame
            var count = ring.numbers.Count;
            for (var k = 0; k < count; k++)
            {
                var from = ring.numbers[k];
                var to = ring.numbers[(k + 1) % count];
                var span = Mathf.Repeat(from.baseAngle - to.baseAngle, 360f); // clockwise from -> to
                var along = Mathf.Repeat(from.baseAngle - atIndicator, 360f);
                if (along > span) continue;
                // A number resting at the indicator reads as exactly that number. Without this, float noise on
                // the wrap ("0" a hair past the indicator) reads 59.99... minutes, i.e. the next hour.
                const float onNumber = .01f;
                if (along <= onNumber) return from.value;
                if (span - along <= onNumber) return Mathf.Repeat(to.value, ring.cycle);
                return Mathf.Repeat(from.value + ring.step * (along / span), ring.cycle);
            }
            return 0f;
        }

        private static Number TargetOf(Ring ring) => ring != null && ring.animating ? ring.animTarget : null;

        // ---------------------------------------------------------------- hands

        /// <summary>Selected time in minutes on a 12-hour dial, read from what sits at the indicator.</summary>
        private float SelectedMinutes(Number hourTarget = null, Number minuteTarget = null)
        {
            var hours = hourTarget != null ? hourTarget.value : _hourRing != null ? ValueAtIndicator(_hourRing) : 0f;
            var minutes = minuteTarget != null ? minuteTarget.value : _minuteRing != null ? ValueAtIndicator(_minuteRing) : 0f;
            return Mathf.Repeat(hours * 60f + minutes, 720f);
        }

        private void UpdateHands(float deltaTime)
        {
            if (_handAnimating)
            {
                _handTime += deltaTime;
                SetHands(_handFrom + GlidePosition(_handDistance, _handStartSpeed, _handDuration, _handTime, _handExponent));
                if (_handTime < _handDuration) return;
                _handAnimating = false;
                var minutes = Mathf.RoundToInt(Mathf.Repeat(_handFrom + _handDistance, 720f)) % 720;
                onTimeSet.Invoke(minutes / 60, minutes % 60);
                return;
            }

            // Not running a click: follow what the rings select (dragging, snapping). A ring still turning to a
            // number counts as already there, so the hands never chase it backwards mid-turn.
            var target = SelectedMinutes(TargetOf(_hourRing), TargetOf(_minuteRing));
            var follow = 1f - Mathf.Exp(-handFollowSharpness * deltaTime);
            _minuteAngle += Mathf.DeltaAngle(_minuteAngle, Mathf.Repeat(target, 60f) * 6f) * follow;
            _hourAngle += Mathf.DeltaAngle(_hourAngle, target * .5f) * follow;
            ApplyHands();
        }

        /// <summary>Real clock rule: the minute hand turns 6°/minute, the hour hand 0.5°/minute.</summary>
        private void SetHands(float minutes)
        {
            _minuteAngle = Mathf.Repeat(minutes, 60f) * 6f;
            _hourAngle = Mathf.Repeat(minutes, 720f) * .5f;
            ApplyHands();
        }

        private void ApplyHands()
        {
            // Clockwise degrees from 12 -> RectTransform z rotation (counter-clockwise positive). The hover spin is
            // drawn on top and always ends on a whole turn, so it never changes the time shown.
            if (_minuteHand != null)
                _minuteHand.localRotation = Quaternion.Euler(0f, 0f, handTwelveRotation - _minuteAngle - _hoverMinuteSpin.angle);
            if (_hourHand != null)
                _hourHand.localRotation = Quaternion.Euler(0f, 0f, handTwelveRotation - _hourAngle - _hoverHourSpin.angle);
        }

        /// <summary>The time the hands show now (minutes on a 12-hour dial), consistent with both hands.</summary>
        private float CurrentHandMinutes()
        {
            var minutes = Mathf.Repeat(_minuteAngle, 360f) / 6f;
            var hourHandMinutes = Mathf.Repeat(_hourAngle, 360f) * 2f;
            var wholeHours = Mathf.Round((hourHandMinutes - minutes) / 60f);
            return Mathf.Repeat(wholeHours * 60f + minutes, 720f);
        }

        private static float ForwardMinutes(float from, float to)
        {
            var delta = Mathf.Repeat(to - from, 720f);
            return delta > 719.99f ? 0f : delta;
        }

        private static float ShortestMinutes(float from, float to) => Mathf.Repeat(to - from + 360f, 720f) - 360f;

        // Glide over a fixed duration T that starts at the current speed and comes to rest exactly at `distance`:
        //   speed(s) = startSpeed * (1 - s)^n  +  fresh * 6 s (1 - s),   s = t / T
        // The first term lets the carried-over speed fade out; the second is the usual slow-start/slow-stop bump
        // (from rest the glide is a plain smoothstep). Speed is continuous at a re-aim and T is never cut short.
        // With n = 2 a speed pointing away from the target (or too fast for it) swings a little past and comes
        // back smoothly. GlideExponent raises n just enough that the fading speed never covers more than
        // `distance`, which forbids that: no overshoot, never reversing (used where turning back is not allowed).

        private static float GlideExponent(float distance, float startSpeed, float duration) =>
            distance * startSpeed > 0f && duration > 0f ? Mathf.Max(2f, startSpeed * duration / distance - 1f) : 2f;

        private static float GlidePosition(float distance, float startSpeed, float duration, float time, float n)
        {
            if (duration <= 0f || time >= duration) return distance;
            var s = time / duration;
            var carried = startSpeed / (n + 1f);
            var fresh = distance / duration - carried;
            return duration * (carried * (1f - Mathf.Pow(1f - s, n + 1f)) + fresh * (3f * s * s - 2f * s * s * s));
        }

        private static float GlideSpeed(float distance, float startSpeed, float duration, float time, float n)
        {
            if (duration <= 0f || time >= duration) return 0f;
            var s = time / duration;
            var fresh = distance / duration - startSpeed / (n + 1f);
            return startSpeed * Mathf.Pow(1f - s, n) + fresh * 6f * s * (1f - s);
        }

        /// <summary>How long a click turn takes: slow enough to watch, within the min/max click duration.</summary>
        private float ClickDuration(float ringDelta, float handDelta) =>
            Mathf.Clamp(
                Mathf.Max(Mathf.Abs(ringDelta) / clickRingSpeed, Mathf.Abs(handDelta) * 6f / clickMinuteHandSpeed),
                minClickDuration, Mathf.Max(minClickDuration, maxClickDuration));

        /// <summary>
        /// True when something moving at a noticeable <paramref name="speed"/> would have to brake much harder than
        /// a normal glide (or stop dead / reverse) to stop after <paramref name="distance"/> within
        /// <paramref name="duration"/>. Slow movers may just stop; nobody notices.
        /// </summary>
        private static bool TooAbrupt(float distance, float speed, float duration, float noticeableSpeed)
        {
            if (Mathf.Abs(speed) < noticeableSpeed) return false;
            if (speed * distance <= 0f) return true;
            return speed * duration / distance - 1f > MaxBrakeExponent;
        }

        // Glide exponent 2 is the normal gentle stop; past this the start of the stop gets visibly harsh.
        private const float MaxBrakeExponent = 8f;

        /// <summary>Carries the current speed over only if it already heads toward the new target.</summary>
        private static float SameDirection(float speed, float distance) => speed * distance > 0f ? speed : 0f;

        // ---------------------------------------------------------------- tucked behind the face

        private float _revealEased; // eased _reveal, shared by every reveal item

        private void InitReveal(RecoilClockImageLayer[] layers, RectTransform root)
        {
            SetBehindFace(false); // put any previous items back in layout order before re-reading the images
            _revealItems.Clear();
            _behindFace = false;
            _layoutOrder.Clear();
            for (var i = 0; i < root.childCount; i++)
                _layoutOrder.Add(root.GetChild(i));

            var faceIndex = IndexOf(layers, RecoilClockImagePreview.FaceLayerLabel);
            _face = faceIndex >= 0 ? FindImage(root, layers[faceIndex].label) : null;
            if (_face == null)
            {
                if (revealLayers.Length > 0)
                    Debug.LogWarning("[RecoilClockDial] Không tìm thấy mặt đồng hồ đang hiển thị; các lớp luôn hiện.", this);
                _reveal = _revealTarget = _revealEased = _open = _grow = 1f;
                _collapseButton = null;
                return;
            }
            var faceSize = DrawnSize(layers[faceIndex]);
            _faceRadius = .45f * Mathf.Min(faceSize.x, faceSize.y);
            _clockRadius = _faceRadius;
            foreach (var ring in _rings)
                _clockRadius = Mathf.Max(_clockRadius, ring.radius + ring.halfWidth); // the band the numbers sit in
            InitCollapse(layers, root);

            foreach (var entry in revealLayers)
            {
                var index = IndexOf(layers, entry.label);
                var image = index >= 0 ? FindImage(root, entry.label) : null;
                if (image == null)
                {
                    Debug.LogWarning($"[RecoilClockDial] Không tìm thấy lớp \"{entry.label}\" để ẩn sau mặt đồng hồ.", this);
                    continue;
                }
                // Shrink toward the face center until the whole image fits inside the face.
                var layer = layers[index];
                var center = _center + RecoilClockImagePreview.Rotate((layer.position - _center) * layer.scale, layer.rotation);
                var drawn = DrawnSize(layer);
                var extent = (center - _center).magnitude + .5f * Mathf.Max(drawn.x, drawn.y);
                // Round art (rings, gears) fills the short side of its box: that much counts as clock, not empty space.
                _clockRadius = Mathf.Max(_clockRadius, (center - _center).magnitude + .5f * Mathf.Min(drawn.x, drawn.y));
                var hiddenScale = Mathf.Clamp(_faceRadius * .9f / Mathf.Max(extent, .001f), .02f, 1f);
                var item = AddRevealItem(image, image.anchoredPosition, hiddenScale, entry.turn);

                var ring = _rings.Find(r => r.layer == layer);
                if (ring != null)
                {
                    ring.reveal = item;
                    foreach (var number in ring.numbers) // numbers ride their ring: same shrink and turn
                        number.reveal = AddRevealItem(number.image, number.image.anchoredPosition, hiddenScale, 0f);
                }
                else if (!_gears.Exists(g => g.image == image))
                {
                    // Anything else (e.g. the indicator) turns around the face center by itself.
                    item.turnsItself = true;
                    item.baseRotation = image.localEulerAngles.z;
                }
            }
            _revealItems.Sort((a, b) => a.siblingIndex.CompareTo(b.siblingIndex));

            if (!_revealInitialized)
            {
                _open = _revealTarget = startHidden ? 0f : 1f;
                _revealInitialized = true;
            }
            SplitOpenProgress();
            ApplyGrow();
            ApplyReveal();
        }

        // ---------------------------------------------------------------- folded into the TIME button

        private bool HasCollapse => _collapseButton != null;

        /// <summary>
        /// Finds the TIME button layer and where its round icon is. While closed, the whole clock is scaled down so the
        /// face exactly covers that icon; opening grows it back to its place.
        /// </summary>
        private void InitCollapse(RecoilClockImageLayer[] layers, RectTransform root)
        {
            _collapseButton = null;
            if (string.IsNullOrEmpty(collapseButtonLabel)) return;
            var index = IndexOf(layers, collapseButtonLabel);
            var image = index >= 0 ? FindImage(root, collapseButtonLabel) : null;
            if (image == null) return; // no button: the clock just reveals as before
            if (!_rootBaseCaptured)
            {
                _rootBasePosition = root.anchoredPosition;
                _rootBaseScale = root.localScale;
                _rootBaseCaptured = true;
            }

            var layer = layers[index];
            _collapseButton = image;
            _collapseButtonGraphic = image.GetComponent<Graphic>();
            _collapseButtonColor = _collapseButtonGraphic != null ? _collapseButtonGraphic.color : Color.white;
            _collapseButtonBasePosition = image.anchoredPosition;
            // The button as drawn when the clock is at rest (clock space), and the icon's spot on it.
            _collapseButtonRotation = layer.rotation;
            _collapseButtonCenter = _center + RecoilClockImagePreview.Rotate((layer.position - _center) * layer.scale, layer.rotation);
            var drawn = DrawnSize(layer);
            _collapseButtonHalfSize = drawn * .5f;
            _collapseIconPoint = _collapseButtonCenter + RecoilClockImagePreview.Rotate(
                new Vector2((collapseIconCenter.x - .5f) * drawn.x, (.5f - collapseIconCenter.y) * drawn.y), layer.rotation);
            _collapsedScale = Mathf.Clamp(collapseIconRadius * drawn.y / Mathf.Max(_faceRadius, .001f), .02f, 1f);
        }

        /// <summary>Splits the open progress into the face growing out of the button, then the rings coming out.</summary>
        private void SplitOpenProgress()
        {
            if (!HasCollapse)
            {
                _grow = 1f;
                _reveal = _open;
                return;
            }
            // Face and rings start together from the button and finish within the same open time.
            var elapsed = _open * OpenDuration;
            _grow = Mathf.Clamp01(elapsed / Mathf.Max(growDuration, .001f));
            _reveal = Mathf.Clamp01(elapsed / Mathf.Max(revealDuration, .001f));
        }

        /// <summary>Seconds to go from closed to open: the longer of the face's growth and the rings' reveal.</summary>
        private float OpenDuration => HasCollapse ? Mathf.Max(growDuration, revealDuration) : revealDuration;

        private float GrowEased => _grow * _grow * (3f - 2f * _grow);

        /// <summary>How much the hover pose applies now: 1 = hovered while closed, fading out as the clock opens.</summary>
        private float HoverWeight()
        {
            var shown = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_grow / .6f));
            return Mathf.SmoothStep(0f, 1f, _hoverAmount) * shown;
        }

        /// <summary>
        /// Current pose of the clock root: its scale, and where the face center sits (clock space at rest). Closed, the
        /// face sits on the button's icon at the folded size, popped up by <see cref="hoverFaceScale"/> while hovered;
        /// opening grows it back to its own place and size.
        /// </summary>
        private void GrowPose(out float scale, out Vector2 faceAt)
        {
            var eased = GrowEased;
            var folded = Mathf.Min(1f, _collapsedScale * Mathf.Lerp(1f, hoverFaceScale, HoverWeight()));
            scale = Mathf.Lerp(folded, 1f, eased);
            faceAt = Vector2.Lerp(_collapseIconPoint, _center, eased);
        }

        /// <summary>
        /// Scales the whole clock about its root and slides it so the face center goes from the button's icon to its
        /// own place; the button itself stays still (its share of the root's scale and slide is undone) and fades.
        /// </summary>
        private void ApplyGrow()
        {
            if (!HasCollapse || _clock == null || _clock.ClockRoot == null) return;
            var root = _clock.ClockRoot;
            GrowPose(out var scale, out var faceAt);
            var offset = faceAt - _center * scale;
            root.localScale = _rootBaseScale * scale;
            root.anchoredPosition = _rootBasePosition + Vector2.Scale(offset, _rootBaseScale);
            _collapseButton.localScale = Vector3.one / scale;
            _collapseButton.anchoredPosition = (_collapseButtonBasePosition - offset) / scale;
            if (_collapseButtonGraphic == null) return;
            var shown = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_grow / .6f));
            var color = _collapseButtonColor;
            color.a *= shown;
            _collapseButtonGraphic.color = color;
        }

        /// <summary>
        /// Hovering the TIME button while closed: the folded face pops up around the icon, the hands spin and the gears
        /// turn. Leaving: the face folds back, the gears slow to a stop and each hand finishes its turn onto the time.
        /// </summary>
        private void UpdateCollapseHover(float deltaTime)
        {
            if (!HasCollapse) return;
            var over = false;
            var pointer = Pointer.current;
            if (pointer != null && _grow < .05f && TryGetClockPoint(pointer.position.ReadValue(), out var point))
                // Entered on the button; once popped up, the pointer may also rest on the bigger face.
                over = OnCollapseButton(point) || _hoverAmount > 0f && (point - _center).magnitude <= _faceRadius;
            var next = Mathf.MoveTowards(_hoverAmount, over ? 1f : 0f, deltaTime / Mathf.Max(hoverDuration, .01f));
            if (next != _hoverAmount)
            {
                _hoverAmount = next;
                ApplyGrow();
            }

            if (over)
            {
                _hoverMinuteSpin.Run(hoverHandSpeed, deltaTime);
                _hoverHourSpin.Run(hoverHandSpeed / 12f, deltaTime); // real clock ratio
            }
            else
            {
                _hoverMinuteSpin.Settle(deltaTime);
                _hoverHourSpin.Settle(deltaTime);
            }

            _hoverGearSpeedNow = Mathf.Lerp(_hoverGearSpeedNow, over ? hoverGearSpeed : 0f, 1f - Mathf.Exp(-6f * deltaTime));
            if (Mathf.Abs(_hoverGearSpeedNow) < .01f)
            {
                _hoverGearSpeedNow = 0f;
                return;
            }
            var step = _hoverGearSpeedNow * deltaTime;
            _hoverGearDrive += step;
            foreach (var gear in _gears)
                ApplyGearPose(gear);
            // Hover turns the gears silently (no AddGearTravel): hovering makes no sound.
        }

        /// <summary>True when <paramref name="point"/> (the clock root's space right now) is on the visible TIME button.</summary>
        private bool OnCollapseButton(Vector2 point)
        {
            if (!HasCollapse || _grow >= .999f) return false;
            // The pointer, from the root's space to clock space at rest, through the root's current pose (the button
            // itself never moves there).
            GrowPose(out var scale, out var faceAt);
            var atRest = faceAt + (point - _center) * scale;
            var local = RecoilClockImagePreview.Rotate(atRest - _collapseButtonCenter, -_collapseButtonRotation);
            return Mathf.Abs(local.x) <= _collapseButtonHalfSize.x && Mathf.Abs(local.y) <= _collapseButtonHalfSize.y;
        }

        /// <summary>Extra clockwise turn of one hand while the TIME button is hovered. Only ever turns forward.</summary>
        private sealed class HandSpin
        {
            private const float MinSettle = .25f, MaxSettle = .8f;

            public float angle; // degrees, clockwise; 0 = the hand shows the real time
            private float _speed; // degrees per second
            private bool _settling;
            private float _from, _distance, _startSpeed, _duration, _time;

            /// <summary>Spins up toward <paramref name="targetSpeed"/>.</summary>
            public void Run(float targetSpeed, float deltaTime)
            {
                _settling = false;
                _speed = Mathf.Lerp(_speed, targetSpeed, 1f - Mathf.Exp(-8f * deltaTime));
                angle = Mathf.Repeat(angle + _speed * deltaTime, 360f);
            }

            /// <summary>Keeps turning forward, slowing down, and stops exactly on a whole turn.</summary>
            public void Settle(float deltaTime)
            {
                if (angle == 0f && _speed == 0f) return;
                if (!_settling)
                {
                    _settling = true;
                    _from = angle;
                    _startSpeed = Mathf.Max(0f, _speed);
                    _distance = 360f - Mathf.Repeat(angle, 360f);
                    // Cubic with the current speed at the start and none at the end. It never turns backwards while
                    // startSpeed * duration <= 3 * distance; too close to stop smoothly -> one more turn.
                    if (_startSpeed > 0f && 3f * _distance / _startSpeed < MinSettle) _distance += 360f;
                    _duration = _startSpeed > 0f
                        ? Mathf.Min(Mathf.Clamp(2f * _distance / _startSpeed, MinSettle, MaxSettle), 3f * _distance / _startSpeed)
                        : MaxSettle;
                    _time = 0f;
                }
                _time += deltaTime;
                var t = Mathf.Clamp01(_time / _duration);
                var t2 = t * t;
                var t3 = t2 * t;
                var carry = _startSpeed * _duration;
                angle = _from + carry * (t3 - 2f * t2 + t) + _distance * (3f * t2 - 2f * t3);
                _speed = (carry * (3f * t2 - 4f * t + 1f) + _distance * (6f * t - 6f * t2)) / _duration;
                if (t < 1f) return;
                angle = 0f;
                _speed = 0f;
                _settling = false;
            }
        }

        private RevealItem AddRevealItem(RectTransform image, Vector2 basePosition, float hiddenScale, float turn)
        {
            var graphic = image.GetComponent<Graphic>();
            var item = new RevealItem
            {
                image = image,
                graphic = graphic,
                color = graphic != null ? graphic.color : Color.white,
                basePosition = basePosition,
                hiddenScale = hiddenScale,
                turn = turn,
                siblingIndex = image.GetSiblingIndex()
            };
            _revealItems.Add(item);
            return item;
        }

        private void UpdateReveal(float deltaTime)
        {
            if (_open == _revealTarget) return;
            _open = Mathf.MoveTowards(_open, _revealTarget, deltaTime / OpenDuration);
            var driveBefore = RevealGearDrive;
            SplitOpenProgress();
            ApplyGrow();
            ApplyReveal();
            if (_gears.Count > 0) AddGearTravel(RevealGearDrive - driveBefore); // the gears clank while the rings turn out
        }

        private void ApplyReveal()
        {
            _revealEased = 1f - (1f - _reveal) * (1f - _reveal) * (1f - _reveal); // quick out from behind, gentle settle
            var alpha = Mathf.Clamp01(_reveal / .3f);
            foreach (var item in _revealItems)
            {
                if (item.image == null) continue;
                var scale = Mathf.Lerp(item.hiddenScale, 1f, _revealEased);
                item.image.localScale = new Vector3(scale, scale, 1f);
                item.image.anchoredPosition = _center + (item.basePosition - _center) * scale;
                if (item.turnsItself)
                    item.image.localRotation = Quaternion.Euler(0f, 0f, item.baseRotation + RevealTurn(item));
                if (item.graphic == null) continue;
                var color = item.color;
                color.a *= alpha;
                item.graphic.color = color;
            }
            // Rings (with their numbers) and gears apply the reveal turn through their own pose code.
            foreach (var ring in _rings)
                ApplyRingPose(ring);
            foreach (var gear in _gears)
                ApplyGearPose(gear);
            // Behind the face while tucked away or moving; back in the saved layout order once fully out.
            SetBehindFace(_reveal < 1f);
        }

        /// <summary>Extra rotation of a reveal item right now: its full turn when tucked away, 0 when fully out.</summary>
        private float RevealTurn(RevealItem item) => item == null ? 0f : item.turn * (1f - _revealEased);

        /// <summary>Moves an image the dial positions itself, keeping its reveal shrink toward the face center.</summary>
        private void Place(RevealItem item, RectTransform image, Vector2 basePosition)
        {
            if (item == null)
            {
                image.anchoredPosition = basePosition;
                return;
            }
            item.basePosition = basePosition;
            image.anchoredPosition = _center + (basePosition - _center) * Mathf.Lerp(item.hiddenScale, 1f, _revealEased);
        }

        private void SetBehindFace(bool behind)
        {
            if (_behindFace == behind || _face == null) return;
            _behindFace = behind;
            if (!behind)
            {
                // Re-apply the whole saved order (moving items back one by one would land them off by the
                // positions the face and the others shifted).
                var index = 0;
                foreach (var child in _layoutOrder)
                    if (child != null) child.SetSiblingIndex(index++);
                return;
            }
            foreach (var item in _revealItems) // ascending layout order
                if (item.image != null && item.image.GetSiblingIndex() > _face.GetSiblingIndex())
                    item.image.SetSiblingIndex(_face.GetSiblingIndex()); // slots in just below the face, in order
        }

        /// <summary>Size the sprite is actually drawn at: fitted into the layer rect with its aspect kept.</summary>
        private static Vector2 DrawnSize(RecoilClockImageLayer layer)
        {
            var box = layer.size * layer.scale;
            var sprite = layer.sprite;
            if (sprite == null || sprite.rect.height <= 0f || box.y <= 0f) return box;
            var aspect = sprite.rect.width / sprite.rect.height;
            return box.x / box.y > aspect ? new Vector2(box.y * aspect, box.y) : new Vector2(box.x, box.x / aspect);
        }

        // ---------------------------------------------------------------- helpers

        private bool TryHit(Vector2 point, out Ring hitRing, out Number hitNumber)
        {
            foreach (var ring in _rings)
            {
                var rotation = ring.Rotation;
                foreach (var number in ring.numbers)
                {
                    var center = RecoilClockImagePreview.GetAttachedCenter(ring.layer, number.layer.position, rotation, _center);
                    var half = number.layer.size * (number.layer.scale * ring.layer.scale) * .5f;
                    var turn = number.layer.rotation + (number.layer.keepUpright ? 0f : rotation);
                    var local = RecoilClockImagePreview.Rotate(point - center, -turn);
                    if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y)
                    {
                        hitRing = ring;
                        hitNumber = number;
                        return true;
                    }
                }
            }

            hitRing = null;
            hitNumber = null;
            var radius = (point - _center).magnitude;
            var best = float.MaxValue;
            foreach (var ring in _rings)
            {
                var distance = Mathf.Abs(radius - ring.radius);
                if (distance > ring.halfWidth || distance >= best) continue;
                best = distance;
                hitRing = ring;
            }
            return hitRing != null;
        }

        private Number NearestToIndicator(Ring ring)
        {
            Number nearest = null;
            var best = float.MaxValue;
            foreach (var number in ring.numbers)
            {
                var distance = Mathf.Abs(Mathf.DeltaAngle(number.baseAngle + ring.Rotation, _indicatorAngle));
                if (distance >= best) continue;
                best = distance;
                nearest = number;
            }
            return nearest;
        }

        private float ComputeIndicatorAngle(RecoilClockImageLayer[] layers)
        {
            var direction = Vector2.right;
            var index = IndexOf(layers, indicatorLabel);
            if (index >= 0)
            {
                var layer = layers[index];
                var fromCenter = RecoilClockImagePreview.Rotate((layer.position - _center) * layer.scale, layer.rotation);
                if (fromCenter.sqrMagnitude > 1e-4f) direction = fromCenter;
            }
            else
            {
                Debug.LogWarning($"[RecoilClockDial] Không tìm thấy chỉ báo \"{indicatorLabel}\"; dùng hướng ngang bên phải.", this);
            }
            if (horizontalIndicator)
                return direction.x >= 0f ? 0f : 180f;
            return Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        }

        private bool TryGetClockPoint(Vector2 screen, out Vector2 clockPoint) =>
            // The clock root is centered with a centered pivot, so its local space is the layout's clock space.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_clock.ClockRoot, screen, _canvasCamera, out clockPoint);

        private float AngleOf(Vector2 point) =>
            Mathf.Atan2(point.y - _center.y, point.x - _center.x) * Mathf.Rad2Deg;

        private static int IndexOf(RecoilClockImageLayer[] layers, string label)
        {
            for (var i = 0; i < layers.Length; i++)
                if (layers[i] != null && layers[i].label == label)
                    return i;
            return -1;
        }

        private static RectTransform FindImage(Transform root, string label)
        {
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == label) return child as RectTransform;
            }
            return null;
        }

        private static string NumberText(string label, string ringLabel)
        {
            var prefix = ringLabel + RecoilClockImagePreview.NumberLabelSeparator;
            return label.StartsWith(prefix, StringComparison.Ordinal) ? label.Substring(prefix.Length) : label;
        }
    }
}
