using System;
using UnityEngine;
public class RaceTracker
{
    private int laps = 0;
    private float lapTime = 0f;
    private bool isOnTrack = true;
    
    public int Laps => laps;
    public float LapTime => lapTime;
    public bool IsOnTrack => isOnTrack;
    
    public event Action<int> OnLapsChanged;
    
    public void IncrementLap()
    {
        if (laps == 0)
        {
            lapTime = 0f;
        }
        else
        {
        }
        
        laps++;
        OnLapsChanged?.Invoke(laps);
    }
    
    public void SetLapTime(float time)
    {
        lapTime = time;
    }
    
    public void SetOnTrack(bool onTrack)
    {
        isOnTrack = onTrack;
    }
    
    public void Reset()
    {
        laps = 0;
        lapTime = 0f;
        isOnTrack = true;
    }
    public bool HasFinishedRace(int numLaps)
    {
        return laps > numLaps;
    }
}
