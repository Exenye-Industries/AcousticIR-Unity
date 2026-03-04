#include "AudioPluginInterface.h"
#include "ConvolutionEngine.h"
#include <cstring>
#include <cmath>
#include <mutex>
#include <unordered_map>

// ============================================================================
// Plugin parameters
// ============================================================================

enum Param
{
    P_DryWet = 0,
    P_Gain,
    P_InstanceID,
    P_NUM
};

// ============================================================================
// Plugin instance data
// ============================================================================

struct PluginData
{
    float params[P_NUM];
    ConvolutionEngine engine;
    bool initialized;
    int instanceId;
};

// ============================================================================
// Global instance registry (for IR loading from C#)
// ============================================================================

static std::mutex g_registryMutex;
static std::unordered_map<int, PluginData*> g_instances;
static int g_nextInstanceId = 1;

static void RegisterInstance(PluginData* data)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    data->instanceId = g_nextInstanceId++;
    data->params[P_InstanceID] = (float)data->instanceId;
    g_instances[data->instanceId] = data;
}

static void UnregisterInstance(PluginData* data)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    g_instances.erase(data->instanceId);
}

static PluginData* FindInstance(int id)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    auto it = g_instances.find(id);
    return (it != g_instances.end()) ? it->second : nullptr;
}

// ============================================================================
// Unity callbacks
// ============================================================================

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
CreateCallback(UnityAudioEffectState* state)
{
    auto* data = new PluginData();
    memset(data->params, 0, sizeof(data->params));
    data->params[P_DryWet] = 0.5f;    // 50% wet by default
    data->params[P_Gain] = 1.0f;       // Unity gain
    data->initialized = false;

    state->effectdata = data;
    RegisterInstance(data);

    return UNITY_AUDIODSP_OK;
}

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
ReleaseCallback(UnityAudioEffectState* state)
{
    auto* data = state->GetEffectData<PluginData>();
    UnregisterInstance(data);
    delete data;
    return UNITY_AUDIODSP_OK;
}

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
ResetCallback(UnityAudioEffectState* state)
{
    auto* data = state->GetEffectData<PluginData>();
    data->engine.reset();
    return UNITY_AUDIODSP_OK;
}

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
ProcessCallback(UnityAudioEffectState* state, float* inbuffer, float* outbuffer,
    unsigned int length, int inchannels, int outchannels)
{
    auto* data = state->GetEffectData<PluginData>();

    // Lazy initialization on first process call (now we know the block size)
    if (!data->initialized)
    {
        data->engine.prepare((int)length, (int)state->samplerate);
        data->initialized = true;
    }

    float dryWet = data->params[P_DryWet];
    float gain = data->params[P_Gain];

    // Process each channel independently
    // Buffer layout: interleaved [L0,R0,L1,R1,...] -> we need to de-interleave
    if (inchannels == outchannels && inchannels > 0)
    {
        // Temporary mono buffers (stack-allocated for small blocks)
        const int maxStack = 4096;
        float monoInStack[maxStack];
        float monoOutStack[maxStack];

        float* monoIn = (length <= maxStack) ? monoInStack : new float[length];
        float* monoOut = (length <= maxStack) ? monoOutStack : new float[length];

        // Process first channel with convolution, copy to all output channels
        // De-interleave first channel
        for (unsigned int i = 0; i < length; i++)
            monoIn[i] = inbuffer[i * inchannels];

        data->engine.process(monoIn, monoOut, (int)length, dryWet);

        // Apply gain and write to all output channels (interleaved)
        for (unsigned int i = 0; i < length; i++)
        {
            float sample = monoOut[i] * gain;
            for (int ch = 0; ch < outchannels; ch++)
                outbuffer[i * outchannels + ch] = sample;
        }

        if (length > maxStack)
        {
            delete[] monoIn;
            delete[] monoOut;
        }
    }
    else
    {
        // Fallback: pass through
        if (inbuffer != outbuffer)
            memcpy(outbuffer, inbuffer, length * outchannels * sizeof(float));
    }

    return UNITY_AUDIODSP_OK;
}

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
SetFloatParameterCallback(UnityAudioEffectState* state, int index, float value)
{
    auto* data = state->GetEffectData<PluginData>();
    if (index < 0 || index >= P_NUM)
        return UNITY_AUDIODSP_ERR_UNSUPPORTED;

    data->params[index] = value;
    return UNITY_AUDIODSP_OK;
}

