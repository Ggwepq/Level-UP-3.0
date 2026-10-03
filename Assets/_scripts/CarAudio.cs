using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CarAudio : MonoBehaviour
{

    [Header("Engine Sound")]
    public float minSpeed;
    public float maxSpeed;
    private float currentSpeed;


    public float minPitch;
    public float maxPitch;
    private float pitchFromCar;


    [Header("Impact Sound")]
    public AudioClip crashSound;
    public AudioClip bumpSound;
    public float hardImpactThreshold = 10f;
    public float minImpactForce = 1.5f;


    [Header("Impact Sound")]
    public AudioClip brakeSound;
    public float brakeDecelerationThreshold = 8f;
    private AudioSource brakeAudioSource;
    private float previousSpeed;
    private bool isBraking;

    private Rigidbody carRb;
    private AudioSource carSound;
    private AudioSource impactSound;

    void Start()
    {
        carRb = GetComponent<Rigidbody>();

        AudioSource[] sources = GetComponents<AudioSource>();
        carSound = sources[0];
        impactSound = sources.Length > 1 ? sources[1] : gameObject.AddComponent<AudioSource>();
        brakeAudioSource = sources.Length > 2 ? sources[2] : gameObject.AddComponent<AudioSource>();

        brakeAudioSource.clip = brakeSound;
        brakeAudioSource.loop = true;
        previousSpeed = 0f;
    }

    void Update()
    {
        EngineSound();
        BrakeSound();
        previousSpeed = currentSpeed;
    }

    void EngineSound()
    {
        currentSpeed = carRb.linearVelocity.magnitude;
        pitchFromCar = currentSpeed / 20f;

        if (currentSpeed < minSpeed)
        {
            carSound.pitch = minPitch;
        }
        else if (currentSpeed < maxSpeed)
        {
            carSound.pitch = Mathf.Clamp(minPitch + pitchFromCar, minPitch, maxPitch);
        }
        else
        {
            carSound.pitch = maxPitch;
        }
    }


    void OnCollisionEnter(Collision collision)
    {
        // float impactForce = collision.relativeVelocity.magnitude;


        float impactForce = 0f;

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);

            // Skip contacts that came from wheel colliders
            if (contact.thisCollider.CompareTag("Wheel"))
                continue;

            impactForce = Mathf.Max(impactForce, collision.impulse.magnitude / Time.fixedDeltaTime);
        }

        Debug.Log(impactForce);


        if (impactForce < minImpactForce) return;

        if (impactForce >= hardImpactThreshold)
        {
            impactSound.pitch = 1f;
            impactSound.PlayOneShot(crashSound, 1f);
        }
        else
        {
            float volume = Mathf.Clamp01(impactForce / hardImpactThreshold);
            impactSound.PlayOneShot(bumpSound, volume);
        }
    }

    void BrakeSound()
    {
        float deceleration = (previousSpeed - currentSpeed) / Time.deltaTime;

        bool shouldBrake = deceleration > brakeDecelerationThreshold && currentSpeed > minSpeed;


        if (shouldBrake && !isBraking)
        {
            brakeAudioSource.Play();
            isBraking = true;
        }
        else if (!shouldBrake && isBraking)
        {
            brakeAudioSource.Stop();
            isBraking = false;
        }
    }

}
