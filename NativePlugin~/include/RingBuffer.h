#pragma once

#include <cstring>
#include <cstdlib>

/// Lock-free single-producer single-consumer ring buffer for audio samples.
class RingBuffer
{
public:
    RingBuffer() : buffer(nullptr), size(0), readPos(0), writePos(0) {}

    ~RingBuffer()
    {
        free(buffer);
    }

    void allocate(int numSamples)
    {
        free(buffer);
        size = numSamples;
        buffer = (float*)calloc(size, sizeof(float));
        readPos = 0;
        writePos = 0;
    }

    void clear()
    {
        if (buffer)
            memset(buffer, 0, size * sizeof(float));
        readPos = 0;
        writePos = 0;
    }

    void write(float sample)
    {
        if (buffer)
        {
            buffer[writePos] = sample;
            writePos = (writePos + 1) % size;
        }
    }

    /// Read sample at (writePos - offset), wrapping around.
    float readBack(int offset) const
    {
        if (!buffer || size == 0) return 0.0f;
        int idx = writePos - 1 - offset;
        while (idx < 0) idx += size;
        return buffer[idx % size];
    }

    int getSize() const { return size; }

private:
    float* buffer;
    int size;
    int readPos;
    int writePos;
};
