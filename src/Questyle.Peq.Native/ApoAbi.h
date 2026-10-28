#pragma once
// Windows SDK APO ABI subset. Layout and method order follow audioenginebaseapo.h,
// audiomediatype.h and AudioAPOTypes.h; no Equalizer APO implementation is used.
#include <windows.h>
#include <mmreg.h>
#include <propsys.h>
#include <cstdint>

inline constexpr GUID PeqClsid{0x6c4460ce,0x5428,0x4c08,{0x97,0xe5,0x2d,0x9e,0xf8,0xe6,0xb4,0xd1}};
inline constexpr GUID ApoIid{0xfd7f2b29,0x24d0,0x4b5c,{0xb1,0x77,0x59,0x2c,0x39,0xf9,0xca,0x10}};
inline constexpr GUID RtIid{0x9e1d6a6d,0xddbc,0x4e95,{0xa4,0xc7,0xad,0x64,0xba,0x37,0x84,0x6c}};
inline constexpr GUID ConfigIid{0x0e5ed805,0xaba6,0x49c3,{0x8f,0x9a,0x2b,0x8c,0x88,0x9c,0x4f,0xa8}};
inline constexpr GUID EffectsIid{0x5fa00f27,0xadd6,0x499a,{0x8a,0x9d,0x6b,0x98,0x52,0x1f,0xa7,0x5b}};
inline constexpr GUID FloatFormat{3,0,0x10,{0x80,0,0,0xaa,0,0x38,0x9b,0x71}};

struct AudioFormat { GUID type; DWORD channels, bytes, bits; float rate; DWORD mask; };
struct AudioMediaType : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL*) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsEqual(AudioMediaType*, DWORD*) = 0;
    virtual const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(AudioFormat*) = 0;
};
struct ConnectionDescriptor { int type; UINT_PTR buffer; UINT32 maxFrames; AudioMediaType* format; UINT32 signature; };
struct ConnectionProperty { UINT_PTR buffer; UINT32 validFrames; int flags; UINT32 signature; };
struct Registration {
    CLSID clsid; int flags; WCHAR name[256], copyright[256];
    UINT32 major, minor, minInputs, maxInputs, minOutputs, maxOutputs, maxInstances, interfaceCount;
    IID interfaces[1];
};
struct InitEffects {
    UINT32 size; CLSID clsid; IPropertyStore* endpoint; IPropertyStore* effects;
    void* reserved; void* devices;
};
struct AudioProcessingObject : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Reset() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetLatency(INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetRegistrationProperties(Registration**) = 0;
    virtual HRESULT STDMETHODCALLTYPE Initialize(UINT32, BYTE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsInputFormatSupported(AudioMediaType*, AudioMediaType*, AudioMediaType**) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(AudioMediaType*, AudioMediaType*, AudioMediaType**) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32*) = 0;
};
struct ProcessingRT : IUnknown {
    virtual void STDMETHODCALLTYPE APOProcess(UINT32, ConnectionProperty**, UINT32, ConnectionProperty**) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32) = 0;
};
struct ProcessingConfiguration : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE LockForProcess(UINT32, ConnectionDescriptor**, UINT32, ConnectionDescriptor**) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnlockForProcess() = 0;
};
struct SystemEffects : IUnknown {};
