using System;
using System.Collections.Generic;
using CustomTypes;
using Unity.Netcode;
using UnityEngine;

[Serializable]
public class DeadReckoningSystem
{
    public enum DeadReckoningMode { None, Linear, Quadratic }
    public enum CorrectionMode { SmoothDamp, Lerp }
    
    private Vector3 serverPos = Vector3.zero;
    private Vector3 serverVel = Vector3.zero;
    private Vector3 serverAcc = Vector3.zero;
    private Vector3 prevServerVel = Vector3.zero;
    private float lastServerRecvTime = 0f;
    
    private Vector3 targetPos = Vector3.zero;
    private Vector3 vel = Vector3.zero;
    
    private float timeOffset = 0f;
    private bool timeOffsetInitialized = false;
    private const float SYNC_ALPHA = 0.05f;
    
    private Vector3 p0, p1, t0, t1;
    private float splineTimer = 0f;
    
    // Cấu hình
    private DeadReckoningMode currentDRMode = DeadReckoningMode.Quadratic;
    private CorrectionMode currentCorrectionMode = CorrectionMode.SmoothDamp;
    public bool UseCubicSpline { get; set; } = false;
    public bool UseAdaptiveThreshold { get; set; } = true;
    public bool UseTimeSync { get; set; } = true;
    
    // Metrics
    private List<float> packetIntervals = new List<float>();
    private float lastPacketLocalTime = 0f;
    
    public DeadReckoningMode CurrentDRMode => currentDRMode;
    public CorrectionMode CurrentCorrectionMode => currentCorrectionMode;
    public Vector3 TargetPos => targetPos;
    public Vector3 Vel => vel;
    
    public void OnServerStateReceived(Vector3 newPos, Vector3 newVel, Vector3 newAcc, float timestamp)
    {
        float now = Time.time;
        float packetTime = UseTimeSync ? timestamp : now;
        
        if (UseTimeSync)
        {
            if (lastServerRecvTime > 0f && timestamp <= lastServerRecvTime)
            {
                // Bỏ qua gói cũ hoặc out-of-order
                return;
            }

            float rtt = 0f;
            try { rtt = NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetCurrentRtt(AppConfig.Singleton.GAME.SERVER_ID) / 1000f; }
            catch { rtt = 0f; }
            
            if (rtt >= 0)
            {
                float estimatedServerNow = timestamp + rtt / 2f;
                float currentOffset = estimatedServerNow - now;
                if (!timeOffsetInitialized)
                {
                    timeOffset = currentOffset;
                    timeOffsetInitialized = true;
                }
                else
                {
                    timeOffset = Mathf.Lerp(timeOffset, currentOffset, SYNC_ALPHA);
                }
            }
        }
        
        float dt;
        if (!UseTimeSync)
        {
            dt = (lastServerRecvTime > 0f) ? now - lastServerRecvTime : 0.05f;
        }
        else
        {
            dt = (lastServerRecvTime > 0f) ? packetTime - lastServerRecvTime : 0.05f;
        }

        if (dt < 0.001f) dt = 0.05f;
        lastServerRecvTime = packetTime;
        
        serverPos = newPos;
        serverVel = newVel;
        serverAcc = (dt > 0.0001f) ? (newVel - prevServerVel) / dt : Vector3.zero;
        prevServerVel = newVel;
        
        float interval = (lastPacketLocalTime > 0f) ? now - lastPacketLocalTime : 0f;
        lastPacketLocalTime = now;
        if (packetIntervals.Count >= 20) 
            packetIntervals.RemoveAt(0);
        packetIntervals.Add(interval);
        
        //ResetSplineState(serverPos);
    }
    
    public void ResetSplineState(Vector3 pos)
    {
        p0 = pos;
        p1 = pos;
        t0 = Vector3.zero;
        t1 = Vector3.zero;
        serverPos = pos;
        splineTimer = 0f;
        vel = Vector3.zero;
        targetPos = pos;
    }
    
    public Vector3 CalculateTargetPosition(float smoothInterpolationTime)
    {
        float now = Time.time;
        float serverTimeNow = UseTimeSync ? (now + timeOffset) : now;

        float rawDelta = (lastServerRecvTime > 0f) ? serverTimeNow - lastServerRecvTime : 0f;
        float predictTime = Mathf.Clamp(rawDelta + 0.05f, 0f, 0.5f);
        
        Vector3 predicted = Vector3.zero;
        
        switch (currentDRMode)
        {
            case DeadReckoningMode.None:
                predicted = serverPos;
                break;
                
            case DeadReckoningMode.Linear:
                predicted = serverPos + serverVel * predictTime;
                break;
                
            case DeadReckoningMode.Quadratic:
                predicted = serverPos + serverVel * predictTime + 0.5f * serverAcc * predictTime * predictTime * 0.8f;
                break;
        }
        
        return predicted;
    }
    
    public Vector3 SmoothDampPosition(Vector3 currentPos, Vector3 targetPosParam, Vector3 velocity, float smoothTime, float deltaTime)
    {
        targetPos = targetPosParam;
        vel = velocity;
        return Vector3.SmoothDamp(currentPos, targetPos, ref vel, smoothTime, float.PositiveInfinity, deltaTime);
    }
    
    public Vector3 LerpPosition(Vector3 currentPos, Vector3 targetPosParam, float smoothTime, float deltaTime)
    {
        targetPos = targetPosParam;
        float t = deltaTime / Mathf.Max(smoothTime, 0.001f);
        return Vector3.Lerp(currentPos, targetPos, t);
    }
    
    public void SetDRMode(DeadReckoningMode mode)
    {
        currentDRMode = mode;
    }
    
    public void SetCorrectionMode(int index)
    {
        currentCorrectionMode = (CorrectionMode)index;
    }
    
    public float GetJitterEstimate()
    {
        if (packetIntervals.Count <= 1) return 0f;
        
        float avg = 0f;
        foreach (var interval in packetIntervals)
            avg += interval;
        avg /= packetIntervals.Count;
        
        float sumSq = 0f;
        foreach (var interval in packetIntervals)
            sumSq += (interval - avg) * (interval - avg);
        
        float stdDev = Mathf.Sqrt(sumSq / (packetIntervals.Count - 1));
        return stdDev * 1000f;
    }
    
    public void Reset(Vector3 initialPos)
    {
        serverPos = initialPos;
        serverVel = Vector3.zero;
        serverAcc = Vector3.zero;
        prevServerVel = Vector3.zero;
        lastServerRecvTime = Time.time;
        timeOffset = 0f;
        vel = Vector3.zero;
        targetPos = initialPos;
        packetIntervals.Clear();
        lastPacketLocalTime = Time.time;
        ResetSplineState(initialPos);
    }
}
