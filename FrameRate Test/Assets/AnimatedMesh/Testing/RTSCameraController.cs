using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// RTS Camera Controller — UI Panel driven
///
/// SETUP:
///  1. Add a Canvas (Screen Space - Overlay) to the scene.
///     Unity will auto-create an EventSystem — leave it there.
///  2. Inside the Canvas add a Panel that fills the screen (stretch all anchors).
///     Set the Panel Image color alpha to 0 so it is invisible but still raycastable.
///  3. Attach THIS script to the Panel GameObject.
///  4. Assign the "Camera To Control" field in the Inspector (or leave empty to use Camera.main).
///
/// CONTROLS:
///  Mobile  — single finger drag = pan | two-finger pinch = zoom
///  Editor  — left-mouse drag = pan | scroll wheel = zoom
/// </summary>
public class RTSCameraController : MonoBehaviour,
    IPointerDownHandler,
    IPointerUpHandler,
    IDragHandler
{
    // -----------------------------------------------------------------------
    // Inspector fields
    // -----------------------------------------------------------------------

    [Header("Target Camera")]
    [Tooltip("Camera to control. Leave empty to use Camera.main.")]
    public Camera cameraToControl;

    [Header("View Angle")]
    [Tooltip("Pitch angle looking down at the ground. 10 = near-horizontal, 90 = top-down.")]
    [Range(10f, 90f)]
    public float viewAngle = 45f;

    [Header("Zoom")]
    public float minZoom = 5f;
    public float maxZoom = 80f;
    public float zoomSpeed = 0.05f;
    public float zoomSmoothing = 8f;

    [Header("Pan")]
    public float panSpeed = 0.04f;
    public float panSmoothing = 10f;

    [Header("World Boundary (optional)")]
    public bool useBoundary = false;
    public Vector2 boundaryMin = new Vector2(-200f, -200f);
    public Vector2 boundaryMax = new Vector2(200f, 200f);

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------

    private Transform _cam;

    private float _targetHeight;
    private Vector3 _targetPosition;

    // Single-finger pan
    private int _panId = -1;
    private Vector2 _lastPan;

    // Two-finger pinch
    private int _pinchId0 = -1;
    private int _pinchId1 = -1;
    private Vector2 _pinchPos0;
    private Vector2 _pinchPos1;
    private float _lastPinchDist;
    private bool _pinching = false;

    // -----------------------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------------------

    private void Awake()
    {
        if (cameraToControl == null)
            cameraToControl = Camera.main;

        _cam = cameraToControl.transform;
        _targetHeight = _cam.position.y;
        _targetPosition = _cam.position;

        ApplyAngle(true);
    }

    private void Update()
    {
#if UNITY_EDITOR || UNITY_STANDALONE
        // Left-mouse drag pan is handled through the EventSystem (OnDrag).
        // Only the scroll-wheel zoom needs polling here.
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
            DoZoom(-scroll * 500f);
#endif
        SmoothApply();
    }

    // -----------------------------------------------------------------------
    // UI EventSystem callbacks  (these fire only when the pointer is on the Panel)
    // -----------------------------------------------------------------------

    public void OnPointerDown(PointerEventData e)
    {
        if (_pinching)
        {
            // Ignore extra fingers while already pinching
            return;
        }

        if (_panId == -1)
        {
            // First finger down — start panning
            _panId = e.pointerId;
            _lastPan = e.position;
        }
        else if (_pinchId0 == -1 && e.pointerId != _panId)
        {
            // Second finger down — upgrade to pinch
            _pinchId0 = _panId;          // recycle the first finger
            _pinchPos0 = _lastPan;

            _pinchId1 = e.pointerId;
            _pinchPos1 = e.position;

            _panId = -1;          // suspend pan while pinching
            _pinching = true;
            _lastPinchDist = Vector2.Distance(_pinchPos0, _pinchPos1);
        }
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (!_pinching)
        {
            if (e.pointerId == _panId)
                _panId = -1;
            return;
        }

        // One pinch finger lifted
        if (e.pointerId == _pinchId0 || e.pointerId == _pinchId1)
        {
            // Surviving finger resumes pan
            int survivorId = (e.pointerId == _pinchId0) ? _pinchId1 : _pinchId0;
            Vector2 survivorPos = (e.pointerId == _pinchId0) ? _pinchPos1 : _pinchPos0;

            _pinching = false;
            _pinchId0 = -1;
            _pinchId1 = -1;

            _panId = survivorId;
            _lastPan = survivorPos;
        }
    }

    public void OnDrag(PointerEventData e)
    {
        if (_pinching)
        {
            // Track which pinch finger moved
            if (e.pointerId == _pinchId0) _pinchPos0 = e.position;
            else if (e.pointerId == _pinchId1) _pinchPos1 = e.position;
            else return;

            float newDist = Vector2.Distance(_pinchPos0, _pinchPos1);
            float delta = newDist - _lastPinchDist;
            DoZoom(-delta);                     // spread fingers = zoom in (lower height)
            _lastPinchDist = newDist;
        }
        else if (e.pointerId == _panId)
        {
            DoPan(e.position - _lastPan);
            _lastPan = e.position;
        }
    }

    // -----------------------------------------------------------------------
    // Core camera operations
    // -----------------------------------------------------------------------

    private void DoPan(Vector2 screenDelta)
    {
        float heightFactor = _targetHeight / maxZoom;
        float speed = panSpeed * _targetHeight * heightFactor;

        Vector3 right = _cam.right;
        right.y = 0f;
        right.Normalize();

        Vector3 forward = _cam.forward;
        forward.y = 0f;
        forward.Normalize();

        _targetPosition += (-right * screenDelta.x + -forward * screenDelta.y) * speed;

        if (useBoundary)
        {
            _targetPosition.x = Mathf.Clamp(_targetPosition.x, boundaryMin.x, boundaryMax.x);
            _targetPosition.z = Mathf.Clamp(_targetPosition.z, boundaryMin.y, boundaryMax.y);
        }
    }

    private void DoZoom(float delta)
    {
        _targetHeight += delta * zoomSpeed;
        _targetHeight = Mathf.Clamp(_targetHeight, minZoom, maxZoom);
    }

    private void SmoothApply()
    {
        Vector3 cur = _cam.position;
        float newY = Mathf.Lerp(cur.y, _targetHeight, Time.deltaTime * zoomSmoothing);
        Vector3 pos = Vector3.Lerp(cur, _targetPosition, Time.deltaTime * panSmoothing);
        pos.y = newY;

        _cam.position = pos;
        ApplyAngle(false);
    }

    private void ApplyAngle(bool snap)
    {
        Vector3 e = _cam.eulerAngles;
        e.x = viewAngle;
        e.z = 0f;
        _cam.eulerAngles = e;

        if (snap)
        {
            Vector3 p = _cam.position;
            p.y = _targetHeight;
            _cam.position = p;
            _targetPosition = p;
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Snap the camera focus to a world XZ point.</summary>
    public void FocusOnPoint(Vector3 worldPoint)
        => _targetPosition = new Vector3(worldPoint.x, _targetHeight, worldPoint.z);

    /// <summary>Change the pitch angle at runtime.</summary>
    public void SetViewAngle(float angle)
    {
        viewAngle = Mathf.Clamp(angle, 10f, 90f);
        ApplyAngle(false);
    }

    /// <summary>Jump to a specific zoom height.</summary>
    public void SetZoom(float height)
        => _targetHeight = Mathf.Clamp(height, minZoom, maxZoom);
}