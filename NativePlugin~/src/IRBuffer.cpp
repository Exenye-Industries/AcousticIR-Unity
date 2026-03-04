#include "IRBuffer.h"
#include <cstdlib>

IRBuffer::IRBuffer()
    : activeIndex(0)
{
}

IRBuffer::~IRBuffer()
{
    for (auto& buf : buffers)
    {
        free(buf.data);
        buf.data = nullptr;
    }
}

void IRBuffer::ensureCapacity(Buffer& buf, int requiredLength)
{
    if (buf.capacity < requiredLength)
    {
        free(buf.data);
        buf.capacity = requiredLength;
        buf.data = (float*)malloc(buf.capacity * sizeof(float));
    }
}

void IRBuffer::loadIR(const float* data, int length, int sampleRate)
{
    // Write to the INACTIVE buffer
    int inactiveIdx = 1 - activeIndex.load(std::memory_order_acquire);
    Buffer& inactive = buffers[inactiveIdx];

    ensureCapacity(inactive, length);
    memcpy(inactive.data, data, length * sizeof(float));
    inactive.length = length;
    inactive.sampleRate = sampleRate;

    // Atomically swap to make this the active buffer
    activeIndex.store(inactiveIdx, std::memory_order_release);
}

const float* IRBuffer::getActiveIR() const
{
    int idx = activeIndex.load(std::memory_order_acquire);
    return buffers[idx].data;
}

int IRBuffer::getActiveLength() const
{
    int idx = activeIndex.load(std::memory_order_acquire);
    return buffers[idx].length;
}

int IRBuffer::getActiveSampleRate() const
{
    int idx = activeIndex.load(std::memory_order_acquire);
    return buffers[idx].sampleRate;
}

bool IRBuffer::hasIR() const
{
    return getActiveLength() > 0 && getActiveIR() != nullptr;
}

void IRBuffer::clear()
{
    for (auto& buf : buffers)
    {
        buf.length = 0;
        buf.sampleRate = 0;
    }
}
