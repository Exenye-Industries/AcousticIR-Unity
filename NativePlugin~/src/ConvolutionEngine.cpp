#include "ConvolutionEngine.h"
#include <cstring>
#include <cmath>
#include <algorithm>

ConvolutionEngine::ConvolutionEngine()
    : directLength(0)
    , fftSize(0)
    , partitionSize(0)
    , numPartitions(0)
    , fftInput(nullptr)
    , fftOutput(nullptr)
    , accumFreq(nullptr)
    , overlapBuffer(nullptr)
    , fdlWritePos(0)
    , currentBlockSize(0)
    , currentSampleRate(0)
    , prepared(false)
    , irLoaded(false)
{
    memset(directIR, 0, sizeof(directIR));
}

ConvolutionEngine::~ConvolutionEngine()
{
    freePartitions();
}

static int nextPowerOfTwo(int n)
{
    int v = 1;
    while (v < n) v <<= 1;
    return v;
}

void ConvolutionEngine::prepare(int blockSize, int sampleRate)
{
    freePartitions();

    currentBlockSize = blockSize;
    currentSampleRate = sampleRate;

    // Partition size = blockSize, FFT size = 2 * blockSize (for overlap-add)
    partitionSize = blockSize;
    fftSize = std::max(32, nextPowerOfTwo(2 * partitionSize));

    fft.init(fftSize);

    // Allocate processing buffers
    fftInput = fft.allocateAligned();
    fftOutput = fft.allocateAligned();
    accumFreq = fft.allocateAligned();
    overlapBuffer = (float*)calloc(fftSize, sizeof(float));

    // Input history for direct convolution
    inputHistory.allocate(MAX_DIRECT_LENGTH + blockSize);

    prepared = true;

    // If IR was already loaded, re-prepare partitions
    if (irBuffer.hasIR())
        preparePartitions();
}

void ConvolutionEngine::setIR(const float* irData, int irLength, int irSampleRate)
{
    irBuffer.loadIR(irData, irLength, irSampleRate);

    if (prepared)
        preparePartitions();
}

void ConvolutionEngine::preparePartitions()
{
    // Free old partitions
    for (auto& p : partitions)
        fft.freeAligned(p.irFreq);
    partitions.clear();

    for (auto& seg : inputSegmentsFreq)
        fft.freeAligned(seg);
    inputSegmentsFreq.clear();

    const float* ir = irBuffer.getActiveIR();
    int irLength = irBuffer.getActiveLength();

    if (!ir || irLength == 0 || !prepared)
    {
        irLoaded = false;
        return;
    }

    // Direct convolution portion (first MAX_DIRECT_LENGTH samples)
    directLength = std::min(irLength, MAX_DIRECT_LENGTH);
    memcpy(directIR, ir, directLength * sizeof(float));

    // FFT partitioned portion (remaining IR after directLength)
    int tailOffset = directLength;
    int tailLength = irLength - tailOffset;

    if (tailLength <= 0)
    {
        numPartitions = 0;
        irLoaded = true;
        return;
    }

    // Split tail into partitions of partitionSize samples
    numPartitions = (tailLength + partitionSize - 1) / partitionSize;

    // Pre-compute FFT of each IR partition
    float* tempBuffer = fft.allocateAligned();

    for (int i = 0; i < numPartitions; i++)
    {
        int offset = tailOffset + i * partitionSize;
        int remaining = std::min(partitionSize, irLength - offset);

        // Zero-pad partition to fftSize
        memset(tempBuffer, 0, fftSize * sizeof(float));
        memcpy(tempBuffer, ir + offset, remaining * sizeof(float));

        // FFT the IR partition
        Partition p;
        p.irFreq = fft.allocateAligned();
        fft.forward(tempBuffer, p.irFreq);
        partitions.push_back(p);
    }

    // Allocate input segment history (Frequency Delay Line)
    inputSegmentsFreq.resize(numPartitions);
    for (int i = 0; i < numPartitions; i++)
        inputSegmentsFreq[i] = fft.allocateAligned();

    fdlWritePos = 0;

    fft.freeAligned(tempBuffer);
    irLoaded = true;
}

