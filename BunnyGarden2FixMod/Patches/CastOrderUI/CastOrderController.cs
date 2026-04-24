using BunnyGarden2FixMod.Utils;
using GB;
using GB.Game;
using GB.Scene;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.CastOrderUI;

/// <summary>
/// キャスト出勤順変更のコントローラー。
/// UI ToolkitベースのCastOrderViewを使用する。
/// 数字キー(1-6)で入れ替え、下部チェックボックスで全固定。
/// </summary>
public class CastOrderController : MonoBehaviour
{
    public static CastOrderController Instance { get; private set; }

    public static void Initialize(GameObject parent)
    {
        var host = new GameObject("BG2CastOrderUI");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<CastOrderController>();
    }

    private CastOrderView m_view;
    private List<CharID> m_castOrder = new();
    private bool m_allLocked;               // 順番固定チェックボックス状態

    /// <summary>パッチからアクセスするための公開プロパティ。</summary>
    public bool AllLocked => m_allLocked;

    private int _cursor = -1;               // 現在選択中の行 (-1: 未選択)
    private DateTime? m_editDate = null;     // 編集開始時の日付

    public bool ShouldSuppressGameInput(string actionName)
    {
        if (!ShouldSuppressGameInput()) return false;
        return actionName != null && (actionName == "Move" || actionName == "Look" || actionName == "Sprint");
    }

    private bool ShouldSuppressGameInput()
    {
        return m_view != null && m_view.IsShown && m_view.IsPointerOverPanel();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[CastOrder] CastOrderController が既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        m_view = gameObject.AddComponent<CastOrderView>();
        m_view.OnCloseClicked += HandleCloseClicked;
        m_view.OnAllLockToggled += HandleAllLockToggled;
    }

    private void OnDestroy()
    {
        if (m_view != null)
        {
            m_view.OnCloseClicked -= HandleCloseClicked;
            m_view.OnAllLockToggled -= HandleAllLockToggled;
        }
        if (Instance == this) Instance = null;
    }

    private void HandleAllLockToggled()
    {
        if (!m_view.IsShown) return;
        // トグル: 現在の状態を反転
        m_allLocked = !m_allLocked;
        PatchLogger.LogInfo($"[CastOrder] 順番固定: {(m_allLocked ? "ON" : "OFF")}");
        m_view.Render(BuildRenderData());
    }

    private void HandleCloseClicked()
    {
        m_view.Hide();
        _cursor = -1;
        m_editDate = null;
        PatchLogger.LogInfo("[CastOrder] パネル閉じる");
    }

    /// <summary>数字キーで指定行のキャストと_cursor位置のキャストを入れ替える。</summary>
    private void HandleDigitKey(int targetIndex)
    {
        if (!m_view.IsShown) return;
        if (targetIndex < 0 || targetIndex >= m_castOrder.Count) return;

        // 固定中は入れ替え不可
        if (m_allLocked)
        {
            PatchLogger.LogInfo("[CastOrder] 順番固定中のため入れ替えをスキップしました");
            return;
        }

        // _cursorが未選択の場合はまず選択に移動（入れ替えは行わない）
        if (_cursor < 0)
        {
            _cursor = targetIndex;
            m_view.Render(BuildRenderData());
            PatchLogger.LogInfo($"[CastOrder] 選択: {_cursor}");
            return;
        }

        // _cursorが既にtargetと同じ場合は入れ替え不要
        if (_cursor == targetIndex)
        {
            PatchLogger.LogInfo("[CastOrder] 同じ行なので入れ替えません");
            return;
        }

        SwapAndApply(_cursor, targetIndex);
    }

