using UnityEngine;

public class CarMovement
{
    // Arcade Movement Settings
    [SerializeField] public float maxSpeed = 40f;
    [SerializeField] public float maxReverseSpeed = 20f;
    [SerializeField] public float accelerationRate = 20f;
    [SerializeField] public float decelerationRate = 10f;
    [SerializeField] public float brakeRate = 30f;
    [SerializeField] public float turnSpeed = 120f;
    
    private float currentSpeed = 0f;
    
    public float CurrentSpeed => currentSpeed;
    
    public void SetMovementParameters(float maxSpd, float maxRevSpd, float accelRate, 
                                      float decelRate, float brkRate, float trnSpeed)
    {
        maxSpeed = maxSpd;
        maxReverseSpeed = maxRevSpd;
        accelerationRate = accelRate;
        decelerationRate = decelRate;
        brakeRate = brkRate;
        turnSpeed = trnSpeed;
    }
    
    public void SetCurrentSpeed(float speed)
    {
        currentSpeed = speed;
    }
    
    public StatePayload SimulateMovement(
        Vector3 startPos,
        Quaternion startRot,
        float startSpeed,
        InputPayload input,
        float dt,
        float rubberBandMultiplier = 1f)
    {
        Vector3 pos = startPos;
        Quaternion rot = startRot;
        float speed = startSpeed;
        
        // Clamp input values
        float accel = Mathf.Clamp(input.inputAcceleration, -1f, 1f);
        float steer = Mathf.Clamp(input.inputSteering, -1f, 1f);
        float brake = Mathf.Clamp(input.inputBrake, 0f, 1f);
        
        // Update speed based on acceleration
        if (Mathf.Abs(accel) > 0.01f)
        {
            speed += accel * accelerationRate * dt;
        }
        else
        {
            speed = Mathf.Lerp(speed, 0f, decelerationRate * dt);
        }
        
        // Apply brake
        if (brake > 0.1f)
        {
            speed = Mathf.Lerp(speed, 0f, brakeRate * dt);
        }
        
        // Apply rubber band multiplier
        float currentMaxForward = maxSpeed * rubberBandMultiplier;
        speed = Mathf.Clamp(speed, -maxReverseSpeed, currentMaxForward);
        
        // Apply rotation
        if (Mathf.Abs(speed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(speed);
            float turnAmount = steer * turnSpeed * directionMultiplier * dt;
            Quaternion deltaRot = Quaternion.Euler(0f, turnAmount, 0f);
            rot *= deltaRot;
        }
        
        // Update position
        pos += rot * Vector3.forward * speed * dt;
        
        currentSpeed = speed;
        
        return new StatePayload
        {
            tick = input.tick,
            position = pos,
            rotation = rot,
            speed = speed
        };
    }
    
    public float ClampSpeed(float speed)
    {
        float maxSpd = Mathf.Max(10f, maxSpeed * 1.2f);
        return Mathf.Clamp(speed, -maxSpd, maxSpd);
    }
    
    public bool IsNearlyStopped(float speed, float otherSpeed = 0f)
    {
        const float LOW_SPEED_THRESHOLD = 0.15f;
        return Mathf.Abs(speed) < LOW_SPEED_THRESHOLD && Mathf.Abs(otherSpeed) < LOW_SPEED_THRESHOLD;
    }
    
    public void ResetSpeed()
    {
        currentSpeed = 0f;
    }
}
