using UnityEngine;

namespace CustomTypes
{
    /// <summary>
    /// Timer độc lập để quản lý tick thay vì đếm thủ công
    /// Giúp tránh lỗi lệch pha giữa client và server
    /// </summary>
    public class NetworkTimer
    {
        private float timer;
        private float minTimeBetweenTicks;
        private int currentTick;

        public int CurrentTick => currentTick;
        public float MinTimeBetweenTicks => minTimeBetweenTicks;
        public float ClientTime => timer;

        /// <summary>
        /// Khởi tạo NetworkTimer với tốc độ tick specified (Hz)
        /// </summary>
        public NetworkTimer(float serverTickRate)
        {
            minTimeBetweenTicks = 1f / serverTickRate;
            timer = 0f;
            currentTick = 0;
        }

        /// <summary>
        /// Cập nhật timer mỗi frame
        /// </summary>
        public void Update(float deltaTime)
        {
            timer += deltaTime;
        }

        /// <summary>
        /// Kiểm tra xem có nên thực hiện tick không
        /// Trả về true nếu đã đủ thời gian cho tick tiếp theo
        /// </summary>
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

        /// <summary>
        /// Reset timer về trạng thái ban đầu
        /// </summary>
        public void Reset()
        {
            timer = 0f;
            currentTick = 0;
        }

        /// <summary>
        /// Đặt tick hiện tại (dùng khi đồng bộ với server)
        /// </summary>
        public void SetCurrentTick(int tick)
        {
            currentTick = tick;
            timer = 0f;
        }

        /// <summary>
        /// Lấy thời gian giữa hai tick
        /// </summary>
        public float GetTickDelta()
        {
            return minTimeBetweenTicks;
        }
    }
}