static UNITY_AUDIODSP_RESULT UNITY_AUDIODSP_CALLBACK
GetFloatParameterCallback(UnityAudioEffectState* state, int index, float* value, char* valuestr)
{
    auto* data = state->GetEffectData<PluginData>();
    if (index < 0 || index >= P_NUM)
        return UNITY_AUDIODSP_ERR_UNSUPPORTED;

    if (value)
        *value = data->params[index];

    if (valuestr)
        valuestr[0] = '\0';

    return UNITY_AUDIODSP_OK;
}

// ============================================================================
// Parameter definitions (static, must live for the lifetime of the DLL)
// ============================================================================

static UnityAudioParameterDefinition g_paramDefs[P_NUM] = {
    // P_DryWet
    { "DryWet", "%", "Dry/Wet mix (0=dry, 100=wet)", 0.0f, 1.0f, 0.5f, 100.0f, 1.0f },
    // P_Gain
    { "Gain", "dB", "Output gain", 0.0f, 2.0f, 1.0f, 1.0f, 1.0f },
    // P_InstanceID (read-only, used by C# to identify this instance)
    { "InstanceID", "", "Internal instance ID (read-only)", 0.0f, 100000.0f, 0.0f, 1.0f, 1.0f },
};

// ============================================================================
// Plugin definition
// ============================================================================

static UnityAudioEffectDefinition g_definition = {
    sizeof(UnityAudioEffectDefinition),     // structsize
    sizeof(UnityAudioParameterDefinition),  // paramstructsize
    UNITY_AUDIO_PLUGIN_API_VERSION,         // apiversion
    0x010000,                               // pluginversion
    0,                                      // channels (0 = any)
    P_NUM,                                  // numparameters
    0,                                      // flags
    "AcousticIR Convolver",                 // name
    CreateCallback,                         // create
    ReleaseCallback,                        // release
    ResetCallback,                          // reset
    ProcessCallback,                        // process
    nullptr,                                // setposition
    g_paramDefs,                            // paramdefs
    SetFloatParameterCallback,              // setfloatparameter
    GetFloatParameterCallback,              // getfloatparameter
    nullptr,                                // getfloatbuffer
};

// ============================================================================
// DLL entry point - called by Unity to discover plugins
// ============================================================================

static UnityAudioEffectDefinition* g_definitionPtrs[] = { &g_definition };

extern "C" UNITY_AUDIODSP_EXPORT_API int AUDIO_CALLING_CONVENTION
UnityGetAudioEffectDefinitions(UnityAudioEffectDefinition*** descptr)
{
    *descptr = g_definitionPtrs;
    return 1; // Number of effects in this plugin
}

// ============================================================================
// Exported C functions for C# P/Invoke (IR loading)
// ============================================================================

extern "C"
{
    /// Load an IR into a specific plugin instance.
    /// Called from C# via [DllImport("AudioPluginAcousticIR")].
    UNITY_AUDIODSP_EXPORT_API void AcousticIR_LoadIR(
        int instanceId, const float* irData, int irLength, int sampleRate)
    {
        PluginData* data = FindInstance(instanceId);
        if (data)
        {
            data->engine.setIR(irData, irLength, sampleRate);
        }
    }

    /// Reset a specific plugin instance.
    UNITY_AUDIODSP_EXPORT_API void AcousticIR_Reset(int instanceId)
    {
        PluginData* data = FindInstance(instanceId);
        if (data)
        {
            data->engine.reset();
        }
    }

    /// Get the instance ID of the most recently created instance.
    /// Useful for C# to find the right instance to load an IR into.
    UNITY_AUDIODSP_EXPORT_API int AcousticIR_GetLatestInstanceId()
    {
        std::lock_guard<std::mutex> lock(g_registryMutex);
        return g_nextInstanceId - 1;
    }
}
