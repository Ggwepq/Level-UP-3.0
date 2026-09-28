using UnityEngine;

/// <summary>
/// Driving chase camera.
/// Put this on your Main Camera and assign the car in "Car Target".
/// Tested against the Built-in Input Manager (legacy Input) on Unity 2020.3.
/// </summary>
[RequireComponent(typeof(Camera))]
public class AdvanceCameraFollow : MonoBehaviour
{
    [Header("Target")]
    public Transform carTarget;
    [Tooltip("Optional. Found automatically on the target if left empty.")]
    public Rigidbody carRigidbody;
    [Tooltip("Point the camera aims at, relative to the car (roughly the roof).")]
    public Vector3 lookAtOffset = new Vector3(0f, 1f, 0f);

    [Header("Follow")]
    [Tooltip("Camera position relative to the car's heading. Z is negative = behind.")]
    public Vector3 moveOffset = new Vector3(0f, 3f, -7f);
    [Tooltip("Horizontal position lag in seconds. Bigger = more floaty.")]
    public float horizontalLag = 0.05f;
    [Tooltip("Vertical lag in seconds. Bigger = camera ignores bumps and hills more.")]
    public float verticalLag = 0.25f;
    [Tooltip("How fast the camera swings behind the car when it turns.")]
    public float rotSmoothness = 5f;
    [Tooltip("How fast the camera aims at the look target.")]
    public float aimSmoothness = 12f;
    [Tooltip("0 = camera follows car nose only, 1 = follows travel direction (better in drifts).")]
    [Range(0f, 1f)] public float velocityHeadingBlend = 0.5f;
    public float velocityHeadingMinSpeed = 4f;
    [Tooltip("If the car teleports (respawn) farther than this, the camera snaps.")]
    public float teleportDistance = 20f;

    [Header("Speed Effects")]
    [Tooltip("Speed (units/sec) at which speed effects are at full strength.")]
    public float maxSpeed = 30f;
    public float speedPullback = 2.5f;
    public float speedRise = 0.5f;
    [Tooltip("Extra field of view at full speed.")]
    public float fovBoost = 15f;
    public float speedEffectSmoothing = 3f;
    [Tooltip("Aim this far ahead of the car at full speed.")]
    public float lookAhead = 3f;
    [Tooltip("Aim sideways into corners. Scales with the car's turning rate.")]
    public float cornerLook = 1.5f;
    public float cornerLookMax = 3f;

    [Header("Banking")]
    [Tooltip("Degrees of camera roll per rad/s of turning. Negative flips the direction.")]
    public float bankAmount = 1.5f;
    public float maxBank = 4f;
    public float bankSmoothing = 4f;

    [Header("Obstacle Avoidance")]
    public bool avoidObstacles = true;
    [Tooltip("Exclude the car's own layer here if you can.")]
    public LayerMask obstacleMask = ~0;
    public float cameraRadius = 0.3f;
    [Range(0.05f, 1f)] public float minDistanceFactor = 0.15f;
    [Tooltip("How fast the camera returns to full distance after an obstacle clears.")]
    public float obstacleRecoverSpeed = 4f;

    [Header("Mouse Look (third person)")]
    public bool mouseLook = true;
    [Tooltip("Hides and locks the cursor while playing.")]
    public bool lockCursor = true;
    [Tooltip("Press to free the cursor. Click in the game to lock it again.")]
    public KeyCode unlockCursorKey = KeyCode.Escape;
    public float mouseSensitivityX = 3f;
    public float mouseSensitivityY = 2f;
    public bool invertY = false;
    public float minPitch = -10f;
    public float maxPitch = 55f;
    [Tooltip("Higher = snappier mouse response.")]
    public float lookSmoothing = 15f;
    [Tooltip("Swing back behind the car after the mouse has been idle. Turn off for a fully free camera.")]
    public bool autoRecenter = true;
    public float recenterDelay = 1.5f;
    public float recenterSpeed = 3f;
    [Tooltip("Only auto-recenter while the car is moving, so you can look around when parked.")]
    public bool recenterOnlyWhenMoving = true;

    [Header("Look Behind")]
    public KeyCode lookBehindKey = KeyCode.C;

