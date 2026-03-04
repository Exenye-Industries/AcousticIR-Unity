#pragma once

#include <atomic>
#include <cstring>

/// Thread-safe double-buffered IR storage.
/// C# writes to the inactive buffer, then swaps atomically.
/// The audio thread only reads from the active buffer.
class IRBuffer
{
public:
    IRBuffer();
    ~IRBuffer();

    /// Load new IR data (called from main thread / C# via P/Invoke).
    /// Copies data into the inactive buffer, then swaps.
    void loadIR(const float* data, int length, int sampleRate);

    /// Get pointer to the active IR data (called from audio thread).
    /// Returns nullptr if no IR is loaded.
    const float* getActiveIR() const;

    /// Get the length of the active IR in samples.
    int getActiveLength() const;

    /// Get the sample rate of the active IR.
    int getActiveSampleRate() const;

    /// Check if an IR is loaded.
    bool hasIR() const;

    /// Clear all IR data.
    void clear();

private:
    struct Buffer
    {
        float* data;
        int length;
        int sampleRate;
        int capacity;

        Buffer() : data(nullptr), length(0), sampleRate(0), capacity(0) {}
    };

    Buffer buffers[2];
    std::atomic<int> activeIndex;

    void ensureCapacity(Buffer& buf, int requiredLength);
};
