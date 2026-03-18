using System;

namespace CustomTypes
{
    public class CircularBuffer<T>
    {
        private T[] buffer;
        private int bufferSize;

        public CircularBuffer(int size)
        {
            bufferSize = size;
            buffer = new T[bufferSize];
        }

        public void Add(T item, int tick)
        {
            int index = GetIndex(tick);
            buffer[index] = item;
        }

        public T Get(int tick)
        {
            int index = GetIndex(tick);
            return buffer[index];
        }

        private int GetIndex(int tick)
        {
            return ((tick % bufferSize) + bufferSize) % bufferSize;
        }

        public void Clear()
        {
            buffer = new T[bufferSize];
        }

        public int Size => bufferSize;
    }
}