    [Header("Shake")]
    public bool shakeOnImpact = true;
    [Tooltip("Sudden velocity change in one physics step that counts as an impact.")]
    public float impactThreshold = 3f;
    [Tooltip("Velocity change that gives maximum shake.")]
    public float impactMaxChange = 12f;
    public float traumaDecay = 1.5f;
    public float maxShakePosition = 0.3f;
    public float maxShakeAngle = 2f;
    public float shakeFrequency = 25f;
    [Tooltip("Constant light rumble near top speed.")]
    [Range(0f, 1f)] public float speedShake = 0.08f;

    // Runtime state
    Camera cam;
    float baseFov;
    bool initialized;

    Vector3 smoothedPivot;
    Vector3 pivotVelocityXZ;
    float pivotVelocityY;

    float currentYaw;
    float orbitYaw;
    float orbitPitch;
    float lookYaw;
    float lookPitch;
    float lastMouseTime;

    float smoothedSpeedT;
    float smoothedAngY;
    float smoothedRoll;
    float obstacleFactor = 1f;
    float trauma;

    Vector3 lastCarForward = Vector3.forward;
    Vector3 lastTargetPos;
    Vector3 lastVelocity;
    Vector3 planarVelocity;
    float planarSpeed;

    readonly RaycastHit[] hits = new RaycastHit[8];

    static float Damp(float dt, float rate)
    {
        return 1f - Mathf.Exp(-rate * dt);
    }

    void Awake()
    {
        cam = GetComponent<Camera>();
        baseFov = cam.fieldOfView;
        FindRigidbody();
    }

    void Start()
    {
        Snap();
        SetCursorLocked(lockCursor);
    }

    void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void HandleCursor()
    {
        if (!lockCursor) return;

        if (Input.GetKeyDown(unlockCursorKey))
            SetCursorLocked(false);
        else if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
            SetCursorLocked(true);
    }

    void FindRigidbody()
    {
        if (carTarget != null && carRigidbody == null)
        {
            carRigidbody = carTarget.GetComponent<Rigidbody>();
            if (carRigidbody == null) carRigidbody = carTarget.GetComponentInParent<Rigidbody>();
        }
    }

    /// <summary>Call this after respawning or teleporting the car.</summary>
    public void Snap()
    {
        if (carTarget == null) return;
        FindRigidbody();

        Vector3 f = carTarget.forward;
        f.y = 0f;
        lastCarForward = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
        currentYaw = Mathf.Atan2(lastCarForward.x, lastCarForward.z) * Mathf.Rad2Deg;

        orbitYaw = 0f;
        orbitPitch = 0f;
        lookYaw = 0f;
        lookPitch = 0f;
        lastMouseTime = Time.time;
        smoothedSpeedT = 0f;
        smoothedAngY = 0f;
        smoothedRoll = 0f;
        obstacleFactor = 1f;
        trauma = 0f;
        pivotVelocityXZ = Vector3.zero;
        pivotVelocityY = 0f;

        smoothedPivot = carTarget.position;
        lastTargetPos = carTarget.position;
        lastVelocity = carRigidbody != null ? carRigidbody.velocity : Vector3.zero;

        Quaternion yawRot = Quaternion.Euler(0f, currentYaw, 0f);
        Vector3 pos = smoothedPivot + yawRot * moveOffset;
        transform.position = pos;
        transform.rotation = Quaternion.LookRotation(smoothedPivot + lookAtOffset - pos, Vector3.up);
        if (cam != null) cam.fieldOfView = baseFov;

        initialized = true;
    }

    /// <summary>Add screen shake (0-1). Other scripts can call this, e.g. on landing.</summary>
    public void AddTrauma(float amount)
    {
        trauma = Mathf.Clamp01(trauma + amount);
    }

    void FixedUpdate()
    {
        if (!shakeOnImpact || carRigidbody == null) return;

        Vector3 v = carRigidbody.velocity;
        float change = (v - lastVelocity).magnitude;
        lastVelocity = v;

        if (change > impactThreshold)
        {
            AddTrauma(Mathf.Clamp01(change / impactMaxChange));
        }
    }

