using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quản lý state và input buffers để phục vụ cho Client-Side Prediction
/// </summary>
public class StateBuffer
{
    private const int BUFFER_SIZE = 8192;
    
    private StatePayload[] stateBuffer;
    private InputPayload[] inputBuffer;
    
    /// <summary>
    /// Khởi tạo buffers
    /// </summary>
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
    
    /// <summary>
    /// Tính toán buffer index từ tick (circular buffer)
    /// </summary>
    public int GetBufferIndex(int tick)
    {
        return ((tick % BUFFER_SIZE) + BUFFER_SIZE) % BUFFER_SIZE;
    }
    
    /// <summary>
    /// Lưu state vào buffer tại tick cụ thể
    /// </summary>
    public void SetState(int tick, StatePayload state)
    {
        int index = GetBufferIndex(tick);
        state.tick = tick;
        stateBuffer[index] = state;
    }
    
    /// <summary>
    /// Lấy state từ buffer tại tick cụ thể
    /// </summary>
    public StatePayload GetState(int tick)
    {
        int index = GetBufferIndex(tick);
        return stateBuffer[index];
    }
    
    /// <summary>
    /// Lưu input vào buffer
    /// </summary>
    public void SetInput(int tick, InputPayload input)
    {
        int index = GetBufferIndex(tick);
        input.tick = tick;
        inputBuffer[index] = input;
    }
    
    /// <summary>
    /// Lấy input từ buffer
    /// </summary>
    public InputPayload GetInput(int tick)
    {
        int index = GetBufferIndex(tick);
        return inputBuffer[index];
    }
    
    /// <summary>
    /// Tìm state trong buffer dựa trên tick (có kiểm tra chính xác)
    /// </summary>
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
    
    /// <summary>
    /// Reset tất cả buffers (cho lúc start/respawn)
    /// </summary>
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
    
    /// <summary>
    /// Lấy danh sách tất cả ticks hiện có trong state buffer (dùng cho debug)
    /// </summary>
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
    
    /// <summary>
    /// Kiểm tra xem input ở tick cụ thể có hợp lệ không
    /// </summary>
    public bool HasValidInputAt(int tick)
    {
        return GetInput(tick).tick == tick;
    }
}
