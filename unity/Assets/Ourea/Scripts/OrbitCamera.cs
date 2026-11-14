using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Drag to rotate, scroll to zoom, WASD / arrows (or Shift+drag) to pan.
public class OrbitCamera : MonoBehaviour
{
    public Vector3 target;
    public float distance = 800f;
    public float yaw = 0f;
    public float pitch = 55f;
    public float minDistance = 15f;
    public float maxDistance = 2500f;
    public Vector2 boundsMin = new Vector2(-100f, -100f);
    public Vector2 boundsMax = new Vector2(800f, 800f);

    Vector3 goalTarget;
    float goalDistance;
    bool dragging;
    Vector3 homeTarget;
    float homeDistance, homeYaw, homePitch;
    float goalYaw, goalPitch;

    void Awake()
    {
        goalTarget = homeTarget = target;
        goalDistance = homeDistance = distance;
        goalYaw = homeYaw = yaw;
        goalPitch = homePitch = pitch;
    }

    public void ResetView()
    {
        goalTarget = homeTarget;
        goalDistance = homeDistance;
        goalYaw = homeYaw;
        goalPitch = homePitch;
    }

    public void Focus(Vector3 point, float dist)
    {
        goalTarget = point;
        goalDistance = Mathf.Clamp(dist, minDistance, maxDistance);
    }

    void LateUpdate()
    {
        Vector2 delta = Vector2.zero;
        Vector2 mouse = Vector2.zero;
        Vector2 move = Vector2.zero;
        float scroll = 0f;
        bool anyButton = false, pressedThisFrame = false, panMod = false;

#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null)
        {
            delta = m.delta.ReadValue() * 0.25f;
            mouse = m.position.ReadValue();
            scroll = m.scroll.ReadValue().y;
            anyButton = m.leftButton.isPressed || m.rightButton.isPressed || m.middleButton.isPressed;
            pressedThisFrame = m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame || m.middleButton.wasPressedThisFrame;
            panMod = m.middleButton.isPressed;
        }
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.wKey.isPressed || k.upArrowKey.isPressed) move.y += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) move.y -= 1f;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) move.x += 1f;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) move.x -= 1f;
            if (k.shiftKey.isPressed) panMod = true;
        }
#else
        delta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 5f;
        mouse = Input.mousePosition;
        scroll = Input.mouseScrollDelta.y;
        anyButton = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        pressedThisFrame = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
        panMod = Input.GetMouseButton(2) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif

        Vector2 guiPoint = new Vector2(mouse.x, Screen.height - mouse.y);
        bool overUI = OureaViewer.PointerOverUI(guiPoint);

        if (pressedThisFrame) dragging = !overUI;
        if (!anyButton) dragging = false;

        Vector3 flatRight = Quaternion.Euler(0f, goalYaw, 0f) * Vector3.right;
        Vector3 flatForward = Quaternion.Euler(0f, goalYaw, 0f) * Vector3.forward;

        if (dragging)
        {
            if (panMod)
            {
                float s = goalDistance * 0.0025f;
                goalTarget -= (flatRight * delta.x + flatForward * delta.y) * s;
            }
            else
            {
                goalYaw += delta.x * 0.6f;
                goalPitch = Mathf.Clamp(goalPitch - delta.y * 0.6f, 10f, 89f);
            }
        }

        if (!overUI && Mathf.Abs(scroll) > 0.01f)
            goalDistance = Mathf.Clamp(goalDistance * (scroll > 0f ? 0.9f : 1.11f), minDistance, maxDistance);

        if (move.sqrMagnitude > 0f)
            goalTarget += (flatRight * move.x + flatForward * move.y) * goalDistance * 0.8f * Time.deltaTime;

        goalTarget.x = Mathf.Clamp(goalTarget.x, boundsMin.x, boundsMax.x);
        goalTarget.z = Mathf.Clamp(goalTarget.z, boundsMin.y, boundsMax.y);

        float t = 1f - Mathf.Exp(-8f * Time.deltaTime);
        target = Vector3.Lerp(target, goalTarget, t);
        distance = Mathf.Lerp(distance, goalDistance, t);
        yaw = Mathf.LerpAngle(yaw, goalYaw, t);
        pitch = Mathf.Lerp(pitch, goalPitch, t);

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = rot;
        transform.position = target - rot * Vector3.forward * distance;
    }
}
