using System;

namespace CustomTypes
{
    /// <summary>
    /// Bộ đệm vòng - Khởi tạo một lần duy nhất, ghi đè dữ liệu cũ khi đầy
    /// Giúp giảm áp lực lên Garbage Collector
    /// </summary>
    public class CircularBuffer<T>
    {
        private T[] buffer;
        private int bufferSize;

        public CircularBuffer(int size)
        {
            bufferSize = size;
            buffer = new T[bufferSize];
        }

        /// <summary>
        /// Thêm phần tử vào bộ đệm dựa trên số tick
        /// </summary>
        public void Add(T item, int tick)
        {
            int index = GetIndex(tick);
            buffer[index] = item;
        }

        /// <summary>
        /// Lấy phần tử ra khỏi bộ đệm
        /// </summary>
        public T Get(int tick)
        {
            int index = GetIndex(tick);
            return buffer[index];
        }

        /// <summary>
        /// Tính chỉ số trong bộ đệm từ tick
        /// </summary>
        private int GetIndex(int tick)
        {
            return ((tick % bufferSize) + bufferSize) % bufferSize;
        }

        /// <summary>
        /// Dọn sạch bộ đệm
        /// </summary>
        public void Clear()
        {
            buffer = new T[bufferSize];
        }

        /// <summary>
        /// Lấy kích thước bộ đệm
        /// </summary>
        public int Size => bufferSize;
    }
}
