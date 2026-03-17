using UnityEngine;
using UnityEngine.EventSystems;

public sealed class CliUiProbe : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public int ClickCount { get; private set; }
    public int PointerDownCount { get; private set; }
    public int PointerUpCount { get; private set; }
    public int DragCount { get; private set; }
    private float _lastPointerDownTime;

    public void OnPointerClick(PointerEventData eventData)
    {
        ClickCount++;
        Debug.Log("CliUiProbe click " + gameObject.name + " count=" + ClickCount + " clickCount=" + eventData.clickCount);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        PointerDownCount++;
        _lastPointerDownTime = Time.realtimeSinceStartup;
        Debug.Log("CliUiProbe down " + gameObject.name + " count=" + PointerDownCount);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        PointerUpCount++;
        var heldMs = Mathf.RoundToInt((Time.realtimeSinceStartup - _lastPointerDownTime) * 1000f);
        Debug.Log("CliUiProbe up " + gameObject.name + " count=" + PointerUpCount + " heldMs=" + heldMs);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        Debug.Log("CliUiProbe begin drag " + gameObject.name);
    }

    public void OnDrag(PointerEventData eventData)
    {
        DragCount++;
        Debug.Log("CliUiProbe drag " + gameObject.name + " delta=" + eventData.delta);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        Debug.Log("CliUiProbe end drag " + gameObject.name + " count=" + DragCount);
    }
}
