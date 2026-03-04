cmake_minimum_required(VERSION 3.22)
project(AudioPluginAcousticIR VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)

# pffft - Pretty Fast FFT (public domain)
add_library(pffft STATIC
    third_party/pffft/pffft.c
)
target_include_directories(pffft PUBLIC third_party/pffft)
# pffft uses SSE on x86
if(CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64|i[3-6]86")
    target_compile_definitions(pffft PRIVATE _USE_SSE=1)
endif()

# Unity Native Audio Plugin
add_library(AudioPluginAcousticIR SHARED
    src/PluginInterface.cpp
    src/ConvolutionEngine.cpp
    src/FFTProcessor.cpp
    src/IRBuffer.cpp
)

target_include_directories(AudioPluginAcousticIR PRIVATE
    include
    third_party/pffft
)

target_link_libraries(AudioPluginAcousticIR PRIVATE pffft)

# Platform-specific settings
if(WIN32)
    set_target_properties(AudioPluginAcousticIR PROPERTIES
        SUFFIX ".dll"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/../Runtime/Plugins/x86_64"
    )
    # Suppress C4996 warnings (deprecated functions)
    target_compile_definitions(AudioPluginAcousticIR PRIVATE _CRT_SECURE_NO_WARNINGS)
elseif(APPLE)
    set_target_properties(AudioPluginAcousticIR PROPERTIES
        SUFFIX ".bundle"
        BUNDLE TRUE
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../Runtime/Plugins"
    )
elseif(UNIX)
    set_target_properties(AudioPluginAcousticIR PROPERTIES
        SUFFIX ".so"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../Runtime/Plugins/x86_64"
    )
endif()

# Optimize for release
if(CMAKE_BUILD_TYPE STREQUAL "Release" OR CMAKE_CONFIGURATION_TYPES)
    target_compile_options(AudioPluginAcousticIR PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/O2 /fp:fast>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-O3 -ffast-math>
    )
endif()
