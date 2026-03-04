#pragma once

#include "FFTProcessor.h"
#include "IRBuffer.h"
#include "RingBuffer.h"
#include <vector>

/// Real-time partitioned convolution engine.
/// Uses direct convolution for the first segment (low latency)
/// and uniform partitioned FFT overlap-add for the tail.
class ConvolutionEngine
{
public:
    ConvolutionEngine();
    ~ConvolutionEngine();

    /// Prepare the engine for processing.
    /// blockSize: number of samples per process callback (per channel).
    /// sampleRate: audio sample rate.
    void prepare(int blockSize, int sampleRate);

    /// Set the impulse response. Thread-safe (uses IRBuffer double-buffering).
    /// Can be called from any thread.
    void setIR(const float* irData, int irLength, int irSampleRate);

    /// Process a block of audio (mono).
    /// inBuffer/outBuffer: blockSize samples each.
    /// dryWet: 0.0 = fully dry, 1.0 = fully wet.
    void process(const float* inBuffer, float* outBuffer, int numSamples, float dryWet);

    /// Reset all internal state (clear buffers).
    void reset();

private:
    // Direct convolution for the first directLength samples of the IR
    static constexpr int MAX_DIRECT_LENGTH = 128;
    float directIR[MAX_DIRECT_LENGTH];
    int directLength;
    RingBuffer inputHistory; // Ring buffer of past input samples

    // FFT-based partitioned convolution for the tail
    struct Partition
    {
        float* irFreq;     // FFT of this IR segment (pffft-aligned)
    };

    FFTProcessor fft;
    int fftSize;           // 2 * blockSize for overlap-add
    int partitionSize;     // Same as blockSize
    int numPartitions;     // Number of FFT partitions in the IR tail

    std::vector<Partition> partitions;

    // Processing buffers (pffft-aligned)
    float* fftInput;       // Time-domain input padded for FFT
    float* fftOutput;      // Frequency-domain result
    float* accumFreq;      // Accumulated frequency-domain result
    float* overlapBuffer;  // Overlap from previous block

    // Input segment history for FDL (Frequency Delay Line)
    std::vector<float*> inputSegmentsFreq; // Ring buffer of FFT'd input segments
    int fdlWritePos;

    IRBuffer irBuffer;
    int currentBlockSize;
    int currentSampleRate;
    bool prepared;
    bool irLoaded;

    void preparePartitions();
    void freePartitions();
};