    private void Update()
    {
        if (Plugin.ConfigCastOrderEnabled == null) return;
        if (!Plugin.ConfigCastOrderEnabled.Value) return;
        if (m_view == null) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // Hotkeyでパネルトグル
        if (kb[Key.F1].wasPressedThisFrame)
        {
            if (m_view.IsShown)
            {
                m_view.Hide();
                _cursor = -1;
                m_editDate = null;
            }
            else if (CanOpen())
            {
                Open();
            }
            else
            {
                PatchLogger.LogInfo("[CastOrder] シーン条件不一致のため開けません");
            }
        }

        if (!m_view.IsShown) return;

        // パネル上でのキーボード操作
        if (!m_view.IsPointerOverPanel()) return;

        // W/↑: 選択を上に移動
        if (kb[Key.W].wasPressedThisFrame || kb[Key.UpArrow].wasPressedThisFrame)
        {
            MoveSelection(-1);
            return;
        }

        // S/↓: 選択を下に移動
        if (kb[Key.S].wasPressedThisFrame || kb[Key.DownArrow].wasPressedThisFrame)
        {
            MoveSelection(1);
            return;
        }

        // Esc: 閉じる
        if (kb[Key.Escape].wasPressedThisFrame)
        {
            m_view.Hide();
            _cursor = -1;
            m_editDate = null;
            return;
        }

        // 数字キー(1-6): _cursor位置のキャストを指定行と入れ替え
        for (int i = 0; i < m_castOrder.Count && i < 6; i++)
        {
            if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame)
            {
                HandleDigitKey(i);
                return;
            }
        }
    }

    private bool CanOpen()
    {
        var system = GBSystem.Instance;
        if (system == null) return false;
        var gameData = system.RefGameData();
        var holeScene = system.GetHoleScene();
        if (gameData == null || holeScene == null) return false;

        // バー入店後は開けない
        if (gameData.IsInBar()) return false;

        return true;
    }

    private void Open()
    {
        var system = GBSystem.Instance;
        var gameData = system.RefGameData();

        // 最新の出勤順を確定してから取り込む（固定中はパッチでスキップされる）
        gameData.UpdateTodaysCastOrder();
        m_castOrder = new List<CharID>(gameData.m_todaysCastOrder);
        // m_allLocked は保持（メニューを開き直しても固定状態を維持）
        _cursor = -1;
        m_editDate = gameData.m_gameDate.Date;

        m_view.Show(BuildRenderData());
        PatchLogger.LogInfo($"[CastOrder] オープン: {string.Join(", ", m_castOrder)} (固定:{(m_allLocked ? "ON" : "OFF")})");
    }

    private void MoveSelection(int delta)
    {
        if (m_castOrder.Count == 0) return;

        int next = _cursor + delta;
        if (next < 0) next = m_castOrder.Count - 1;
        if (next >= m_castOrder.Count) next = 0;

        _cursor = next;
        m_view.Render(BuildRenderData());
    }

    private void SwapAndApply(int indexA, int indexB)
    {
        var system = GBSystem.Instance;
        var gameData = system?.RefGameData();
        var holeScene = system?.GetHoleScene();

        if (gameData == null || holeScene == null) return;

        // 日付チェック
        if (m_editDate.HasValue && gameData.m_gameDate.Date != m_editDate.Value)
        {
            PatchLogger.LogInfo("[CastOrder] 日付が変更されたため操作をキャンセルしました");
            m_view.Hide();
            _cursor = -1;
            m_editDate = null;
            return;
        }

        // バー入店チェック
        if (gameData.IsInBar())
        {
            PatchLogger.LogInfo("[CastOrder] バー入店後のため操作をキャンセルしました");
            m_view.Hide();
            _cursor = -1;
            m_editDate = null;
            return;
        }

        // 入れ替え
        var chara = m_castOrder[indexA];
        m_castOrder.RemoveAt(indexA);
        m_castOrder.Insert(indexB, chara);

        // ゲームに適用
        gameData.m_todaysCastOrder = [.. m_castOrder];

        // 入れ替え後は_cursorをリセットしてハイライトを消す
        _cursor = -1;

        // BarScene がまだスポーンされていない場合のみキャラクターを再ロード
        var barScene = FindAnyObjectByType<BarScene>(FindObjectsInactive.Include);
        if (barScene == null)
        {
            holeScene.UnloadCharacter();
            BarScene.PreloadCharacter();
        }

        PatchLogger.LogInfo($"[CastOrder] 順序変更: {string.Join(", ", m_castOrder)}");

        // UIを再レンダリング
        m_view.Render(BuildRenderData());
    }

    private CastOrderView.RenderData BuildRenderData()
    {
        var rows = new List<CastOrderView.RowData>();
        for (int i = 0; i < m_castOrder.Count; i++)
        {
            if (m_castOrder[i] >= CharID.NUM) break;
            rows.Add(new CastOrderView.RowData
            {
                Index = i,
                CharId = m_castOrder[i],
                IsSelected = i == _cursor,
            });
        }

        return new CastOrderView.RenderData
        {
            Rows = rows,
            SelectedIndex = _cursor,
            AllLocked = m_allLocked,
        };
    }
}
