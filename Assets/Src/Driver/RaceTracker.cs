using System;
using UnityEngine;

/// <summary>
/// Quản lý lap tracking, checkpoints, và race state
/// </summary>
public class RaceTracker
{
    private int laps = 0;
    private float lapTime = 0f;
    private bool isOnTrack = true;
    
    public int Laps => laps;
    public float LapTime => lapTime;
    public bool IsOnTrack => isOnTrack;
    
    public event Action<int> OnLapsChanged;
    
    /// <summary>
    /// Cập nhật số lap
    /// </summary>
    public void IncrementLap()
    {
        if (laps == 0)
        {
            lapTime = 0f;
        }
        else
        {
            // Tính lap time từ race time
            // (phần này do game manager handle, ta chỉ trigger event)
        }
        
        laps++;
        OnLapsChanged?.Invoke(laps);
    }
    
    /// <summary>
    /// Set lap time (từ GameManager)
    /// </summary>
    public void SetLapTime(float time)
    {
        lapTime = time;
    }
    
    /// <summary>
    /// Update track status
    /// </summary>
    public void SetOnTrack(bool onTrack)
    {
        isOnTrack = onTrack;
    }
    
    /// <summary>
    /// Reset race tracker (cho lúc start race)
    /// </summary>
    public void Reset()
    {
        laps = 0;
        lapTime = 0f;
        isOnTrack = true;
    }
    
    /// <summary>
    /// Check nếu player đã hoàn thành race
    /// </summary>
    public bool HasFinishedRace(int numLaps)
    {
        return laps > numLaps;
    }
}
