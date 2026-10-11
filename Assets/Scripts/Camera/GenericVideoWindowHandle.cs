using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Controller-ray dragging in the head's view plane; never samples pose SDK data.</summary>
public sealed class GenericVideoWindowHandle : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler, IDragHandler, IEndDragHandler
{
    public GenericVideoView View;
    public bool Resize;
    private bool held;
    private int pointerId;
    private Vector2 initialPointer, initialOffset;
    private float initialScale;

    public void OnInitializePotentialDrag(PointerEventData data) { data.useDragThreshold = false; }

    public void OnPointerDown(PointerEventData data)
    {
        if (held || data.button != PointerEventData.InputButton.Left ||
            !View.TryGetPointerPosition(data, out initialPointer)) return;
        held = true;
        pointerId = data.pointerId;
        initialOffset = View.WindowOffset;
        initialScale = View.WindowScale;
    }

    public void OnDrag(PointerEventData data)
    {
        if (!held || data.pointerId != pointerId || !View.TryGetPointerPosition(data, out Vector2 point)) return;
        if (Resize)
        {
            Vector2 diagonal = initialPointer - initialOffset;
            if (diagonal.sqrMagnitude < .000001f) return;
            float factor = Vector2.Dot(point - initialOffset, diagonal) / diagonal.sqrMagnitude;
            View.SetWindowLayout(initialOffset, initialScale * factor);
        }
        else View.SetWindowLayout(initialOffset + point - initialPointer, initialScale);
    }

    public void OnPointerUp(PointerEventData data) { Finish(data.pointerId); }
    public void OnEndDrag(PointerEventData data) { Finish(data.pointerId); }

    private void Finish(int id)
    {
        if (!held || id != pointerId) return;
        held = false;
        View.SaveWindowLayout();
    }

    private void OnDisable()
    {
        if (held) Finish(pointerId);
    }
}
