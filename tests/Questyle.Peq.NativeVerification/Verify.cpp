#define PEQ_TESTS
#include "../../src/Questyle.Peq.Native/PeqApo.cpp"
#include <cstdio>
#include <vector>
#include <stdexcept>
#include <filesystem>
// Independent SDK declarations verify the hand-declared ABI used by the product.
#include <audioenginebaseapo.h>
static_assert(sizeof(ConnectionProperty)==sizeof(APO_CONNECTION_PROPERTY));
static_assert(offsetof(ConnectionProperty,flags)==offsetof(APO_CONNECTION_PROPERTY,u32BufferFlags));
static_assert(offsetof(ConnectionProperty,signature)==offsetof(APO_CONNECTION_PROPERTY,u32Signature));
static_assert(sizeof(ConnectionDescriptor)==sizeof(APO_CONNECTION_DESCRIPTOR));
static_assert(sizeof(Registration)==sizeof(APO_REG_PROPERTIES));
static_assert(sizeof(InitEffects)==sizeof(APOInitSystemEffects));
static_assert(sizeof(AudioFormat)==sizeof(UNCOMPRESSEDAUDIOFORMAT));
namespace {
struct PeqTestAccess {
    static void initialize(PeqApo* apo,const std::wstring& path) { apo->directory=path; apo->endpoint=L"{00000000-0000-0000-0000-000000000001}"; apo->initialized=true; }
};
struct Media final : AudioMediaType {
    AudioFormat f{FloatFormat,2,4,32,48000,3};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* out) override { *out=FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE IsEqual(AudioMediaType*,DWORD*) override { return E_NOTIMPL; }
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override { return nullptr; }
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(AudioFormat* out) override { *out=f; return S_OK; }
};
struct Properties final : IPropertyStore {
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetAt(DWORD,PROPERTYKEY*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetValue(REFPROPERTYKEY,PROPVARIANT* value) override {
        const wchar_t parent[]=L"{1}.USB\\VID_1234&PID_4007\\OTHER";
        value->vt=VT_LPWSTR; value->pwszVal=static_cast<wchar_t*>(CoTaskMemAlloc(sizeof(parent))); memcpy(value->pwszVal,parent,sizeof(parent)); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetValue(REFPROPERTYKEY,REFPROPVARIANT) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Commit() override { return E_NOTIMPL; }
};
struct Outer final : IUnknown {
    ULONG refs=1;
    IUnknown* inner=nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override { if(iid==IID_IUnknown){*out=this;AddRef();return S_OK;} return inner->QueryInterface(iid,out); }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
};
int checks=0;
void check(bool value,const char* name) { if(!value) throw std::runtime_error(name); printf("PASS %s\n",name); ++checks; }
peq::Settings settings(int64_t revision,bool enabled,double preamp,std::initializer_list<peq::Band> bands) {
    peq::Settings s{}; s.header={0x31514550,1,revision,enabled,static_cast<int>(bands.size()),preamp};
    std::copy(bands.begin(),bands.end(),s.bands); return s;
}
void write(const std::wstring& directory,const peq::Settings& s) {
    const auto temporary=directory+L"\\peq.tmp", target=directory+L"\\peq.bin";
    HANDLE file=CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,0,nullptr); DWORD written;
    if(file==INVALID_HANDLE_VALUE || !WriteFile(file,&s,sizeof(peq::Header)+s.header.count*sizeof(peq::Band),&written,nullptr)) throw std::runtime_error("write config");
    CloseHandle(file); if(!MoveFileExW(temporary.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING)) throw std::runtime_error("replace config");
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2) return 2;
    try {
        std::wstring directory=argv[1]; std::filesystem::create_directories(directory);
        check(DllCanUnloadNow()==S_OK,"unloaded COM state");
        IClassFactory* factory=nullptr;
        check(SUCCEEDED(DllGetClassObject(PeqClsid,IID_IClassFactory,reinterpret_cast<void**>(&factory))),"COM class factory");
        Outer outer;
        check(SUCCEEDED(factory->CreateInstance(&outer,IID_IUnknown,reinterpret_cast<void**>(&outer.inner))),"audio engine can create an aggregated APO");
        IAudioProcessingObject* aggregated=nullptr; IUnknown* identity=nullptr;
        check(SUCCEEDED(outer.inner->QueryInterface(ApoIid,reinterpret_cast<void**>(&aggregated))) && SUCCEEDED(aggregated->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(&identity))) && identity==&outer,"aggregated interfaces preserve the controlling IUnknown identity");
        identity->Release(); aggregated->Release(); outer.inner->Release();
        check(outer.refs==1,"aggregated references release without retaining the outer object");
        IAudioProcessingObject* sdkApo=nullptr;
        check(SUCCEEDED(factory->CreateInstance(nullptr,ApoIid,reinterpret_cast<void**>(&sdkApo))),"SDK IAudioProcessingObject interface");
        APO_REG_PROPERTIES* registration=nullptr;
        check(SUCCEEDED(sdkApo->GetRegistrationProperties(&registration)) && registration->Flags==15 && registration->clsid==PeqClsid,"SDK reads APO registration ABI");
        CoTaskMemFree(registration);
        Properties properties; InitEffects init{sizeof(InitEffects),PeqClsid,&properties,nullptr,nullptr,nullptr};
        check(sdkApo->Initialize(sizeof(init),reinterpret_cast<BYTE*>(&init))==E_ACCESSDENIED,"APO rejects a non-dongle endpoint before opening configuration");
        auto* apo=static_cast<PeqApo*>(reinterpret_cast<AudioProcessingObject*>(sdkApo));
        PeqTestAccess::initialize(apo,directory);
        IAudioProcessingObjectConfiguration* cfg=nullptr; IAudioProcessingObjectRT* rt=nullptr;
        check(SUCCEEDED(sdkApo->QueryInterface(ConfigIid,reinterpret_cast<void**>(&cfg))) && SUCCEEDED(sdkApo->QueryInterface(RtIid,reinterpret_cast<void**>(&rt))),"SDK configuration and realtime interfaces");
        Media media; AudioMediaType* accepted=nullptr;
        check(SUCCEEDED(apo->IsInputFormatSupported(nullptr,&media,&accepted)),"48 kHz float stereo negotiation");
        media.f.bits=24; check(FAILED(apo->IsInputFormatSupported(nullptr,&media,&accepted)),"reject integer samples instead of misinterpreting them"); media.f.bits=32;
        constexpr UINT32 frames=480;
        APO_CONNECTION_DESCRIPTOR descriptor{}; descriptor.u32MaxFrameCount=frames; descriptor.pFormat=reinterpret_cast<IAudioMediaType*>(&media);
        APO_CONNECTION_DESCRIPTOR* descriptors[]{&descriptor};
        auto s=settings(1,true,0,{{1,0,1000,6,1}}); write(directory,s);
        check(SUCCEEDED(cfg->LockForProcess(1,descriptors,1,descriptors)),"SDK locks the real APO with isolated files");
        std::vector<float> input(frames*2), output(frames*2);
        APO_CONNECTION_PROPERTY inputProperty{}, outputProperty{};
        inputProperty.pBuffer=reinterpret_cast<UINT_PTR>(input.data()); inputProperty.u32ValidFrameCount=frames; inputProperty.u32BufferFlags=BUFFER_VALID;
        outputProperty.pBuffer=reinterpret_cast<UINT_PTR>(output.data());
        APO_CONNECTION_PROPERTY* inputs[]{&inputProperty}; APO_CONNECTION_PROPERTY* outputs[]{&outputProperty};
        double inPower=0,outPower=0,otherPower=0;
        for(int block=0;block<100;++block) {
            for(UINT32 frame=0;frame<frames;++frame) { input[frame*2]=static_cast<float>(0.1*sin(2*peq::Pi*1000*(block*frames+frame)/48000)); input[frame*2+1]=0; }
            rt->APOProcess(1,inputs,1,outputs);
            if(block>20) for(UINT32 frame=0;frame<frames;++frame) { inPower+=input[frame*2]*input[frame*2]; outPower+=output[frame*2]*output[frame*2]; otherPower+=output[frame*2+1]*output[frame*2+1]; }
        }
        check(std::abs(10*log10(outPower/inPower)-6)<0.002,"measured sine gain is +6 dB at the requested center frequency");
        check(otherPower==0,"independent channel histories prevent stereo crosstalk");
        inputProperty.u32BufferFlags=BUFFER_SILENT; rt->APOProcess(1,inputs,1,outputs);
        check(outputProperty.u32BufferFlags==BUFFER_VALID && std::any_of(output.begin(),output.end(),[](float x){return x!=0;}),"silent input drains the IIR tail");
        inputProperty.u32BufferFlags=BUFFER_VALID;
        auto waitRevision=[&](long long revision) {
            for(int i=0;i<100;++i) { rt->APOProcess(1,inputs,1,outputs); Sleep(10);
                HANDLE file=CreateFileW((directory+L"\\status-{00000000-0000-0000-0000-000000000001}.bin").c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
                Status status{}; DWORD read=0; ReadFile(file,&status,sizeof(status),&read,nullptr); CloseHandle(file);
                if(status.revision==revision && status.error==0) return true;
            } return false;
        };
        write(directory,settings(2,true,-6,{})); Sleep(20); write(directory,settings(3,false,0,{{1,0,1000,20,1}}));
        check(waitRevision(3),"rapid atomic configuration changes reach the audio callback");
        Sleep(30); rt->APOProcess(1,inputs,1,outputs);
        check(std::equal(input.begin(),input.end(),output.begin()),"master bypass returns exact float samples");
        auto bad=settings(4,true,0,{{1,0,1000,6,1}}); bad.bands[0].q=0;
        write(directory,bad); Sleep(80); rt->APOProcess(1,inputs,1,outputs);
        check(std::equal(input.begin(),input.end(),output.begin()),"invalid configuration cannot replace the last accepted DSP");
        check(SUCCEEDED(cfg->UnlockForProcess()),"unlock stops the watcher and releases mapped files");
        write(directory,settings(5,true,0,{{1,0,22000,1,1}})); media.f.rate=44100;
        check(SUCCEEDED(cfg->LockForProcess(1,descriptors,1,descriptors)),"44.1 kHz accepts a representable 22 kHz band"); cfg->UnlockForProcess();
        media.f.rate=32000; check(FAILED(cfg->LockForProcess(1,descriptors,1,descriptors)),"a band above the current Nyquist limit is explicitly rejected");
        write(directory,settings(6,true,0,{{1,0,1000,1,1}}));
        check(SUCCEEDED(cfg->LockForProcess(1,descriptors,1,descriptors)),"32 kHz accepts representable filters without silently dropping bands"); cfg->UnlockForProcess();
        rt->Release(); cfg->Release(); sdkApo->Release(); factory->Release();
        check(DllCanUnloadNow()==S_OK,"all COM interfaces and worker resources are released");
        auto shelf=settings(0,true,-3,{{1,1,100,6,0.70710678}});
        check(std::abs(peq::response(shelf,0.01,48000)-3)<0.001 && std::abs(peq::response(shelf,20000,48000)+3)<0.001,"low shelf asymptotes include preamp");
        auto notch=settings(0,true,0,{{1,5,1000,0,3}});
        check(peq::response(notch,1000,48000)<-200,"notch cancels its center frequency");
        printf("%d native PEQ checks passed\n",checks); return 0;
    } catch(const std::exception& e) { fprintf(stderr,"FAIL %s\n",e.what()); return 1; }
}
