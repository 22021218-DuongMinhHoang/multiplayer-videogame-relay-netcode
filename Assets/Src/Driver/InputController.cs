using System;
using CustomTypes;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[Serializable]
public class InputController : NetworkBehaviour
{
    [SerializeField] [HideInInspector] private CarController _carController;
    
    private NetworkPlayer _networkPlayer;

    public override void OnNetworkSpawn()
    {
        _networkPlayer = GetComponent<NetworkPlayer>();
        EventManager.Instance.CarSpawn.AddListener(OnCarSpawn);
    }

    private void OnCarSpawn(int id)
    {
        if (_networkPlayer != null && id == _networkPlayer.ID)
        {
            if (_networkPlayer.car != null)
            {
                _carController = _networkPlayer.car.GetComponent<CarController>();
            }
            EventManager.Instance.CarSpawn.RemoveListener(OnCarSpawn);
        }
    }
    public void OnMove(InputAction.CallbackContext context)
    {
        if (!IsOwner || _carController == null) return;

        Vector2 input = context.ReadValue<Vector2>();

        _carController.inputAcceleration = input.y;
        _carController.inputSteering = input.x;

        OnMoveRpc(input);
    }

    [Rpc(SendTo.Server)]
    private void OnMoveRpc(Vector2 input)
    {
        if (_carController != null)
        {
            _carController.inputAcceleration = input.y;
            _carController.inputSteering = input.x;
        }
    }

    public void OnBrake(InputAction.CallbackContext context)
    {
        if (!IsOwner || _carController == null) return;

        float input = context.ReadValue<float>();

        _carController.inputBrake = input;

        OnBrakeRpc(input);
    }

    [Rpc(SendTo.Server)]
    public void OnBrakeRpc(float input)
    {
        if (_carController != null)
        {
            _carController.inputBrake = input;
        }
    }

    public void OnAttack(InputAction.CallbackContext context)
    {
        if (!IsOwner || _carController == null) return;

        if (context.performed && _networkPlayer.Rockets > 0 && 
            _carController.State != CarState.Idle && 
            _carController.State != CarState.Dead)
        {
             _networkPlayer.Rockets--; 

            OnAttackRpc();
        }
    }

    [Rpc(SendTo.Server)]
    private void OnAttackRpc()
    {
        var spawnPos = _carController.transform.position + new Vector3(0, 2);
        var spawnRot = _carController.transform.rotation;

        GameManager.Instance.SpawnRocket(spawnPos, spawnRot, _networkPlayer.Name);
    }

    public void OnSummary(InputAction.CallbackContext context)
    {
        if (context.performed && GameManager.Instance.State == GameState.Started)
            EventManager.Instance.RaiseSummaryDisplay();
        else if (context.canceled && GameManager.Instance.State == GameState.Started)
            EventManager.Instance.RaiseSummaryHid();
    }

    public void OnWrite(InputAction.CallbackContext context)
    {
        if (context.performed) UIManager.Instance.chatController.WriteChatMessage();
    }
}