void ConvolutionEngine::process(const float* inBuffer, float* outBuffer, int numSamples, float dryWet)
{
    if (!prepared)
    {
        memcpy(outBuffer, inBuffer, numSamples * sizeof(float));
        return;
    }

    if (!irLoaded || !irBuffer.hasIR())
    {
        memcpy(outBuffer, inBuffer, numSamples * sizeof(float));
        return;
    }

    float wetGain = dryWet;
    float dryGain = 1.0f - dryWet;

    // ---- Direct convolution (low latency, first directLength taps) ----
    for (int i = 0; i < numSamples; i++)
    {
        inputHistory.write(inBuffer[i]);

        float directOut = 0.0f;
        for (int tap = 0; tap < directLength; tap++)
        {
            directOut += inputHistory.readBack(tap) * directIR[tap];
        }

        outBuffer[i] = directOut;
    }

    // ---- FFT partitioned convolution (tail) ----
    if (numPartitions > 0 && numSamples == partitionSize)
    {
        // Prepare input: zero-pad current block to fftSize
        memset(fftInput, 0, fftSize * sizeof(float));
        memcpy(fftInput, inBuffer, numSamples * sizeof(float));

        // FFT the input block
        fft.forward(fftInput, inputSegmentsFreq[fdlWritePos]);

        // Multiply-accumulate across all partitions
        memset(accumFreq, 0, fftSize * sizeof(float));

        for (int p = 0; p < numPartitions; p++)
        {
            // FDL: read input segment from (fdlWritePos - p) wrapped
            int readPos = fdlWritePos - p;
            if (readPos < 0) readPos += numPartitions;

            if (p == 0)
                fft.complexMultiply(inputSegmentsFreq[readPos], partitions[p].irFreq, accumFreq);
            else
                fft.complexMultiplyAccumulate(inputSegmentsFreq[readPos], partitions[p].irFreq, accumFreq);
        }

        // Inverse FFT
        fft.inverse(accumFreq, fftOutput);

        // Overlap-add: add the overlap from the previous block
        for (int i = 0; i < numSamples; i++)
        {
            outBuffer[i] += fftOutput[i] + overlapBuffer[i];
        }

        // Save the second half as overlap for next block
        memcpy(overlapBuffer, fftOutput + partitionSize, partitionSize * sizeof(float));
        // Zero the rest
        if (fftSize > 2 * partitionSize)
            memset(overlapBuffer + partitionSize, 0, (fftSize - 2 * partitionSize) * sizeof(float));

        // Advance FDL write position
        fdlWritePos = (fdlWritePos + 1) % numPartitions;
    }

    // ---- Mix dry/wet ----
    for (int i = 0; i < numSamples; i++)
    {
        outBuffer[i] = dryGain * inBuffer[i] + wetGain * outBuffer[i];
    }
}

void ConvolutionEngine::reset()
{
    inputHistory.clear();

    if (overlapBuffer)
        memset(overlapBuffer, 0, fftSize * sizeof(float));

    for (auto& seg : inputSegmentsFreq)
    {
        if (seg) memset(seg, 0, fftSize * sizeof(float));
    }

    fdlWritePos = 0;
}

void ConvolutionEngine::freePartitions()
{
    for (auto& p : partitions)
        fft.freeAligned(p.irFreq);
    partitions.clear();

    for (auto& seg : inputSegmentsFreq)
        fft.freeAligned(seg);
    inputSegmentsFreq.clear();

    fft.freeAligned(fftInput); fftInput = nullptr;
    fft.freeAligned(fftOutput); fftOutput = nullptr;
    fft.freeAligned(accumFreq); accumFreq = nullptr;
    free(overlapBuffer); overlapBuffer = nullptr;

    fft.destroy();
    numPartitions = 0;
    irLoaded = false;
    prepared = false;
}
