using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class InputManager
{
    private int currentTick = 0;
    private InputPayload lastKnownInput;
    private SortedDictionary<int, InputPayload> pendingInputs;
    //private const int MAX_PENDING_INPUTS = 5000;
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
    
    public void SyncTickIfNeeded(int serverTick)
    {
        int tickDiff = Mathf.Abs(currentTick - serverTick);
        if (tickDiff >= 100)
        {
            Debug.LogWarning($"[InputManager] Tick lệch quá lớn ({tickDiff}), đồng bộ: {currentTick} -> {serverTick}");
            currentTick = serverTick;
        }
    }
    
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
    
    public void UpdateLastKnownInput(InputPayload input)
    {
        lastKnownInput = input;
    }
    
    public void AddPendingInput(InputPayload input)
    {
        if (!pendingInputs.ContainsKey(input.tick))
        {
            pendingInputs.Add(input.tick, input);
            if (pendingInputs.Count > BUFFER_SIZE)
                pendingInputs.Remove(pendingInputs.Keys.First());
        }
        else
        {
            pendingInputs[input.tick] = input;
        }
    }
    
    public bool TryGetPendingInput(int tick, out InputPayload input)
    {
        return pendingInputs.TryGetValue(tick, out input);
    }
    public void RemoveOldPendingInputs(int upToTick)
    {
        var oldTicks = pendingInputs.Keys.Where(k => k <= upToTick).ToList();
        foreach (var t in oldTicks)
            pendingInputs.Remove(t);
    }
    
    public bool IsInputValid(InputPayload input, int lastProcessedTick)
    {
        if (input.tick < lastProcessedTick - BUFFER_SIZE || input.tick > lastProcessedTick + 600)
            return false;
        return true;
    }
    
    public InputPayload GetNextInputToProcess(int targetTick)
    {
        if (TryGetPendingInput(targetTick, out InputPayload received))
        {
            return received;
        }
        
        InputPayload fallback = lastKnownInput;
        fallback.tick = targetTick;
        return fallback;
    }
    
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
    
    public bool HasPendingInputs => pendingInputs.Count > 0;
    
    public int GetNextPendingInputTick()
    {
        return pendingInputs.Count > 0 ? pendingInputs.Keys.First() : -1;
    }
}
