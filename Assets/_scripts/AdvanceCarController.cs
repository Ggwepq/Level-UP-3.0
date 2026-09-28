using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class AdvanceCarController : MonoBehaviour
{
    public enum Axel
    {
        Front,
        Rear
    }

    public enum DriveType
    {
        AllWheel,
        FrontWheel,
        RearWheel
    }

    [Serializable]
    public struct Wheel
    {
        public GameObject wheelModel;
        public WheelCollider wheelCollider;
        public GameObject wheelEffectObj;
        public Axel axel;
    }

    [Header("Wheels")]
    public List<Wheel> wheels;
    public Vector3 _centerOfMass;

    [Header("Engine")]
    public DriveType driveType = DriveType.AllWheel;
    [Tooltip("Motor torque per driven wheel.")]
    public float motorTorque = 500f;
    public float maxSpeedKmh = 120f;
    public float reverseSpeedKmh = 40f;

    [Header("Brakes")]
    [Tooltip("Applied when you press the opposite direction to the way you're moving.")]
    public float brakeTorque = 1000f;
    public KeyCode handbrakeKey = KeyCode.Space;
    [Tooltip("Handbrake acts on the rear wheels only.")]
    public float handbrakeTorque = 1500f;
    [Tooltip("Light braking when you release the throttle.")]
    public float engineBrakeTorque = 80f;

    [Header("Steering")]
    public float turnSensitivity = 1f;
    public float maxSteerAngle = 30f;
    [Tooltip("Degrees per second the wheels turn.")]
    public float steerSpeed = 180f;
    [Tooltip("Steering is scaled down to this fraction at top speed.")]
    [Range(0.1f, 1f)] public float highSpeedSteerFactor = 0.4f;

    [Header("Handbrake Drift")]
    [Tooltip("Rear sideways grip while the handbrake is held. Lower = more drift.")]
    [Range(0.1f, 1f)] public float handbrakeSidewaysGrip = 0.6f;
    public float gripChangeSpeed = 4f;

    [Header("Stability")]
    [Tooltip("Resists body roll. 0 disables it.")]
    public float antiRollForce = 5000f;
    [Tooltip("Extra push toward the ground per m/s of speed.")]
    public float downforce = 20f;

    [Header("Skid Effects")]
    [Tooltip("Sideways slip above this counts as skidding.")]
    public float skidSlipThreshold = 0.5f;
    [Tooltip("Forward slip (wheelspin or lock-up) above this counts as skidding.")]
    public float skidForwardSlipThreshold = 0.8f;
    public float minSkidSpeedKmh = 5f;

    [Header("Debug")]
    public KeyCode resetKey = KeyCode.R;

    // Read these from other scripts (audio, camera, UI).
    public float SpeedKmh { get { return speedKmh; } }
    public float ForwardSpeed { get { return forwardSpeed; } }
    public bool IsGrounded { get { return isGrounded; } }
    public bool IsHandbraking { get { return handbrake; } }
    public bool IsBraking { get { return footBraking || handbrake; } }
    public bool IsSkidding { get; private set; }

    Rigidbody carRb;

    float moveInput;
    float steerInput;
    bool handbrake;
    bool footBraking;

    float speedKmh;
    float forwardSpeed;
    bool isGrounded;

    float currentSteer;
    float rearGrip = 1f;
    float appliedRearGrip = 1f;

    TrailRenderer[] trails;
    bool[] trailActive;
    float[] baseSidewaysStiffness;

    WheelCollider frontLeft, frontRight, rearLeft, rearRight;
    bool hasFrontPair, hasRearPair;

    void Start()
    {
        carRb = GetComponent<Rigidbody>();
        carRb.centerOfMass = _centerOfMass;
        carRb.interpolation = RigidbodyInterpolation.Interpolate; // smoother camera follow

        int count = wheels.Count;
        trails = new TrailRenderer[count];
        trailActive = new bool[count];
        baseSidewaysStiffness = new float[count];

        for (int i = 0; i < count; i++)
        {
            Wheel wheel = wheels[i];

            // Cache once instead of calling GetComponentInChildren every frame.
            if (wheel.wheelEffectObj != null)
            {
                trails[i] = wheel.wheelEffectObj.GetComponentInChildren<TrailRenderer>();
                if (trails[i] != null) trails[i].emitting = false;
            }

            if (wheel.wheelCollider != null)
                baseSidewaysStiffness[i] = wheel.wheelCollider.sidewaysFriction.stiffness;
        }

        hasFrontPair = TryFindPair(Axel.Front, out frontLeft, out frontRight);
        hasRearPair = TryFindPair(Axel.Rear, out rearLeft, out rearRight);
    }

    void Update()
    {
        GetInputs();
        AnimateWheels();
        UpdateWheelEffects();

        if (Input.GetKeyDown(resetKey)) ResetUpright();
    }

    // All physics runs in FixedUpdate so it doesn't depend on frame rate.
    void FixedUpdate()
    {
        UpdateState();
        DriveAndBrake();
        Steer();
        UpdateHandbrakeGrip();
        ApplyStability();
    }

    void GetInputs()
    {
        moveInput = Input.GetAxis("Vertical");
        steerInput = Input.GetAxis("Horizontal");
        handbrake = Input.GetKey(handbrakeKey);
    }

    void UpdateState()
    {
        forwardSpeed = Vector3.Dot(carRb.velocity, transform.forward);
        speedKmh = Mathf.Abs(forwardSpeed) * 3.6f;

        isGrounded = false;
        foreach (var wheel in wheels)
        {
            if (wheel.wheelCollider != null && wheel.wheelCollider.isGrounded)
            {
                isGrounded = true;
                break;
            }
        }
    }

    bool IsDriven(Axel axel)
    {
        switch (driveType)
        {
            case DriveType.FrontWheel: return axel == Axel.Front;
            case DriveType.RearWheel: return axel == Axel.Rear;
            default: return true;
        }
    }

    void DriveAndBrake()
    {
        bool throttle = Mathf.Abs(moveInput) > 0.05f;

        // Pressing the opposite direction to how you're moving brakes instead of reversing.
        bool opposing = throttle && speedKmh > 2f && Mathf.Sign(moveInput) != Mathf.Sign(forwardSpeed);
        footBraking = opposing;

        // Torque fades out as you near the speed limit.
        float limit = moveInput >= 0f ? maxSpeedKmh : reverseSpeedKmh;
        float ratio = Mathf.Clamp01(speedKmh / Mathf.Max(limit, 1f));
        float falloff = 1f - ratio * ratio * ratio;

        float torque = (throttle && !opposing) ? moveInput * motorTorque * falloff : 0f;

        foreach (var wheel in wheels)
        {
            if (wheel.wheelCollider == null) continue;

            wheel.wheelCollider.motorTorque = IsDriven(wheel.axel) ? torque : 0f;

            float brake = 0f;
            if (opposing)
                brake = brakeTorque * Mathf.Abs(moveInput);
            else if (!throttle)
                brake = speedKmh < 1f ? brakeTorque : engineBrakeTorque; // hold still on slopes

            if (handbrake && wheel.axel == Axel.Rear)
                brake = Mathf.Max(brake, handbrakeTorque);

            wheel.wheelCollider.brakeTorque = brake;
        }
    }

    void Steer()
    {
        float speedFactor = Mathf.Lerp(1f, highSpeedSteerFactor, Mathf.Clamp01(speedKmh / Mathf.Max(maxSpeedKmh, 1f)));
        float target = steerInput * turnSensitivity * maxSteerAngle * speedFactor;

        currentSteer = Mathf.MoveTowards(currentSteer, target, steerSpeed * Time.fixedDeltaTime);

        foreach (var wheel in wheels)
        {
            if (wheel.axel == Axel.Front && wheel.wheelCollider != null)
                wheel.wheelCollider.steerAngle = currentSteer;
        }
    }

    void UpdateHandbrakeGrip()
    {
        float target = handbrake ? handbrakeSidewaysGrip : 1f;
        rearGrip = Mathf.MoveTowards(rearGrip, target, gripChangeSpeed * Time.fixedDeltaTime);

        if (Mathf.Approximately(rearGrip, appliedRearGrip)) return;
        appliedRearGrip = rearGrip;

        for (int i = 0; i < wheels.Count; i++)
        {
            if (wheels[i].axel != Axel.Rear || wheels[i].wheelCollider == null) continue;

            WheelFrictionCurve friction = wheels[i].wheelCollider.sidewaysFriction;
            friction.stiffness = baseSidewaysStiffness[i] * rearGrip;
            wheels[i].wheelCollider.sidewaysFriction = friction;
        }
    }

    void ApplyStability()
    {
        if (isGrounded && downforce > 0f)
            carRb.AddForce(-transform.up * downforce * carRb.velocity.magnitude);

        if (antiRollForce > 0f)
        {
            if (hasFrontPair) ApplyAntiRoll(frontLeft, frontRight);
            if (hasRearPair) ApplyAntiRoll(rearLeft, rearRight);
        }
    }

    void ApplyAntiRoll(WheelCollider left, WheelCollider right)
    {
        float travelL = 1f;
        float travelR = 1f;
        WheelHit hit;

        bool groundedL = left.GetGroundHit(out hit);
        if (groundedL)
            travelL = (-left.transform.InverseTransformPoint(hit.point).y - left.radius) / left.suspensionDistance;

        bool groundedR = right.GetGroundHit(out hit);
        if (groundedR)
            travelR = (-right.transform.InverseTransformPoint(hit.point).y - right.radius) / right.suspensionDistance;

        float force = (travelL - travelR) * antiRollForce;

        if (groundedL) carRb.AddForceAtPosition(left.transform.up * -force, left.transform.position);
        if (groundedR) carRb.AddForceAtPosition(right.transform.up * force, right.transform.position);
    }

    bool TryFindPair(Axel axel, out WheelCollider left, out WheelCollider right)
    {
        left = null;
        right = null;
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        int count = 0;

        foreach (var wheel in wheels)
        {
            if (wheel.axel != axel || wheel.wheelCollider == null) continue;
            count++;

            float x = transform.InverseTransformPoint(wheel.wheelCollider.transform.position).x;
            if (x < minX) { minX = x; left = wheel.wheelCollider; }
            if (x > maxX) { maxX = x; right = wheel.wheelCollider; }
        }

        return count == 2 && left != right;
    }

    void AnimateWheels()
    {
        foreach (var wheel in wheels)
        {
            if (wheel.wheelCollider == null || wheel.wheelModel == null) continue;

            Vector3 pos;
            Quaternion rot;
            wheel.wheelCollider.GetWorldPose(out pos, out rot);
            wheel.wheelModel.transform.SetPositionAndRotation(pos, rot);
        }
    }

    void UpdateWheelEffects()
    {
        bool anySkid = false;

        for (int i = 0; i < wheels.Count; i++)
        {
            bool skid = IsWheelSkidding(wheels[i]);
            anySkid |= skid;

            TrailRenderer trail = trails[i];
            if (trail == null || skid == trailActive[i]) continue;

            // Clear on start so the trail doesn't stretch from where it last stopped.
            if (skid) trail.Clear();
            trail.emitting = skid;
            trailActive[i] = skid;
        }

        IsSkidding = anySkid;
    }

    bool IsWheelSkidding(Wheel wheel)
    {
        if (wheel.wheelCollider == null) return false;

        // Wheel in the air = no skid marks.
        WheelHit hit;
        if (!wheel.wheelCollider.GetGroundHit(out hit)) return false;

        if (speedKmh < minSkidSpeedKmh) return false;

        if (handbrake && wheel.axel == Axel.Rear) return true;

        return Mathf.Abs(hit.sidewaysSlip) > skidSlipThreshold
            || Mathf.Abs(hit.forwardSlip) > skidForwardSlipThreshold;
    }

    /// <summary>Flips the car upright in place. Handy when testing.</summary>
    public void ResetUpright()
    {
        carRb.velocity = Vector3.zero;
        carRb.angularVelocity = Vector3.zero;
        transform.position += Vector3.up * 1f;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }
}
