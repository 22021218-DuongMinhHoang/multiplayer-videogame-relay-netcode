using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[Serializable]
public class JerkCounter
{
    [SerializeField] int windowSize = 500;
    List<float> jerkList;
    Queue<float> jerkQueue = new();

    public float highestJerk { get; private set; }
    public float AverageValue => avgJerk;
    float avgJerk;
    
    
    public float Update(float jerk, bool updateHighestJerkUi = false)
    {
        if (jerkQueue.Count < windowSize)
        {
            if (jerkQueue.Count == 0)
            {
                avgJerk = jerk;
                jerkQueue.Enqueue(jerk);
            }
            else
            {
                float currSum = avgJerk * jerkQueue.Count;
                float newSum = currSum + jerk;
                jerkQueue.Enqueue(jerk);
                avgJerk = newSum / jerkQueue.Count;
            }
        }
        else if (jerkQueue.Count == windowSize)
        {
            avgJerk += 1f / windowSize * (jerk - jerkQueue.Dequeue());
            jerkQueue.Enqueue(jerk);
            if (jerk > highestJerk)
            {
                highestJerk = jerk;
                if (updateHighestJerkUi && UIManager.Instance != null && UIManager.Instance.highestJerk != null)
                {
                    UIManager.Instance.highestJerk.text = $"{(int)highestJerk}";
                }
            }
        }

        return avgJerk;
    }

    public void Reset()
    {
        jerkQueue.Clear();
        highestJerk = 0f;
        avgJerk = 0f;
    }
}

[Serializable]
public class ExponentialMovingAverage {
    public float Value { get; private set; }
    bool initialized = false;
    public ExponentialMovingAverage(float initial = 0f) { Value = initial; initialized = true; }
    public float Update(float sample, float alpha) {
        if (!initialized) { Value = sample; initialized = true; return Value; }
        Value = alpha * sample + (1f - alpha) * Value;
        return Value;
    }
}

[Serializable]
public class MovingAverage {
    int windowSize = 20;
    float[] buf;
    int idx = 0;
    int count = 0;
    float sum = 0f;
    public MovingAverage() {  }
    public float Update(float sample) {
        if (buf == null) buf = new float[Math.Max(1, windowSize)];
        sum -= buf[idx];
        buf[idx] = sample;
        sum += sample;
        idx = (idx + 1) % buf.Length;
        count = Math.Min(count + 1, buf.Length);
        return sum / count;
    }
}
