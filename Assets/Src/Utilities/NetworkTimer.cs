using System;
using UnityEngine;

namespace CustomTypes
{
    [Serializable]
    public class NetworkTimer
    {
        private float timer;
        private float minTimeBetweenTicks;
        private int currentTick;

        public int CurrentTick => currentTick;
        public float MinTimeBetweenTicks => minTimeBetweenTicks;
        public float ClientTime => timer;

        public NetworkTimer(float serverTickRate)
        {
            minTimeBetweenTicks = 1f / serverTickRate;
            timer = 0f;
            currentTick = 0;
        }

        public void Update(float deltaTime)
        {
            timer += deltaTime;
        }

        public bool ShouldTick()
        {
            if (timer >= minTimeBetweenTicks)
            {
                timer -= minTimeBetweenTicks;
                currentTick++;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            timer = 0f;
            currentTick = 0;
        }

        public void SetCurrentTick(int tick)
        {
            currentTick = tick;
            timer = 0f;
        }

        public float GetTickDelta()
        {
            return minTimeBetweenTicks;
        }
    }
}
