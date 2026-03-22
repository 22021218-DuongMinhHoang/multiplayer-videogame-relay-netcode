using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ServerReconciliation
{
    private const float RECONCILE_POS_THRESHOLD = 2.5f;
    private const float RECONCILE_ROT_THRESHOLD = 5.0f;
    private const float RECONCILE_LERP_TIME = 0.2f;
    private const float LOW_SPEED_THRESHOLD = 0.15f;
    
    private StatePayload latestServerState = new StatePayload();
    private StatePayload lastProcessedState = new StatePayload();
    private bool isRewinding = false;
    
    // Metrics
    private double aeeSum = 0.0;
    private long aeeCount = 0;
    private long hitCount = 0;
    private float hitThreshold = 0.5f;
    
    public bool IsRewinding => isRewinding;
    public StatePayload LatestServerState => latestServerState;
    public StatePayload LastProcessedState => lastProcessedState;
    public double AverageExportError => aeeCount > 0 ? aeeSum / aeeCount : 0.0;
    public float HitPercentage => aeeCount > 0 ? (hitCount * 100f / aeeCount) : 0f;
    
    public void RecordServerState(int tick, Vector3 pos, Quaternion rot, float speed)
    {
        latestServerState = new StatePayload
        {
            tick = tick,
            position = pos,
            rotation = rot,
            speed = speed
        };
    }
    
    public (float positionError, float rotationError) CalculateErrors(StatePayload predicted, StatePayload server)
    {
        float posError = Vector3.Distance(server.position, predicted.position);
        float rotError = Quaternion.Angle(server.rotation, predicted.rotation);
        return (posError, rotError);
    }
    
    public bool ShouldReconcile(float posError, float rotError, float currentSpeed, float serverSpeed)
    {
        bool isNearlyStopped = Mathf.Abs(currentSpeed) < LOW_SPEED_THRESHOLD && 
                              Mathf.Abs(serverSpeed) < LOW_SPEED_THRESHOLD;
        
        bool errorsLarge = posError > RECONCILE_POS_THRESHOLD || rotError > RECONCILE_ROT_THRESHOLD;
        
        return errorsLarge && !isRewinding && !isNearlyStopped;
    }
    
    public bool ShouldHardSnap(float posError, float rotError)
    {
        return posError > RECONCILE_POS_THRESHOLD * 2f || rotError > RECONCILE_ROT_THRESHOLD * 2f;
    }
    
    public void StartRewinding()
    {
        isRewinding = true;
    }
    
    public void FinishRewinding()
    {
        isRewinding = false;
    }
    
    public void UpdateLastProcessedState(int tick, Vector3 pos, Quaternion rot, float speed)
    {
        lastProcessedState = new StatePayload
        {
            tick = tick,
            position = pos,
            rotation = rot,
            speed = speed
        };
    }
    
    public void RecordError(float error)
    {
        aeeSum += error;
        aeeCount++;
        if (error <= hitThreshold)
            hitCount++;
    }
    
    public void ResetMetrics()
    {
        aeeSum = 0.0;
        aeeCount = 0;
        hitCount = 0;
    }
    
    public void Reset()
    {
        latestServerState = new StatePayload();
        lastProcessedState = new StatePayload();
        isRewinding = false;
        ResetMetrics();
    }
    public bool StatesAreEquivalent(StatePayload state1, StatePayload state2)
    {
        if (state1.tick != state2.tick)
            return false;
        return state1.Equals(state2);
    }
}