    void LateUpdate()
    {
        if (carTarget == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        if (!initialized) Snap();

        float tp = teleportDistance;
        if ((carTarget.position - smoothedPivot).sqrMagnitude > tp * tp) Snap();

        UpdateKinematics(dt);
        UpdateHeading(dt);
        UpdateLookInput(dt);
        UpdatePivot();
        ApplyTransform(dt);
        ApplyFov();
    }

    void UpdateKinematics(float dt)
    {
        Vector3 vel;
        if (carRigidbody != null) vel = carRigidbody.velocity;
        else vel = (carTarget.position - lastTargetPos) / dt;
        lastTargetPos = carTarget.position;

        planarVelocity = new Vector3(vel.x, 0f, vel.z);
        planarSpeed = planarVelocity.magnitude;

        float speedT = maxSpeed > 0.01f ? Mathf.Clamp01(planarSpeed / maxSpeed) : 0f;
        smoothedSpeedT = Mathf.Lerp(smoothedSpeedT, speedT, Damp(dt, speedEffectSmoothing));

        float angY = carRigidbody != null ? carRigidbody.angularVelocity.y : 0f;
        smoothedAngY = Mathf.Lerp(smoothedAngY, angY, Damp(dt, 6f));
    }

    void UpdateHeading(float dt)
    {
        // Only the car's yaw is used, so flips, pitch and roll don't swing the camera.
        Vector3 fwd = carTarget.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude > 0.01f) lastCarForward = fwd.normalized;

        Vector3 heading = lastCarForward;

        // Blend toward the travel direction while moving forward (helps in drifts).
        // Reversing keeps the camera behind the car's nose.
        if (planarSpeed > velocityHeadingMinSpeed && Vector3.Dot(planarVelocity, lastCarForward) > 0f)
        {
            float w = velocityHeadingBlend * Mathf.Clamp01((planarSpeed - velocityHeadingMinSpeed) / velocityHeadingMinSpeed);
            heading = Vector3.Slerp(lastCarForward, planarVelocity / planarSpeed, w);
        }

        float targetYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
        currentYaw = Mathf.LerpAngle(currentYaw, targetYaw, Damp(dt, rotSmoothness));
    }

    void UpdateLookInput(float dt)
    {
        HandleCursor();

        bool cursorReady = !lockCursor || Cursor.lockState == CursorLockMode.Locked;

        float mx = 0f;
        float my = 0f;
        if (mouseLook && cursorReady)
        {
            mx = Input.GetAxis("Mouse X");
            my = Input.GetAxis("Mouse Y");
        }

        if (Mathf.Abs(mx) > 0.001f || Mathf.Abs(my) > 0.001f)
        {
            // Mouse offsets the camera on top of the car-following heading.
            orbitYaw += mx * mouseSensitivityX;
            orbitYaw = Mathf.Repeat(orbitYaw + 180f, 360f) - 180f;
            orbitPitch += (invertY ? my : -my) * mouseSensitivityY;
            orbitPitch = Mathf.Clamp(orbitPitch, minPitch, maxPitch);
            lastMouseTime = Time.time;
        }
        else if (autoRecenter
                 && Time.time - lastMouseTime > recenterDelay
                 && (!recenterOnlyWhenMoving || planarSpeed > velocityHeadingMinSpeed))
        {
            float k = Damp(dt, recenterSpeed);
            orbitYaw = Mathf.LerpAngle(orbitYaw, 0f, k);
            orbitPitch = Mathf.Lerp(orbitPitch, 0f, k);
        }

        bool behind = Input.GetKey(lookBehindKey);
        float targetYaw = behind ? 180f : orbitYaw;
        float targetPitch = behind ? 0f : orbitPitch;
        float s = Damp(dt, lookSmoothing);
        lookYaw = Mathf.LerpAngle(lookYaw, targetYaw, s);
        lookPitch = Mathf.Lerp(lookPitch, targetPitch, s);
    }

    void UpdatePivot()
    {
        Vector3 raw = carTarget.position;

        // Horizontal follows tightly, vertical is smoothed more so bumps don't bounce the camera.
        Vector3 flatCurrent = new Vector3(smoothedPivot.x, 0f, smoothedPivot.z);
        Vector3 flatTarget = new Vector3(raw.x, 0f, raw.z);
        Vector3 flat = Vector3.SmoothDamp(flatCurrent, flatTarget, ref pivotVelocityXZ, horizontalLag);
        float y = Mathf.SmoothDamp(smoothedPivot.y, raw.y, ref pivotVelocityY, verticalLag);

        smoothedPivot = new Vector3(flat.x, y, flat.z);
    }

