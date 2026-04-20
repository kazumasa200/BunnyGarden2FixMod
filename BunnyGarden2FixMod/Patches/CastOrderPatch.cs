using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using GB.Scene;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// バーに入る前にキャストの出勤順序を変更できる機能。
///
/// ■ 操作方法
///   F1          : 編集モード ON/OFF
///   1〜5 キー    : 1回目押し → キャストを選択（黄色表示）
///                  2回目押し → 選択中キャストと入れ替え
/// </summary>
public class CastOrderPatch : MonoBehaviour
{
    private CastOrder? _editing;
    private int _cursor = -1;

    private readonly struct CastOrder(DateTime dateTime, List<CharID> list)
    {
        public DateTime DateTime { get; } = dateTime;
        public List<CharID> List { get; } = list;

        public bool IsUpToDate()
        {
            var system = GBSystem.Instance;
            return system != null && system.RefGameData()?.m_gameDate.Date == DateTime.Date;
        }
    }

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<CastOrderPatch>();
    }

    private void Update()
    {
        if (!Plugin.ConfigCastOrderEnabled.Value) return;

        var system = GBSystem.Instance;
        if (system == null)
        {
            ResetEditing();
            return;
        }

        var gameData = system.RefGameData();
        var holeScene = system.GetHoleScene();
        if (gameData == null || holeScene == null)
        {
            ResetEditing();
            return;
        }

        // F1 で編集モードのON/OFF
        if (Keyboard.current?[Key.F1].wasPressedThisFrame == true)
        {
            if (_editing == null)
            {
                // 最新の出勤順を確定してから取り込む
                gameData.UpdateTodaysCastOrder();
                _editing = new CastOrder(gameData.m_gameDate, [.. gameData.m_todaysCastOrder]);
                PatchLogger.LogInfo("[CastOrder] 編集モード開始");
            }
            else
            {
                ResetEditing();
                PatchLogger.LogInfo("[CastOrder] 編集モード終了");
            }
        }

        if (_editing is CastOrder editing)
        {
            // 日付変更またはバー入店後は自動終了
            if (!editing.IsUpToDate() || gameData.IsInBar())
            {
                ResetEditing();
                return;
            }

            for (int i = 0; i < editing.List.Count; i++)
            {
                if (editing.List[i] >= CharID.NUM) break;
                if (Keyboard.current?[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame != true) continue;

                if (_cursor < 0)
                {
                    // 1回目: キャストを選択
                    _cursor = i;
                    continue;
                }

                // 2回目: 選択キャストと入れ替え
                if (_cursor != i)
                {
                    var chara = editing.List[_cursor];
                    editing.List.RemoveAt(_cursor);
                    editing.List.Insert(i, chara);

                    // BarScene がまだスポーンされていない場合のみキャラクターを再ロード
                    var barScene = FindAnyObjectByType<BarScene>(FindObjectsInactive.Include);
                    if (barScene == null)
                    {
                        gameData.m_todaysCastOrder = [.. editing.List];
                        holeScene.UnloadCharacter();
                        BarScene.PreloadCharacter();
                        PatchLogger.LogInfo($"[CastOrder] 順序変更: {string.Join(", ", editing.List)}");
                    }
                    else
                    {
                        PatchLogger.LogInfo("[CastOrder] BarScene がスポーン済みのため変更をスキップしました");
                    }
                }

                _cursor = -1;
            }
        }
    }

    private void OnGUI()
    {
        if (!Plugin.ConfigCastOrderEnabled.Value) return;
        if (_editing is not CastOrder editing) return;

        float scale = GetUiScale();
        var prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0f, 0f, 0f, 255f);

        GUILayout.BeginArea(new Rect(10, 70, 260, 280));
        GUILayout.BeginVertical("box");

        GUI.color = Color.cyan;
        GUILayout.Label("キャスト出勤順 / Cast Order");
        GUI.color = Color.white;
        GUILayout.Label("F1 = 終了 / Exit");
        GUILayout.Label("数字キーで選択、もう一度で入れ替え");
        GUILayout.Label("Press number to select, again to swap.");
        GUILayout.Space(4);

        for (int i = 0; i < editing.List.Count; i++)
        {
            if (editing.List[i] >= CharID.NUM) break;
            GUI.color = i == _cursor ? Color.yellow : Color.white;
            GUILayout.Label($"  {i + 1}: {editing.List[i]}");
        }

        GUI.color = Color.white;
        GUILayout.EndVertical();
        GUILayout.EndArea();

        GUI.backgroundColor = prevBg;

        GUI.matrix = prevMatrix;
    }

    /// <summary>
    /// Windows の表示スケール（DPI）に合わせた UI 倍率を返す。
    /// Screen.dpi が 0 以下（Steam Deck など取得不可）の場合は 1.0 を返す。
    /// 96 DPI = 100%（スケール 1.0）を基準とする。
    /// </summary>
    private static float GetUiScale()
    {
        float dpi = Screen.dpi;
        if (dpi <= 0f) return 1f;
        return Mathf.Clamp(dpi / 96f, 0.5f, 4f);
    }

    private void ResetEditing()
    {
        _editing = null;
        _cursor = -1;
    }
}
