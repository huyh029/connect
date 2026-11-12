using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class TiktokEventTestPopup : MonoBehaviour
{
    public static bool RandomTeamIfUnassigned
    {
        get => PlayerPrefs.GetInt("TikTok.RandomTeamIfUnassigned", 0) != 0;
        set { PlayerPrefs.SetInt("TikTok.RandomTeamIfUnassigned", value ? 1 : 0); PlayerPrefs.Save(); }
    }

    private void OnGUI()
    {
        if (SettingPopup == null || !SettingPopup.activeInHierarchy) return;
        int previousDepth = GUI.depth;
        GUI.depth = -200;
        var rect = new Rect(Screen.width - 335, 15, 320, 118);
        GUI.Box(rect, GUIContent.none);
        bool selected = GUI.Toggle(new Rect(rect.x + 10, rect.y + 8, 300, 28),
            RandomTeamIfUnassigned, "Random phe nếu chưa chọn phe");
        if (selected != RandomTeamIfUnassigned) RandomTeamIfUnassigned = selected;
        var wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && randomGiftSimulation == null;
        if (GUI.Button(new Rect(rect.x + 10, rect.y + 42, 300, 32), "Giả lập 1.000 người tặng quà random"))
            StartRandomGiftSimulation();
        GUI.enabled = wasEnabled;
        GUI.Label(new Rect(rect.x + 10, rect.y + 80, 300, 28), randomGiftStatus);
        GUI.depth = previousDepth;
    }
    private Coroutine randomGiftSimulation;
    private string randomGiftStatus = "Mỗi người 1 quà, phe ngẫu nhiên.";

    private void StartRandomGiftSimulation()
    {
        if (randomGiftSimulation != null) return;
        List<TikTokArmyRule> gifts;
        try
        {
            var path = Path.Combine(Application.streamingAssetsPath, "tiktok_army_settings.csv");
            var settings = RecoilPathfinding.RecoilSettingsCsv.ReadTikTok(File.ReadAllText(path));
            gifts = settings.Rules.FindAll(rule => rule.Enabled && rule.MatchesEvent("gift") && rule.Coins > 0);
            if (gifts.Count == 0) throw new InvalidOperationException("Không có quà đang bật trong cấu hình.");
        }
        catch (Exception exception)
        {
            randomGiftStatus = "Không đọc được cấu hình quà.";
            Debug.LogWarning("Giả lập quà: " + exception.Message, this);
            return;
        }
        randomGiftSimulation = StartCoroutine(SimulateRandomGifts(gifts));
    }

    private IEnumerator SimulateRandomGifts(List<TikTokArmyRule> gifts)
    {
        var batchId = Guid.NewGuid().ToString("N");
        // At most 3 máy bắn đá and 10 pháp sư over the whole simulation (the game also clamps what it hands out).
        RecoilPathfinding.RecoilAutoBattleDemo.BeginGiftSimulationCaps();
        var allowed = new List<TikTokArmyRule>(gifts.Count);
        try
        {
            for (int i = 0; i < 1000; i++)
            {
                var user = new TikUser { user_id = $"{RecoilPathfinding.RecoilAutoBattleDemo.GiftSimulationUserPrefix}{batchId}-{i}",
                    nickname = $"Test User {i + 1}" };
                // Select a team before donating, independent of the popup's auto-team toggle.
                TikTokUdpReceiver.OnEvent?.Invoke(new TikEvent { type = "comment", user = user,
                    comment = UnityEngine.Random.value < .5f ? "1" : "2" });
                // Gifts whose unit type is used up are left out, so the 1.000 viewers still get soldiers.
                allowed.Clear();
                foreach (var candidate in gifts)
                    if (!RecoilPathfinding.RecoilAutoBattleDemo.GiftSimulationTypeUsedUp(candidate.UnitType)) allowed.Add(candidate);
                var pool = allowed.Count > 0 ? allowed : gifts;
                var gift = pool[UnityEngine.Random.Range(0, pool.Count)];
                TikTokUdpReceiver.OnEvent?.Invoke(new TikEvent { type = "gift", user = user,
                    gift = new TikGift { id = gift.GiftId, name = gift.Name, price = gift.Coins },
                    delta = 1, count = 1 });
                randomGiftStatus = $"Đã gửi {i + 1:N0} / 1.000 người.";
                if ((i + 1) % 10 == 0) yield return null;
            }
        }
        finally { randomGiftSimulation = null; }
    }

    private void OnDisable()
    {
        if (randomGiftSimulation == null) return;
        StopCoroutine(randomGiftSimulation);
        randomGiftSimulation = null;
        randomGiftStatus = "Đã dừng giả lập.";
    }
    [SerializeField] TMP_InputField userIdInput, userNameInpup, giftIdInput, giftPriceInput, likeCountInput, commentInput, countGiftInput, otherUsersInput;
    [SerializeField] Button btnSend;
    [SerializeField] Toggle shareToggle, followToggle;
    [SerializeField] GameObject SettingPopup;
    [SerializeField] InputAction keyControl;
    [SerializeField] bool isHandleKeyEvent;
    private void Awake()
    {
        btnSend.onClick.AddListener(OnSend);
        keyControl.Enable();
    }
    private void Update()
    {
        if (keyControl.WasPressedThisFrame())
        {
            SettingPopup.SetActive(!SettingPopup.activeInHierarchy);
            // Cursor.visible = SettingPopup.activeInHierarchy ? true : false;
        }
        //HandleInputActionByKey();
    }

    private void OnSend()
    {
        int.TryParse(otherUsersInput.text, out int repeat);
        repeat = Math.Max(repeat, 1);
        for (int i = 0; i < repeat; i++)
        {
            var user = new TikUser()
            {
                user_id = userIdInput.text,
                nickname = userNameInpup.text,
            };
            if (string.IsNullOrEmpty(user.user_id))
            {
                user.user_id = UnityEngine.Random.Range(0, 10000).ToString();
                user.nickname = $"User {user.user_id}";
            }
            if (!string.IsNullOrEmpty(giftPriceInput.text) || !string.IsNullOrEmpty(giftIdInput.text))
            {
                int.TryParse(giftIdInput.text, out int id);
                int.TryParse(giftPriceInput.text, out int price);
                int.TryParse(countGiftInput.text, out int delta);
                var gift = new TikGift()
                {
                    id = id,
                    price = price,
                };

                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    gift = gift,
                    type = "gift_final",
                    delta = Math.Max(delta, 1)
                });
            }
            if (!string.IsNullOrEmpty(commentInput.text))
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    comment = commentInput.text,
                    type = "comment"
                });
            }
            if (int.TryParse(likeCountInput.text, out int count))
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    like_count = count,
                    type = "like"
                });
            }
            if (shareToggle.isOn)
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    type = "share"
                });
            }
            if (followToggle.isOn)
            {
                TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
                {
                    user = user,
                    type = "follow"
                });
            }
        }
    }
    // public void HandleInputActionByKey()
    // {
    //     if (!isHandleKeyEvent) return;
    //     Keyboard keyboard = Keyboard.current;
    //     if (keyboard == null || tiktokConfig == null || tiktokConfig.tiktokDatas == null)
    //     {
    //         return;
    //     }

    //     var data = tiktokConfig.tiktokDatas;
    //     int index = -1;

    //     if (keyboard.digit1Key.wasPressedThisFrame)
    //     {
    //         index = 0;
    //     }
    //     else if (keyboard.digit2Key.wasPressedThisFrame)
    //     {
    //         index = 1;
    //     }
    //     else if (keyboard.digit3Key.wasPressedThisFrame)
    //     {
    //         index = 2;
    //     }
    //     else if (keyboard.digit4Key.wasPressedThisFrame)
    //     {
    //         index = 3;
    //     }
    //     else if (keyboard.digit5Key.wasPressedThisFrame)
    //     {
    //         index = 4;
    //     }
    //     else if (keyboard.digit6Key.wasPressedThisFrame)
    //     {
    //         index = 5;
    //     }
    //     else if (keyboard.digit7Key.wasPressedThisFrame)
    //     {
    //         index = 6;
    //     }
    //     else if (keyboard.digit8Key.wasPressedThisFrame)
    //     {
    //         index = 7;
    //     }
    //     else if (keyboard.digit9Key.wasPressedThisFrame)
    //     {
    //         index = 8;
    //     }
    //     else if (keyboard.digit0Key.wasPressedThisFrame)
    //     {
    //         index = 9;
    //     }
    //     else if (keyboard.minusKey.wasPressedThisFrame)
    //     {
    //         index = 10;
    //     }
    //     else if (keyboard.equalsKey.wasPressedThisFrame)
    //     {
    //         index = 11;
    //     }
    //     else if (keyboard.backspaceKey.wasPressedThisFrame)
    //     {
    //         index = 12;
    //     }

    //     if (index < 0 || index >= data.Length)
    //     {
    //         return;
    //     }

    //     ExecuteEvent(data[index]);
    // }

    // private void ExecuteEvent(TiktokConfigSO.TiktokData tiktokData)
    // {
    //     if (tiktokData == null)
    //     {
    //         return;
    //     }

    //     var userId = UnityEngine.Random.Range(0, 10000).ToString();
    //     var user = new TikUser()
    //     {
    //         user_id = userId,
    //         nickname = $"User {userId}",
    //     };

    //     if (tiktokData.IdGift > 0 || tiktokData.PriceGift > 0)
    //     {
    //         int price = Mathf.RoundToInt(tiktokData.PriceGift);
    //         var gift = new TikGift()
    //         {
    //             id = tiktokData.IdGift,
    //             price = price,
    //         };

    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             gift = gift,
    //             type = "gift_final",
    //             count = 1,
    //             total = price,
    //             delta = 1
    //         });
    //     }

    //     if (tiktokData.LikeCount > 0)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             like_count = tiktokData.LikeCount,
    //             type = "like"
    //         });
    //     }

    //     if (tiktokData.Comments != null && tiktokData.Comments.Length > 0)
    //     {
    //         foreach (var comment in tiktokData.Comments)
    //         {
    //             if (string.IsNullOrEmpty(comment))
    //             {
    //                 continue;
    //             }

    //             TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //             {
    //                 user = user,
    //                 comment = comment,
    //                 type = "comment"
    //             });
    //         }
    //     }

    //     if (tiktokData.IsShare)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             type = "share"
    //         });
    //     }

    //     if (tiktokData.IsFollow)
    //     {
    //         TikTokUdpReceiver.OnEvent.Invoke(new TikEvent()
    //         {
    //             user = user,
    //             type = "follow"
    //         });
    //     }
    // }
}
