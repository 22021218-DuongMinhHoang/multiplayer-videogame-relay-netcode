using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quản lý server reconciliation: phát hiện sai lệch và rewind/fast-forward
/// </summary>
public class ServerReconciliation
{
    // Reconciliation thresholds
    private const float RECONCILE_POS_THRESHOLD = 1.5f;
    private const float RECONCILE_ROT_THRESHOLD = 1.0f;
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
    
    /// <summary>
    /// Ghi nhận server state mới
    /// </summary>
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
    
    /// <summary>
    /// Tính toán error giữa predicted state và server state
    /// </summary>
    public (float positionError, float rotationError) CalculateErrors(StatePayload predicted, StatePayload server)
    {
        float posError = Vector3.Distance(server.position, predicted.position);
        float rotError = Quaternion.Angle(server.rotation, predicted.rotation);
        return (posError, rotError);
    }
    
    /// <summary>
    /// Check nếu cần rewind và reconcile
    /// </summary>
    public bool ShouldReconcile(float posError, float rotError, float currentSpeed, float serverSpeed)
    {
        bool isNearlyStopped = Mathf.Abs(currentSpeed) < LOW_SPEED_THRESHOLD && 
                              Mathf.Abs(serverSpeed) < LOW_SPEED_THRESHOLD;
        
        bool errorsLarge = posError > RECONCILE_POS_THRESHOLD || rotError > RECONCILE_ROT_THRESHOLD;
        
        return errorsLarge && !isRewinding && !isNearlyStopped;
    }
    
    /// <summary>
    /// Check nếu sai số rất lớn (cần snap cứng)
    /// </summary>
    public bool ShouldHardSnap(float posError, float rotError)
    {
        return posError > RECONCILE_POS_THRESHOLD * 2f || rotError > RECONCILE_ROT_THRESHOLD * 2f;
    }
    
    /// <summary>
    /// Bắt đầu rewind
    /// </summary>
    public void StartRewinding()
    {
        isRewinding = true;
    }
    
    /// <summary>
    /// Kết thúc rewind
    /// </summary>
    public void FinishRewinding()
    {
        isRewinding = false;
    }
    
    /// <summary>
    /// Cập nhật last processed state sau rewind xong
    /// </summary>
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
    
    /// <summary>
    /// Ghi nhận error metrics
    /// </summary>
    public void RecordError(float error)
    {
        aeeSum += error;
        aeeCount++;
        if (error <= hitThreshold)
            hitCount++;
    }
    
    /// <summary>
    /// Reset metrics
    /// </summary>
    public void ResetMetrics()
    {
        aeeSum = 0.0;
        aeeCount = 0;
        hitCount = 0;
    }
    
    /// <summary>
    /// Reset state
    /// </summary>
    public void Reset()
    {
        latestServerState = new StatePayload();
        lastProcessedState = new StatePayload();
        isRewinding = false;
        ResetMetrics();
    }
    
    /// <summary>
    /// Kiểm tra nếu latest server state và predicted state giống nhau
    /// </summary>
    public bool StatesAreEquivalent(StatePayload state1, StatePayload state2)
    {
        if (state1.tick != state2.tick)
            return false;
        return state1.Equals(state2);
    }
}
