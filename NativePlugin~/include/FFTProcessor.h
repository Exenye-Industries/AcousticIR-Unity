#pragma once

#include "pffft.h"

/// Wrapper around pffft for real-valued FFT operations.
/// Handles setup, forward/inverse transforms, and complex multiplication.
class FFTProcessor
{
public:
    FFTProcessor();
    ~FFTProcessor();

    /// Initialize for a given FFT size (must be power of 2, >= 32).
    void init(int fftSize);

    /// Release all resources.
    void destroy();

    /// Forward FFT: time domain -> frequency domain.
    /// Input and output must be fftSize floats.
    /// Output is in pffft's internal ordering (suitable for zconvolve).
    void forward(const float* input, float* output);

    /// Inverse FFT: frequency domain -> time domain.
    /// Input and output must be fftSize floats.
    void inverse(const float* input, float* output);

    /// Complex multiplication in frequency domain (convolution).
    /// result = a * b (element-wise complex multiply in pffft ordering).
    /// All buffers must be fftSize floats, pffft-aligned.
    void complexMultiply(const float* a, const float* b, float* result);

    /// Accumulate complex multiplication: result += a * b.
    void complexMultiplyAccumulate(const float* a, const float* b, float* result);

    /// Allocate pffft-aligned buffer of fftSize floats.
    float* allocateAligned();

    /// Free pffft-aligned buffer.
    void freeAligned(float* buffer);

    int getFFTSize() const { return fftSize; }

private:
    PFFFT_Setup* setup;
    int fftSize;
    float* workBuffer; // pffft work buffer
};
