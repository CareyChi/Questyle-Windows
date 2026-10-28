#include "ApoAbi.h"
#include "Dsp.h"
#include <atomic>
#include <string>
#include <new>
#include <algorithm>
#include <xmmintrin.h>

namespace {
std::atomic<long> objects{0}, locks{0};
constexpr HRESULT InvalidFormat = static_cast<HRESULT>(0x887d0009);
constexpr PROPERTYKEY ParentKey{{0xb3f8fa53,0x0004,0x438e,{0x90,0x03,0x51,0xa4,0x6e,0x13,0x9b,0xfc}},2};
constexpr PROPERTYKEY EndpointKey{{0x1da5d803,0xd492,0x4edd,{0x8c,0x23,0xe0,0xc0,0xff,0xee,0x7f,0x0e}},4};
struct Status { volatile LONG64 revision, heartbeat; LONG rate, channels; volatile LONG error; LONG reserved; };
static_assert(sizeof(Status)==32);
bool dongle(const std::wstring& parent) {
    return parent.find(L"VID_0A12&PID_4007")!=std::wstring::npos || parent.find(L"VID_0E6A&PID_4007")!=std::wstring::npos;
}
HRESULT stringProperty(IPropertyStore* store, const PROPERTYKEY& key, std::wstring& result) {
    PROPVARIANT v{}; HRESULT hr=store->GetValue(key,&v);
    if (SUCCEEDED(hr)) { if(v.vt==VT_LPWSTR && v.pwszVal) result=v.pwszVal; else hr=E_INVALIDARG; }
    PropVariantClear(&v); return hr;
}
HRESULT readSettings(const std::wstring& path, peq::Settings& s) {
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,0,nullptr);
    if(file==INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    LARGE_INTEGER size{}; DWORD read=0;
    bool ok=GetFileSizeEx(file,&size) && size.QuadPart>=static_cast<LONGLONG>(sizeof(peq::Header)) && size.QuadPart<=static_cast<LONGLONG>(sizeof(s)) &&
        ReadFile(file,&s,static_cast<DWORD>(size.QuadPart),&read,nullptr) && read==size.QuadPart;
    CloseHandle(file);
    return ok && peq::valid(s,read) ? S_OK : E_INVALIDARG;
}
bool supported(AudioMediaType* media, AudioFormat& format) {
    return media && SUCCEEDED(media->GetUncompressedAudioFormat(&format)) && format.type==FloatFormat &&
        format.bytes==4 && format.bits==32 && format.channels>=1 && format.channels<=peq::MaxChannels &&
        std::isfinite(format.rate) && format.rate>=8000 && format.rate<=384000;
}
class PeqApo final : public AudioProcessingObject, public ProcessingRT, public ProcessingConfiguration, public SystemEffects {
    friend class Factory;
    std::atomic<ULONG> refs{1};
    struct InnerUnknown final : IUnknown {
        PeqApo& owner;
        explicit InnerUnknown(PeqApo& value):owner(value) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override { return owner.innerQuery(iid,out); }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++owner.refs; }
        ULONG STDMETHODCALLTYPE Release() override { ULONG n=--owner.refs; if(!n) delete &owner; return n; }
    } inner;
    IUnknown* controlling;
    HRESULT innerQuery(REFIID iid,void** out) {
        if(!out) return E_POINTER; *out=nullptr;
        if(iid==IID_IUnknown) { *out=&inner; inner.AddRef(); return S_OK; }
        if(iid==ApoIid) *out=static_cast<AudioProcessingObject*>(this);
        else if(iid==RtIid) *out=static_cast<ProcessingRT*>(this);
        else if(iid==ConfigIid) *out=static_cast<ProcessingConfiguration*>(this);
        else if(iid==EffectsIid) *out=static_cast<SystemEffects*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
#ifdef PEQ_TESTS
    friend struct PeqTestAccess;
#endif
    bool initialized=false, locked=false;
    AudioFormat format{}; UINT32 maxFrames=0;
    std::wstring directory, endpoint;
    HANDLE stop=nullptr, thread=nullptr, mapping=nullptr, statusFile=INVALID_HANDLE_VALUE;
    Status* status=nullptr;
    peq::Dsp* current=nullptr; peq::Dsp* fading=nullptr;
    std::atomic<peq::Dsp*> pending{nullptr}, retired{nullptr};
    UINT32 fadeFrame=0, fadeLength=0;
    void stopProcessing() {
        if(thread) { SetEvent(stop); WaitForSingleObject(thread,INFINITE); CloseHandle(thread); thread=nullptr; }
        if(stop) { CloseHandle(stop); stop=nullptr; }
        delete current; current=nullptr; delete fading; fading=nullptr;
        delete pending.exchange(nullptr); delete retired.exchange(nullptr);
        if(status) { InterlockedExchange64(&status->heartbeat,0); UnmapViewOfFile(status); status=nullptr; }
        if(mapping) { CloseHandle(mapping); mapping=nullptr; }
        if(statusFile!=INVALID_HANDLE_VALUE) { CloseHandle(statusFile); statusFile=INVALID_HANDLE_VALUE; }
    }
    static DWORD WINAPI watchEntry(void* self) { return static_cast<PeqApo*>(self)->watch(); }
    DWORD watch() {
        HANDLE changed=FindFirstChangeNotificationW(directory.c_str(),FALSE,FILE_NOTIFY_CHANGE_LAST_WRITE|FILE_NOTIFY_CHANGE_FILE_NAME);
        if(changed==INVALID_HANDLE_VALUE) { InterlockedExchange(&status->error,HRESULT_FROM_WIN32(GetLastError())); return 0; }
        HANDLE handles[]{stop,changed};
        long long acceptedRevision=current->revision;
        bool initial=true;
        while(true) {
            DWORD event=initial ? WAIT_OBJECT_0+1 : WaitForMultipleObjects(2,handles,FALSE,50);
            initial=false;
            delete retired.exchange(nullptr);
            if(event==WAIT_OBJECT_0) break;
            if(event==WAIT_TIMEOUT) continue; // reclaim completed transitions outside the audio callback
            if(event!=WAIT_OBJECT_0+1) { InterlockedExchange(&status->error,HRESULT_FROM_WIN32(GetLastError())); break; }
            // Re-arm before reading so an atomic file replacement cannot lose the next update.
            if(!FindNextChangeNotification(changed)) { InterlockedExchange(&status->error,HRESULT_FROM_WIN32(GetLastError())); break; }
            peq::Settings settings{}; HRESULT hr=readSettings(directory+L"\\peq.bin",settings);
            if(SUCCEEDED(hr)) {
                if(settings.header.revision==acceptedRevision) continue;
                auto* next=new(std::nothrow) peq::Dsp(settings,format.rate);
                if(!next) hr=E_OUTOFMEMORY;
                else if(next->count<0) { delete next; hr=InvalidFormat; }
                else { delete pending.exchange(next); acceptedRevision=settings.header.revision; }
            }
            InterlockedExchange(&status->error,hr);
        }
        FindCloseChangeNotification(changed); return 0;
    }
public:
    explicit PeqApo(IUnknown* outer=nullptr):inner(*this),controlling(outer?outer:&inner) { ++objects; }
    ~PeqApo() { stopProcessing(); --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        return controlling->QueryInterface(iid,out);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return controlling->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return controlling->Release(); }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 bytes,BYTE* data) override {
        if(initialized) return static_cast<HRESULT>(0x887d0001);
        if(!data || bytes<sizeof(InitEffects)) return E_INVALIDARG;
        auto* init=reinterpret_cast<InitEffects*>(data);
        if(init->size!=bytes || init->clsid!=PeqClsid || !init->endpoint) return E_INVALIDARG;
        std::wstring parent;
        HRESULT hr=stringProperty(init->endpoint,ParentKey,parent); if(FAILED(hr)) return hr;
        std::transform(parent.begin(),parent.end(),parent.begin(),towupper);
        if(!dongle(parent)) return E_ACCESSDENIED;
        hr=stringProperty(init->endpoint,EndpointKey,endpoint); if(FAILED(hr)) return hr;
        GUID parsed{}; if(FAILED(CLSIDFromString(endpoint.c_str(),&parsed))) return E_INVALIDARG;
        wchar_t canonical[39]{}; StringFromGUID2(parsed,canonical,39); endpoint=canonical;
        wchar_t path[32768]; DWORD size=sizeof(path);
        LSTATUS error=RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Questyle\\Peq",L"DataPath",RRF_RT_REG_SZ,nullptr,path,&size);
        if(error!=ERROR_SUCCESS) return HRESULT_FROM_WIN32(error);
        directory=path; initialized=true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { if(current) current->reset(); if(fading) fading->reset(); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetLatency(INT64* latency) override { if(!latency) return E_POINTER; if(!locked) return static_cast<HRESULT>(0x887d0006); *latency=0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(Registration** out) override {
        if(!out) return E_POINTER;
        *out=static_cast<Registration*>(CoTaskMemAlloc(sizeof(Registration))); if(!*out) return E_OUTOFMEMORY;
        **out={}; auto& p=**out; p.clsid=PeqClsid; p.flags=15;
        wcscpy_s(p.name,L"Questyle Dongle PEQ"); wcscpy_s(p.copyright,L"Questyle Windows contributors");
        p.major=1; p.minInputs=p.maxInputs=p.minOutputs=p.maxOutputs=1;
        p.maxInstances=UINT32_MAX; p.interfaceCount=1; p.interfaces[0]=ApoIid; return S_OK;
    }
    HRESULT formatSupport(AudioMediaType* opposite,AudioMediaType* requested,AudioMediaType** out) {
        if(!out) return E_POINTER; *out=nullptr; AudioFormat f{}, other{};
        if(!supported(requested,f)) return InvalidFormat;
        if(opposite && (!supported(opposite,other) || f.channels!=other.channels || f.rate!=other.rate)) return InvalidFormat;
        *out=requested; requested->AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(AudioMediaType* o,AudioMediaType* r,AudioMediaType** s) override { return formatSupport(o,r,s); }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(AudioMediaType* o,AudioMediaType* r,AudioMediaType** s) override { return formatSupport(o,r,s); }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) override { if(!count) return E_POINTER; *count=format.channels; return locked?S_OK:static_cast<HRESULT>(0x887d0006); }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs,ConnectionDescriptor** in,UINT32 outputs,ConnectionDescriptor** out) override {
        if(locked) return static_cast<HRESULT>(0x887d000a);
        if(!initialized) return static_cast<HRESULT>(0x887d0002);
        if(inputs!=1 || outputs!=1 || !in || !out || !in[0] || !out[0]) return E_INVALIDARG;
        AudioFormat other{};
        if(!supported(in[0]->format,format) || !supported(out[0]->format,other) || format.rate!=other.rate ||
            format.channels!=other.channels || out[0]->maxFrames<in[0]->maxFrames) return InvalidFormat;
        peq::Settings settings{}; HRESULT hr=readSettings(directory+L"\\peq.bin",settings); if(FAILED(hr)) return hr;
        current=new(std::nothrow) peq::Dsp(settings,format.rate);
        if(!current) return E_OUTOFMEMORY;
        if(current->count<0) { stopProcessing(); return InvalidFormat; }
        maxFrames=in[0]->maxFrames; fadeLength=static_cast<UINT32>(format.rate/100);
        std::wstring statusPath=directory+L"\\status-"+endpoint+L".bin";
        statusFile=CreateFileW(statusPath.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,CREATE_ALWAYS,0,nullptr);
        if(statusFile==INVALID_HANDLE_VALUE) { hr=HRESULT_FROM_WIN32(GetLastError()); stopProcessing(); return hr; }
        mapping=CreateFileMappingW(statusFile,nullptr,PAGE_READWRITE,0,sizeof(Status),nullptr);
        if(!mapping) { hr=HRESULT_FROM_WIN32(GetLastError()); stopProcessing(); return hr; }
        status=static_cast<Status*>(MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,sizeof(Status)));
        if(!status) { hr=HRESULT_FROM_WIN32(GetLastError()); stopProcessing(); return hr; }
        *status={}; status->rate=static_cast<LONG>(format.rate); status->channels=format.channels;
        stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        if(!stop) { hr=HRESULT_FROM_WIN32(GetLastError()); stopProcessing(); return hr; }
        thread=CreateThread(nullptr,0,watchEntry,this,0,nullptr);
        if(!thread) { hr=HRESULT_FROM_WIN32(GetLastError()); stopProcessing(); return hr; }
        locked=true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if(!locked) return static_cast<HRESULT>(0x887d0006);
        stopProcessing(); locked=false; return S_OK;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 frames) override { return frames; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 frames) override { return frames; }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs,ConnectionProperty** in,UINT32 outputs,ConnectionProperty** out) override {
        if(outputs!=1 || !out || !out[0]) return;
        auto& output=*out[0]; output.validFrames=0; output.flags=0;
        if(!locked || inputs!=1 || !in || !in[0] || in[0]->validFrames>maxFrames || in[0]->flags==0) return;
        const auto& input=*in[0];
        if(!fading && retired.load()==nullptr) if(auto* next=pending.exchange(nullptr)) { fading=current; current=next; fadeFrame=0; }
        float* dst=reinterpret_cast<float*>(output.buffer); const float* src=reinterpret_cast<const float*>(input.buffer);
        unsigned csr=_mm_getcsr(); _mm_setcsr(csr|0x8040); // restore caller's denormal mode below
        for(UINT32 frame=0;frame<input.validFrames;++frame) {
            const double mix=fading ? std::min(1.0,static_cast<double>(++fadeFrame)/fadeLength) : 1.0;
            for(UINT32 ch=0;ch<format.channels;++ch) {
                UINT32 i=frame*format.channels+ch; double x=input.flags==2 ? 0 : src[i];
                double y=current->sample(x,ch);
                if(fading) y=fading->sample(x,ch)*(1-mix)+y*mix;
                dst[i]=static_cast<float>(y);
            }
            if(fading && fadeFrame>=fadeLength) { retired.store(fading); fading=nullptr; }
        }
        _mm_setcsr(csr);
        output.validFrames=input.validFrames; output.flags=1; // IIR tails continue through silent input.
        InterlockedExchange64(&status->revision,current->revision);
        InterlockedExchange64(&status->heartbeat,static_cast<LONG64>(GetTickCount64()));
    }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory() { ++objects; } ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override { if(!out) return E_POINTER; *out=nullptr; if(iid!=IID_IUnknown && iid!=IID_IClassFactory) return E_NOINTERFACE; *out=this; AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n=--refs; if(!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** out) override {
        if(outer && iid!=IID_IUnknown) return CLASS_E_NOAGGREGATION;
        auto* apo=new(std::nothrow) PeqApo(outer); if(!apo) return E_OUTOFMEMORY;
        HRESULT hr=apo->inner.QueryInterface(iid,out); apo->inner.Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if(lock) ++locks; else --locks; return S_OK; }
};
}
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {
    if(clsid!=PeqClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory=new(std::nothrow) Factory; if(!factory) return E_OUTOFMEMORY;
    HRESULT hr=factory->QueryInterface(iid,out); factory->Release(); return hr;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return objects==0 && locks==0 ? S_OK : S_FALSE; }
extern "C" __declspec(dllexport) HRESULT WINAPI PeqResponse(const void* data,int bytes,double frequency,double rate,double* db) {
    if(!data || !db || bytes<32 || bytes>static_cast<int>(sizeof(peq::Settings)) || !std::isfinite(rate) || rate<8000 || rate>384000 ||
        !std::isfinite(frequency) || frequency<=0 || frequency>=rate/2) return E_INVALIDARG;
    peq::Settings s{}; memcpy(&s,data,bytes); if(!peq::valid(s,bytes)) return E_INVALIDARG;
    for(int i=0;i<s.header.count;++i) if(s.header.enabled && s.bands[i].enabled && s.bands[i].frequency>=rate/2) return InvalidFormat;
    *db=peq::response(s,frequency,rate); return S_OK;
}
extern "C" __declspec(dllexport) HRESULT WINAPI PeqResponseCurve(const void* data,int bytes,const double* frequencies,int count,double rate,double* db) {
    if(!data || !frequencies || !db || count<=0 || bytes<32 || bytes>static_cast<int>(sizeof(peq::Settings)) ||
        !std::isfinite(rate) || rate<8000 || rate>384000) return E_INVALIDARG;
    peq::Settings s{}; memcpy(&s,data,bytes); if(!peq::valid(s,bytes)) return E_INVALIDARG;
    struct MagnitudeFilter { double bSum,bDifference,bMiddle,aSum,aDifference,aMiddle; };
    MagnitudeFilter filters[peq::MaxBands]; int filterCount=0;
    for(int i=0;i<s.header.count;++i) if(s.header.enabled && s.bands[i].enabled) {
        if(s.bands[i].frequency>=rate/2) return InvalidFormat;
        const auto c=peq::coefficients(s.bands[i],rate);
        filters[filterCount++]={c.b0+c.b2,c.b0-c.b2,c.b1,1+c.a2,1-c.a2,c.a1};
    }
    for(int i=0;i<count;++i) if(!std::isfinite(frequencies[i]) || frequencies[i]<=0 || frequencies[i]>=rate/2) return E_INVALIDARG;
    for(int i=0;i<count;++i) {
        const double angle=2*peq::Pi*frequencies[i]/rate, cosine=cos(angle), sine=sin(angle);
        double value=s.header.enabled?s.header.preamp:0;
        for(int j=0;j<filterCount;++j) {
            const auto& c=filters[j];
            const double nr=c.bSum*cosine+c.bMiddle, ni=c.bDifference*sine;
            const double dr=c.aSum*cosine+c.aMiddle, di=c.aDifference*sine;
            value+=10*log10((nr*nr+ni*ni)/(dr*dr+di*di));
        }
        db[i]=value;
    }
    return S_OK;
}
