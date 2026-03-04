#include "FFTProcessor.h"
#include <cstring>

FFTProcessor::FFTProcessor()
    : setup(nullptr)
    , fftSize(0)
    , workBuffer(nullptr)
{
}

FFTProcessor::~FFTProcessor()
{
    destroy();
}

void FFTProcessor::init(int size)
{
    destroy();

    fftSize = size;
    setup = pffft_new_setup(fftSize, PFFFT_REAL);
    workBuffer = (float*)pffft_aligned_malloc(fftSize * sizeof(float));
    memset(workBuffer, 0, fftSize * sizeof(float));
}

void FFTProcessor::destroy()
{
    if (setup)
    {
        pffft_destroy_setup(setup);
        setup = nullptr;
    }
    if (workBuffer)
    {
        pffft_aligned_free(workBuffer);
        workBuffer = nullptr;
    }
    fftSize = 0;
}

void FFTProcessor::forward(const float* input, float* output)
{
    if (!setup) return;
    // Use unordered transform - required for pffft_zconvolve_accumulate
    pffft_transform(setup, input, output, workBuffer, PFFFT_FORWARD);
}

void FFTProcessor::inverse(const float* input, float* output)
{
    if (!setup) return;
    // Use unordered transform (matches forward)
    pffft_transform(setup, input, output, workBuffer, PFFFT_BACKWARD);

    // pffft inverse does not normalize - divide by fftSize
    float scale = 1.0f / (float)fftSize;
    for (int i = 0; i < fftSize; i++)
        output[i] *= scale;
}

void FFTProcessor::complexMultiply(const float* a, const float* b, float* result)
{
    if (!setup) return;
    // Clear result first, then accumulate = multiply without prior accumulation
    memset(result, 0, fftSize * sizeof(float));
    pffft_zconvolve_accumulate(setup, a, b, result, 1.0f);
}

void FFTProcessor::complexMultiplyAccumulate(const float* a, const float* b, float* result)
{
    if (!setup) return;
    // result += a * b
    pffft_zconvolve_accumulate(setup, a, b, result, 1.0f);
}

float* FFTProcessor::allocateAligned()
{
    float* buf = (float*)pffft_aligned_malloc(fftSize * sizeof(float));
    if (buf) memset(buf, 0, fftSize * sizeof(float));
    return buf;
}

void FFTProcessor::freeAligned(float* buffer)
{
    if (buffer)
        pffft_aligned_free(buffer);
}
