using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Quản lý input: thu thập, buffer, gửi đến server, handle tick
/// </summary>
public class InputManager
{
    private int currentTick = 0;
    private InputPayload lastKnownInput;
    private SortedDictionary<int, InputPayload> pendingInputs;
    private const int MAX_PENDING_INPUTS = 5000;
    private const int BUFFER_SIZE = 8192;
    
    public int CurrentTick => currentTick;
    public InputPayload LastKnownInput => lastKnownInput;
    public int PendingInputCount => pendingInputs.Count;
    
    public InputManager()
    {
        pendingInputs = new SortedDictionary<int, InputPayload>();
        lastKnownInput = new InputPayload 
        { 
            tick = 1, 
            inputAcceleration = 0f, 
            inputSteering = 0f, 
            inputBrake = 0f,
            isCollide = false 
        };
    }
    
    /// <summary>
    /// Cập nhật tick client dựa trên server time (50Hz)
    /// </summary>
    public void UpdateClientTick()
    {
        if (currentTick == 0 && NetworkManager.Singleton != null && NetworkManager.Singleton.ServerTime.Tick > 0)
        {
            currentTick = NetworkManager.Singleton.ServerTime.Tick;
        }
        else if (currentTick > 0)
        {
            currentTick++;
        }
    }
    
    /// <summary>
    /// Kiểm tra lệch tick client/server
    /// </summary>
    public (bool HasWarning, int TickDiff) CheckTickSync()
    {
        if (NetworkManager.Singleton == null || currentTick <= 1)
            return (false, 0);
        
        int serverTick = NetworkManager.Singleton.ServerTime.Tick;
        if (serverTick <= 1)
            return (false, 0);
        
        int tickDiff = Mathf.Abs(currentTick - serverTick);
        return (tickDiff >= 10, tickDiff);
    }
    
    /// <summary>
    /// Đồng bộ lại tick nếu lệch quá lớn
    /// </summary>
    public void SyncTickIfNeeded(int serverTick)
    {
        int tickDiff = Mathf.Abs(currentTick - serverTick);
        if (tickDiff >= 100)
        {
            Debug.LogWarning($"[InputManager] Tick lệch quá lớn ({tickDiff}), đồng bộ: {currentTick} -> {serverTick}");
            currentTick = serverTick;
        }
    }
    
    /// <summary>
    /// Tạo input payload từ input hiện tại
    /// </summary>
    public InputPayload CreateInputPayload(float inputAcceleration, float inputBrake, float inputSteering, bool isCollide)
    {
        return new InputPayload
        {
            tick = currentTick,
            inputAcceleration = Mathf.Abs(inputAcceleration) > 0.0001f ? inputAcceleration : 0f,
            inputBrake = Mathf.Abs(inputBrake) > 0.0001f ? inputBrake : 0f,
            inputSteering = Mathf.Abs(inputSteering) > 0.0001f ? inputSteering : 0f,
            isCollide = isCollide
        };
    }
    
    /// <summary>
    /// Lưu input mới nhất được biết (cho jitter buffer)
    /// </summary>
    public void UpdateLastKnownInput(InputPayload input)
    {
        lastKnownInput = input;
    }
    
    /// <summary>
    /// Thêm pending input từ client gửi lên server
    /// </summary>
    public void AddPendingInput(InputPayload input)
    {
        if (!pendingInputs.ContainsKey(input.tick))
        {
            pendingInputs.Add(input.tick, input);
            if (pendingInputs.Count > MAX_PENDING_INPUTS)
                pendingInputs.Remove(pendingInputs.Keys.First());
        }
        else
        {
            pendingInputs[input.tick] = input;
        }
    }
    
    /// <summary>
    /// Lấy input từ pending inputs (server-side)
    /// </summary>
    public bool TryGetPendingInput(int tick, out InputPayload input)
    {
        return pendingInputs.TryGetValue(tick, out input);
    }
    
    /// <summary>
    /// Xóa input cũ khỏi pending inputs
    /// </summary>
    public void RemoveOldPendingInputs(int upToTick)
    {
        var oldTicks = pendingInputs.Keys.Where(k => k <= upToTick).ToList();
        foreach (var t in oldTicks)
            pendingInputs.Remove(t);
    }
    
    /// <summary>
    /// Kiểm tra xem input có hợp lệ không
    /// </summary>
    public bool IsInputValid(InputPayload input, int lastProcessedTick)
    {
        if (input.tick < lastProcessedTick - BUFFER_SIZE || input.tick > lastProcessedTick + 600)
            return false;
        return true;
    }
    
    /// <summary>
    /// Lấy input cần xử lý tiếp theo (server-side)
    /// </summary>
    public InputPayload GetNextInputToProcess(int targetTick)
    {
        if (TryGetPendingInput(targetTick, out InputPayload received))
        {
            return received;
        }
        
        // Nếu không có input mới, dùng last known input (jitter buffer)
        InputPayload fallback = lastKnownInput;
        fallback.tick = targetTick;
        return fallback;
    }
    
    /// <summary>
    /// Reset input manager (cho lúc start/respawn)
    /// </summary>
    public void Reset()
    {
        currentTick = 0;
        pendingInputs.Clear();
        lastKnownInput = new InputPayload 
        { 
            tick = 1, 
            inputAcceleration = 0f, 
            inputSteering = 0f, 
            inputBrake = 0f,
            isCollide = false 
        };
    }
    
    /// <summary>
    /// Kiểm tra nếu pendingInputs có input chờ xử lý
    /// </summary>
    public bool HasPendingInputs => pendingInputs.Count > 0;
    
    /// <summary>
    /// Lấy tick input soonest cần xử lý
    /// </summary>
    public int GetNextPendingInputTick()
    {
        return pendingInputs.Count > 0 ? pendingInputs.Keys.First() : -1;
    }
}