    void ApplyTransform(float dt)
    {
        Quaternion carYawRot = Quaternion.Euler(0f, currentYaw, 0f);
        Quaternion camYawRot = Quaternion.Euler(0f, currentYaw + lookYaw, 0f);

        // Camera position: pulls back and rises with speed.
        Vector3 offset = moveOffset;
        offset.z -= speedPullback * smoothedSpeedT;
        offset.y += speedRise * smoothedSpeedT;

        Vector3 origin = smoothedPivot + lookAtOffset;
        Quaternion orbitRot = camYawRot * Quaternion.Euler(lookPitch, 0f, 0f);
        Vector3 desiredPos = origin + orbitRot * (offset - lookAtOffset);

        Vector3 toCam = desiredPos - origin;
        float fullDist = toCam.magnitude;
        Vector3 dir = fullDist > 0.001f ? toCam / fullDist : -transform.forward;

        // Obstacle avoidance: snap in fast, ease back out slowly.
        float targetFactor = 1f;
        if (avoidObstacles && fullDist > 0.001f)
        {
            int count = Physics.SphereCastNonAlloc(origin, cameraRadius, dir, hits, fullDist,
                obstacleMask, QueryTriggerInteraction.Ignore);

            float nearest = fullDist;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].collider.transform.IsChildOf(carTarget)) continue;
                if (hits[i].distance < nearest) nearest = hits[i].distance;
            }
            targetFactor = Mathf.Clamp(nearest / fullDist, minDistanceFactor, 1f);
        }

        if (targetFactor < obstacleFactor) obstacleFactor = targetFactor;
        else obstacleFactor = Mathf.Lerp(obstacleFactor, targetFactor, Damp(dt, obstacleRecoverSpeed));

        Vector3 finalPos = origin + dir * (fullDist * obstacleFactor);

        // Aim point: ahead of the car and into corners. Ignored when looking behind.
        float aheadWeight = Mathf.Max(0f, Mathf.Cos(lookYaw * Mathf.Deg2Rad));
        Vector3 carForward = carYawRot * Vector3.forward;
        Vector3 carRight = carYawRot * Vector3.right;
        float side = Mathf.Clamp(smoothedAngY * cornerLook, -cornerLookMax, cornerLookMax);

        Vector3 lookTarget = origin
            + carForward * (lookAhead * smoothedSpeedT * aheadWeight)
            + carRight * (side * aheadWeight);

        Quaternion targetRot = Quaternion.LookRotation(lookTarget - finalPos, Vector3.up);

        // Lean the camera into turns. If it tilts the wrong way, negate Bank Amount.
        float targetRoll = Mathf.Clamp(-smoothedAngY * bankAmount, -maxBank, maxBank);
        smoothedRoll = Mathf.Lerp(smoothedRoll, targetRoll, Damp(dt, bankSmoothing));
        targetRot = targetRot * Quaternion.Euler(0f, 0f, smoothedRoll);

        Quaternion smoothedRot = Quaternion.Slerp(transform.rotation, targetRot, Damp(dt, aimSmoothness));

        // Shake (trauma squared feels more natural than linear).
        trauma = Mathf.Max(0f, trauma - traumaDecay * dt);
        float speedRumble = speedShake * Mathf.Clamp01((smoothedSpeedT - 0.7f) / 0.3f);
        float shake = Mathf.Clamp01(trauma + speedRumble);
        shake *= shake;

        Vector3 posNoise = Vector3.zero;
        Quaternion rotNoise = Quaternion.identity;
        if (shake > 0.0001f)
        {
            float t = Time.time * shakeFrequency;
            posNoise = new Vector3(Noise(t, 0f), Noise(t, 1f), Noise(t, 2f)) * (shake * maxShakePosition);
            rotNoise = Quaternion.Euler(
                Noise(t, 3f) * shake * maxShakeAngle,
                Noise(t, 4f) * shake * maxShakeAngle,
                Noise(t, 5f) * shake * maxShakeAngle);
        }

        transform.rotation = smoothedRot * rotNoise;
        transform.position = finalPos + smoothedRot * posNoise;
    }

    void ApplyFov()
    {
        cam.fieldOfView = baseFov + fovBoost * smoothedSpeedT;
    }

    static float Noise(float t, float seed)
    {
        return Mathf.PerlinNoise(t, seed * 17.13f) * 2f - 1f;
    }
}
