using System;
using System.Collections.Generic;
using UnityEngine;

public class StateBuffer
{
    private const int BUFFER_SIZE = 8192;
    
    private StatePayload[] stateBuffer;
    private InputPayload[] inputBuffer;
    
    public void Initialize()
    {
        stateBuffer = new StatePayload[BUFFER_SIZE];
        inputBuffer = new InputPayload[BUFFER_SIZE];
        
        for (int i = 0; i < BUFFER_SIZE; i++)
        {
            stateBuffer[i] = new StatePayload();
            inputBuffer[i] = new InputPayload();
        }
    }
    
    public int GetBufferIndex(int tick)
    {
        return ((tick % BUFFER_SIZE) + BUFFER_SIZE) % BUFFER_SIZE;
    }
    
    public void SetState(int tick, StatePayload state)
    {
        int index = GetBufferIndex(tick);
        state.tick = tick;
        stateBuffer[index] = state;
    }
    
    public StatePayload GetState(int tick)
    {
        int index = GetBufferIndex(tick);
        return stateBuffer[index];
    }
    
    public void SetInput(int tick, InputPayload input)
    {
        int index = GetBufferIndex(tick);
        input.tick = tick;
        inputBuffer[index] = input;
    }
    
    public InputPayload GetInput(int tick)
    {
        int index = GetBufferIndex(tick);
        return inputBuffer[index];
    }
    
    public bool TryGetStateByTick(int searchTick, out StatePayload foundState)
    {
        int startIdx = GetBufferIndex(searchTick);
        foundState = stateBuffer[startIdx];
        
        if (foundState.tick == searchTick)
            return true;
        
        // Tìm kiếm xung quanh nếu index không khớp
        for (int i = 0; i < BUFFER_SIZE; i++)
        {
            if (stateBuffer[i].tick == searchTick)
            {
                foundState = stateBuffer[i];
                return true;
            }
        }
        
        return false;
    }
    
    public void Reset(Vector3 initialPos, Quaternion initialRot, float initialSpeed)
    {
        for (int i = 0; i < BUFFER_SIZE; i++)
        {
            stateBuffer[i] = new StatePayload 
            { 
                tick = 0, 
                position = initialPos, 
                rotation = initialRot,
                speed = initialSpeed
            };
            inputBuffer[i] = new InputPayload 
            { 
                tick = 0, 
                inputAcceleration = 0f, 
                inputSteering = 0f, 
                inputBrake = 0f,
                isCollide = false
            };
        }
    }

    public List<int> GetActiveTicks()
    {
        List<int> activeTicks = new List<int>();
        for (int i = 0; i < BUFFER_SIZE; i++)
        {
            if (stateBuffer[i].tick != 0)
                activeTicks.Add(stateBuffer[i].tick);
        }
        return activeTicks;
    }
    
    public bool HasValidInputAt(int tick)
    {
        return GetInput(tick).tick == tick;
    }
}
