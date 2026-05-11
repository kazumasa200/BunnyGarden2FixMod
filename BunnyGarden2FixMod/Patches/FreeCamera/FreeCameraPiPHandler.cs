using UnityEngine;
using UnityEngine.EventSystems;

namespace BunnyGarden2FixMod.Patches.FreeCamera;

/// FreeCameraのPiPウィンドウのドラッグとリサイズを管理するクラス
public class FreeCameraPiPHandler : MonoBehaviour, IDragHandler, IPointerDownHandler, IEndDragHandler
{
    private RectTransform rectTransform;
    private const float EdgeSize = 30f; // ドラッグでリサイズするエリアのサイズ
    public float TargetAspectRatio { get; set; } = 16f / 9f; // デフォルトの縦横比
    private enum DragMode
    {
        None,
        Move,
        Resize
    }
    private DragMode currentDragMode = DragMode.None;
    private bool isLeft, isRight, isTop, isBottom;
    public System.Action<int, int> OnResizeCommitted; // リサイズ確定時のコールバック（幅と高さを引数に）

    void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
    }
    public void OnPointerDown(PointerEventData eventData)
    {
        // 常に最前面に持ってくる
        rectTransform.SetAsLastSibling();

            // --- ドラッグ開始時の判定 ---
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, eventData.pressPosition, eventData.pressEventCamera, out localPoint);

        float width = rectTransform.rect.width;
        float height = rectTransform.rect.height;

        // Pivot(1,0)基準の判定
        isLeft   = localPoint.x <  EdgeSize - width;
        isRight  = localPoint.x > -EdgeSize;
        isBottom = localPoint.y <  EdgeSize;
        isTop    = localPoint.y > -EdgeSize + height ;

        if (isLeft || isRight || isBottom || isTop)
        {
            currentDragMode = DragMode.Resize;
        }
        else
        {
            currentDragMode = DragMode.Move;
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (currentDragMode == DragMode.Resize)
        {
            // --- リサイズ処理のみ ---
            Vector2 sizeDelta = rectTransform.sizeDelta;
            float deltaX = isRight ? eventData.delta.x : (isLeft   ? -eventData.delta.x : 0);
            float deltaY = isTop   ? eventData.delta.y : (isBottom ? -eventData.delta.y : 0);

            if (Mathf.Abs(deltaX) > Mathf.Abs(deltaY))
            {
                sizeDelta.x += deltaX;
                sizeDelta.y = sizeDelta.x / TargetAspectRatio;
            }
            else
            {
                sizeDelta.y += deltaY;
                sizeDelta.x = sizeDelta.y * TargetAspectRatio;
            }

            if (sizeDelta.x > 100f && sizeDelta.y > 100f)
            {
                rectTransform.sizeDelta = sizeDelta;
            }
        }
        else if (currentDragMode == DragMode.Move)
        {
            // --- 移動処理のみ ---
            rectTransform.anchoredPosition += eventData.delta;
        }
    }

    // ドラッグが終わったら解像度を再設定し，モードをリセット
    public void OnEndDrag(PointerEventData eventData)
    {
        if (currentDragMode == DragMode.Resize)
        {
            var rectTransform = GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                int newWidth = Mathf.RoundToInt(rectTransform.rect.width);
                int newHeight = Mathf.RoundToInt(rectTransform.rect.height);
                OnResizeCommitted?.Invoke(newWidth, newHeight);
                Plugin.Logger.LogInfo($"PiPサイズ変更: {newWidth}x{newHeight}");
            }
        }
        currentDragMode = DragMode.None;
    }
}
