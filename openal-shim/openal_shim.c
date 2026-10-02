/*
 * OpenAL 1.1 shim for HarmonyOS (OHOS)
 *
 * 实现 MonoGame DesktopGL 音频路径所需的 OpenAL/ALC 函数子集：
 *  - 设备/上下文：alcOpenDevice/alcCreateContext/alcMakeContextCurrent/...
 *  - 静态缓冲播放：alGenBuffers/alBufferData/alSourcei(AL_BUFFER)
 *  - 流式队列播放：alSourceQueueBuffers/alSourceUnqueueBuffers/AL_BUFFERS_PROCESSED
 *  - 增益/音调/循环/声像
 * 后端：OHAudio C API（OH_AudioRenderer 回调模式），回调内软件混音。
 * 不支持：EFX 特效/滤波、IMA4/MSADPCM 直传（MonoGame 检测不到扩展会自行解码）、捕获。
 */
#include "al_shim_types.h"
#include <ohaudio/native_audiostreambuilder.h>
#include <ohaudio/native_audiorenderer.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include <pthread.h>

/* ---------- 常量（与 AL 标准一致） ---------- */
#define AL_NONE                0
#define AL_FALSE               0
#define AL_TRUE                1
#define AL_SOURCE_RELATIVE     0x0202
#define AL_CONE_INNER_ANGLE    0x1001
#define AL_CONE_OUTER_ANGLE    0x1002
#define AL_PITCH               0x1003
#define AL_POSITION            0x1004
#define AL_DIRECTION           0x1005
#define AL_VELOCITY            0x1006
#define AL_LOOPING             0x1007
#define AL_BUFFER              0x1009
#define AL_GAIN                0x100A
#define AL_SOURCE_STATE        0x1010
#define AL_INITIAL             0x1011
#define AL_PLAYING             0x1012
#define AL_PAUSED              0x1013
#define AL_STOPPED             0x1014
#define AL_BUFFERS_QUEUED      0x1015
#define AL_BUFFERS_PROCESSED   0x1016
#define AL_SEC_OFFSET          0x1024
#define AL_SAMPLE_OFFSET       0x1025
#define AL_BYTE_OFFSET         0x1026
#define AL_FORMAT_MONO8        0x1100
#define AL_FORMAT_MONO16       0x1101
#define AL_FORMAT_STEREO8      0x1102
#define AL_FORMAT_STEREO16     0x1103
#define AL_FREQUENCY           0x2001
#define AL_BITS                0x2002
#define AL_CHANNELS            0x2003
#define AL_SIZE                0x2004
#define AL_NO_ERROR            0
#define AL_VENDOR              0xB001
#define AL_VERSION             0xB002
#define AL_RENDERER            0xB003
#define AL_EXTENSIONS          0xB004

#define ALC_FREQUENCY          0x1007
#define ALC_REFRESH            0x1008
#define ALC_SYNC               0x1009
#define ALC_MONO_SOURCES       0x1010
#define ALC_STEREO_SOURCES     0x1011
#define ALC_NO_ERROR           0
#define ALC_DEFAULT_DEVICE_SPECIFIER 0x1004
#define ALC_DEVICE_SPECIFIER   0x1005
#define ALC_EXTENSIONS         0x1006
#define ALC_CAPTURE_DEFAULT_DEVICE_SPECIFIER 0x310
#define ALC_CAPTURE_DEVICE_SPECIFIER 0x311
#define ALC_ATTRIBUTES_SIZE    0x1002
#define ALC_ALL_ATTRIBUTES     0x1003

#define MAX_BUFFERS 4096
#define MAX_SOURCES 512

typedef struct ALBuffer {
    int used;
    int channels;
    int bits;
    int freq;
    uint8_t *data;
    int size; /* bytes */
} ALBuffer;

typedef struct QBuf {
    ALuint bid;
    uint32_t offset; /* 当前 buffer 内字节偏移（播放中） */
    struct QBuf *next;
} QBuf;

typedef struct ALSource {
    int used;
    ALenum state;
    float gain;
    float pitch;
    ALint looping;
    float posX;
    ALuint staticBuf;
    uint32_t staticOffset;
    QBuf *qPlay;
    QBuf *qDone;
    int nQueued;
    int nProcessed;
} ALSource;

static ALBuffer g_buffers[MAX_BUFFERS];
static ALSource g_sources[MAX_SOURCES];
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static OH_AudioRenderer *g_renderer = NULL;
static int g_devFreq = 44100;
static int g_contextLive = 0;
static const char *g_alcExt = "ALC_ENUMERATE_ALL_EXT ALC_ENUMERATION_EXT ALC_EXT_CAPTURE ALC_SOFT_pause_device";
static const char *g_alExt = "AL_EXT_FLOAT32 AL_SOFT_pause_device";
static const char *g_specifier = "OHAudio (OpenAL Shim)";
static int g_cbCount = 0;

/* ------------------------------------------------------------------ */
static ALBuffer *getBuf(ALuint id)
{
    if (id == 0 || id > MAX_BUFFERS || !g_buffers[id - 1].used)
        return NULL;
    return &g_buffers[id - 1];
}

static ALSource *getSrc(ALuint id)
{
    if (id == 0 || id > MAX_SOURCES || !g_sources[id - 1].used)
        return NULL;
    return &g_sources[id - 1];
}

/* 供一个 source 的下一帧立体声样本（16bit），返回 1 有数据 0 结束 */
static int srcNextFrame(ALSource *s, int *l, int *r)
{
    if (s->staticBuf) {
        ALBuffer *b = getBuf(s->staticBuf);
        if (!b || b->size == 0)
            return 0;
        if (s->staticOffset >= (uint32_t)b->size) {
            if (s->looping) {
                s->staticOffset = 0;
            } else {
                return 0;
            }
        }
        uint8_t *p = b->data + s->staticOffset;
        if (b->channels == 2) {
            if (b->bits == 16) {
                int16_t *sp = (int16_t *)p;
                *l = sp[0]; *r = sp[1];
                s->staticOffset += 4;
            } else {
                *l = (p[0] - 128) << 8; *r = (p[1] - 128) << 8;
                s->staticOffset += 2;
            }
        } else {
            if (b->bits == 16) {
                *l = *r = *(int16_t *)p;
                s->staticOffset += 2;
            } else {
                *l = *r = (p[0] - 128) << 8;
                s->staticOffset += 1;
            }
        }
        return 1;
    }
    while (s->qPlay) {
        ALBuffer *b = getBuf(s->qPlay->bid);
        if (!b || s->qPlay->offset >= (uint32_t)b->size) {
            QBuf *done = s->qPlay;
            s->qPlay = done->next;
            done->next = s->qDone;
            s->qDone = done;
            s->nProcessed++;
            s->nQueued--;
            continue;
        }
        uint8_t *p = b->data + s->qPlay->offset;
        if (b->channels == 2) {
            if (b->bits == 16) {
                int16_t *sp = (int16_t *)p;
                *l = sp[0]; *r = sp[1];
                s->qPlay->offset += 4;
            } else {
                *l = (p[0] - 128) << 8; *r = (p[1] - 128) << 8;
                s->qPlay->offset += 2;
            }
        } else {
            if (b->bits == 16) {
                *l = *r = *(int16_t *)p;
                s->qPlay->offset += 2;
            } else {
                *l = *r = (p[0] - 128) << 8;
                s->qPlay->offset += 1;
            }
        }
        return 1;
    }
    return 0;
}

/* OHAudio 写回调：直接软件混音输出（与 API 线程用 g_lock 互斥） */
static int32_t onWriteData(OH_AudioRenderer *renderer, void *userData,
                           void *buffer, int32_t length)
{
    (void)renderer; (void)userData;
    int16_t *out = (int16_t *)buffer;
    int frames = length / 4; /* stereo16 */
    memset(buffer, 0, length);

    if ((++g_cbCount % 200) == 1)
        fprintf(stderr, "[openal-shim] write callback #%d frames=%d\n", g_cbCount, frames);

    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < MAX_SOURCES; i++) {
        ALSource *s = &g_sources[i];
        if (!s->used || s->state != AL_PLAYING)
            continue;

        float pitch = s->pitch > 0.01f ? s->pitch : 1.0f;
        int stepInt = (int)pitch;
        float stepFrac = pitch - stepInt;
        float frac = 0.0f;
        float lPan = 1.0f, rPan = 1.0f;
        if (s->posX < 0.0f) { rPan = 1.0f + s->posX; }
        else if (s->posX > 0.0f) { lPan = 1.0f - s->posX; }
        if (lPan < 0) lPan = 0;
        if (rPan < 0) rPan = 0;

        for (int f = 0; f < frames; f++) {
            int steps = stepInt + ((frac + stepFrac) >= 1.0f ? 1 : 0);
            frac = (frac + stepFrac) >= 1.0f ? frac + stepFrac - 1.0f : frac + stepFrac;
            int l = 0, r = 0;
            int got = 0;
            int adv = steps < 1 ? 1 : steps;
            for (int k = 0; k < adv; k++) {
                got = srcNextFrame(s, &l, &r);
                if (!got) break;
            }
            if (!got) {
                s->state = AL_STOPPED;
                break;
            }
            int mixL = (int)(l * s->gain * lPan);
            int mixR = (int)(r * s->gain * rPan);
            out[f * 2] = (int16_t)(out[f * 2] + (mixL > 32767 ? 32767 : (mixL < -32768 ? -32768 : mixL)));
            out[f * 2 + 1] = (int16_t)(out[f * 2 + 1] + (mixR > 32767 ? 32767 : (mixR < -32768 ? -32768 : mixR)));
        }
    }
    pthread_mutex_unlock(&g_lock);
    return 0;
}

static int32_t onInterrupt(OH_AudioRenderer *r, void *ud, OH_AudioInterrupt_ForceType t, OH_AudioInterrupt_Hint h)
{
    (void)r; (void)ud;
    fprintf(stderr, "[openal-shim] interrupt: force=%d hint=%d\n", (int)t, (int)h);
    return 0;
}
static int32_t onStreamEvent(OH_AudioRenderer *r, void *ud, OH_AudioStream_Event e)
{
    (void)r; (void)ud;
    fprintf(stderr, "[openal-shim] stream event: %d\n", (int)e);
    return 0;
}
static int32_t onError(OH_AudioRenderer *r, void *ud, OH_AudioStream_Result err)
{
    (void)r; (void)ud;
    fprintf(stderr, "[openal-shim] renderer error: %d\n", (int)err);
    return 0;
}

/* ------------------------------------------------------------------ */
/* ALC */

ALC_API ALCdevice *ALC_APIENTRY alcOpenDevice(const ALCchar *devicename)
{
    (void)devicename;
    if (g_renderer)
        return (ALCdevice *)(uintptr_t)1;

    OH_AudioStreamBuilder *builder = NULL;
    OH_AudioStream_Result r;

    r = OH_AudioStreamBuilder_Create(&builder, AUDIOSTREAM_TYPE_RENDERER);
    if (r != AUDIOSTREAM_SUCCESS) {
        fprintf(stderr, "[openal-shim] builder create failed %d\n", r);
        return NULL;
    }
    OH_AudioStreamBuilder_SetSamplingRate(builder, g_devFreq);
    OH_AudioStreamBuilder_SetChannelCount(builder, 2);
    OH_AudioStreamBuilder_SetSampleFormat(builder, AUDIOSTREAM_SAMPLE_S16LE);
    OH_AudioStreamBuilder_SetEncodingType(builder, AUDIOSTREAM_ENCODING_TYPE_RAW);
    OH_AudioStreamBuilder_SetLatencyMode(builder, AUDIOSTREAM_LATENCY_MODE_NORMAL);
    OH_AudioStreamBuilder_SetRendererInfo(builder, AUDIOSTREAM_USAGE_MUSIC);

    OH_AudioRenderer_Callbacks cb;
    memset(&cb, 0, sizeof(cb));
    cb.OH_AudioRenderer_OnWriteData = onWriteData;
    cb.OH_AudioRenderer_OnInterruptEvent = onInterrupt;
    cb.OH_AudioRenderer_OnStreamEvent = onStreamEvent;
    cb.OH_AudioRenderer_OnError = onError;
    OH_AudioStreamBuilder_SetRendererCallback(builder, cb, NULL);

    r = OH_AudioStreamBuilder_GenerateRenderer(builder, &g_renderer);
    if (r != AUDIOSTREAM_SUCCESS || !g_renderer) {
        fprintf(stderr, "[openal-shim] GenerateRenderer failed %d\n", r);
        OH_AudioStreamBuilder_Destroy(builder);
        return NULL;
    }
    r = OH_AudioRenderer_Start(g_renderer);
    fprintf(stderr, "[openal-shim] renderer started: ret=%d (%d Hz stereo16)\n", r, g_devFreq);
    OH_AudioStreamBuilder_Destroy(builder);
    return (ALCdevice *)(uintptr_t)1;
}

ALC_API ALCboolean ALC_APIENTRY alcCloseDevice(ALCdevice *device)
{
    (void)device;
    if (g_renderer) {
        OH_AudioRenderer_Stop(g_renderer);
        OH_AudioRenderer_Release(g_renderer);
        g_renderer = NULL;
    }
    return AL_TRUE;
}

ALC_API ALCcontext *ALC_APIENTRY alcCreateContext(ALCdevice *device, const ALCint *attrlist)
{
    (void)device; (void)attrlist;
    if (!g_renderer)
        return NULL;
    return (ALCcontext *)(uintptr_t)1;
}

ALC_API void ALC_APIENTRY alcDestroyContext(ALCcontext *context)
{
    (void)context;
    g_contextLive = 0;
}

ALC_API ALCboolean ALC_APIENTRY alcMakeContextCurrent(ALCcontext *context)
{
    g_contextLive = (context != NULL);
    return AL_TRUE;
}

ALC_API void ALC_APIENTRY alcProcessContext(ALCcontext *context) { (void)context; }
ALC_API void ALC_APIENTRY alcSuspendContext(ALCcontext *context) { (void)context; }
ALC_API ALCcontext *ALC_APIENTRY alcGetCurrentContext(void) { return (ALCcontext *)(uintptr_t)(g_contextLive ? 1 : 0); }
ALC_API ALCenum ALC_APIENTRY alcGetError(ALCdevice *device) { (void)device; return ALC_NO_ERROR; }

ALC_API const ALCchar *ALC_APIENTRY alcGetString(ALCdevice *device, ALCenum param)
{
    (void)device;
    switch (param) {
        case ALC_DEFAULT_DEVICE_SPECIFIER:
        case ALC_DEVICE_SPECIFIER:
            return (const ALCchar *)g_specifier;
        case ALC_EXTENSIONS:
            return (const ALCchar *)g_alcExt;
        default:
            return (const ALCchar *)"";
    }
}

ALC_API void ALC_APIENTRY alcGetIntegerv(ALCdevice *device, ALCenum param, ALCsizei size, ALCint *values)
{
    (void)device;
    if (!values || size <= 0)
        return;
    if (param == ALC_ATTRIBUTES_SIZE && size >= 1) {
        values[0] = 4;
    } else if (param == ALC_ALL_ATTRIBUTES && size >= 4) {
        values[0] = ALC_FREQUENCY; values[1] = g_devFreq;
        values[2] = 0;
    }
}

ALC_API ALCboolean ALC_APIENTRY alcIsExtensionPresent(ALCdevice *device, const ALCchar *extname)
{
    (void)device;
    if (!extname)
        return AL_FALSE;
    return strstr(g_alcExt, extname) != NULL ? AL_TRUE : AL_FALSE;
}

ALC_API void *ALC_APIENTRY alcGetProcAddress(const ALCchar *fname) { (void)fname; return NULL; }

/* ALC_SOFT_pause_device */
ALC_API void ALC_APIENTRY alcDevicePauseSOFT(ALCdevice *device)
{
    (void)device;
    if (g_renderer)
        OH_AudioRenderer_Pause(g_renderer);
}

ALC_API void ALC_APIENTRY alcDeviceResumeSOFT(ALCdevice *device)
{
    (void)device;
    if (g_renderer)
        OH_AudioRenderer_Start(g_renderer);
}

/* 捕获 stub */
ALC_API ALCdevice *ALC_APIENTRY alcCaptureOpenDevice(const ALCchar *d, ALCuint f, ALCenum fmt, ALCsizei sz)
{ (void)d; (void)f; (void)fmt; (void)sz; return NULL; }
ALC_API ALCboolean ALC_APIENTRY alcCaptureCloseDevice(ALCdevice *device) { (void)device; return AL_FALSE; }
ALC_API void ALC_APIENTRY alcCaptureStart(ALCdevice *device) { (void)device; }
ALC_API void ALC_APIENTRY alcCaptureStop(ALCdevice *device) { (void)device; }
ALC_API void ALC_APIENTRY alcCaptureSamples(ALCdevice *device, ALCvoid *buffer, ALCsizei samples) { (void)device; (void)buffer; (void)samples; }

/* ------------------------------------------------------------------ */
/* AL */

AL_API ALenum AL_APIENTRY alGetError(void) { return AL_NO_ERROR; }

AL_API const ALchar *AL_APIENTRY alGetString(ALenum param)
{
    switch (param) {
        case AL_VENDOR: return "Zai";
        case AL_VERSION: return "1.1 ALSOFT-SHIM/OHOS";
        case AL_RENDERER: return "OHAudio Software Mixer";
        case AL_EXTENSIONS: return (const ALchar *)g_alExt;
        default: return "";
    }
}

AL_API ALboolean AL_APIENTRY alIsExtensionPresent(const ALchar *extname)
{
    if (!extname)
        return AL_FALSE;
    return strstr(g_alExt, extname) != NULL ? AL_TRUE : AL_FALSE;
}

AL_API void *AL_APIENTRY alGetProcAddress(const ALchar *fname) { (void)fname; return NULL; }
AL_API ALenum AL_APIENTRY alGetEnumValue(const ALchar *ename) { (void)ename; return 0; }

AL_API void AL_APIENTRY alEnable(ALenum capability) { (void)capability; }
AL_API void AL_APIENTRY alDisable(ALenum capability) { (void)capability; }
AL_API ALboolean AL_APIENTRY alIsEnabled(ALenum capability) { (void)capability; return AL_FALSE; }

AL_API void AL_APIENTRY alDopplerFactor(ALfloat value) { (void)value; }
AL_API void AL_APIENTRY alDopplerVelocity(ALfloat value) { (void)value; }
AL_API void AL_APIENTRY alSpeedOfSound(ALfloat value) { (void)value; }
AL_API void AL_APIENTRY alDistanceModel(ALenum distanceModel) { (void)distanceModel; }

AL_API void AL_APIENTRY alListenerf(ALenum param, ALfloat value) { (void)param; (void)value; }
AL_API void AL_APIENTRY alListener3f(ALenum param, ALfloat v1, ALfloat v2, ALfloat v3) { (void)param; (void)v1; (void)v2; (void)v3; }
AL_API void AL_APIENTRY alListenerfv(ALenum param, const ALfloat *values) { (void)param; (void)values; }
AL_API void AL_APIENTRY alGetListenerf(ALenum param, ALfloat *value) { (void)param; (void)value; }
AL_API void AL_APIENTRY alGetListener3f(ALenum param, ALfloat *v1, ALfloat *v2, ALfloat *v3) { (void)param; (void)v1; (void)v2; (void)v3; }
AL_API void AL_APIENTRY alGetListenerfv(ALenum param, ALfloat *values) { (void)param; (void)values; }

/* ---------- buffers ---------- */

AL_API void AL_APIENTRY alGenBuffers(ALsizei n, ALuint *buffers)
{
    if (!buffers)
        return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < n; i++) {
        buffers[i] = 0;
        for (int b = 0; b < MAX_BUFFERS; b++) {
            if (!g_buffers[b].used) {
                memset(&g_buffers[b], 0, sizeof(ALBuffer));
                g_buffers[b].used = 1;
                buffers[i] = (ALuint)(b + 1);
                break;
            }
        }
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alDeleteBuffers(ALsizei n, const ALuint *buffers)
{
    if (!buffers)
        return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < n; i++) {
        ALBuffer *b = getBuf(buffers[i]);
        if (b) {
            free(b->data);
            b->data = NULL;
            b->used = 0;
        }
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API ALboolean AL_APIENTRY alIsBuffer(ALuint bid)
{
    return getBuf(bid) ? AL_TRUE : AL_FALSE;
}

AL_API void AL_APIENTRY alBufferData(ALuint bid, ALenum format, const ALvoid *data, ALsizei size, ALsizei freq)
{
    ALBuffer *b = getBuf(bid);
    if (!b || !data || size <= 0)
        return;
    int channels = 1, bits = 16;
    switch (format) {
        case AL_FORMAT_MONO8: channels = 1; bits = 8; break;
        case AL_FORMAT_MONO16: channels = 1; bits = 16; break;
        case AL_FORMAT_STEREO8: channels = 2; bits = 8; break;
        case AL_FORMAT_STEREO16: channels = 2; bits = 16; break;
        default:
            fprintf(stderr, "[openal-shim] alBufferData: unsupported format 0x%x\n", format);
            return;
    }
    pthread_mutex_lock(&g_lock);
    free(b->data);
    b->data = (uint8_t *)malloc(size);
    if (!b->data) {
        pthread_mutex_unlock(&g_lock);
        return;
    }
    memcpy(b->data, data, size);
    b->size = size;
    b->channels = channels;
    b->bits = bits;
    b->freq = freq;
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alBufferf(ALuint bid, ALenum param, ALfloat value) { (void)bid; (void)param; (void)value; }
AL_API void AL_APIENTRY alBuffer3f(ALuint bid, ALenum param, ALfloat v1, ALfloat v2, ALfloat v3) { (void)bid; (void)param; (void)v1; (void)v2; (void)v3; }
AL_API void AL_APIENTRY alBufferfv(ALuint bid, ALenum param, const ALfloat *values) { (void)bid; (void)param; (void)values; }
AL_API void AL_APIENTRY alBufferi(ALuint bid, ALenum param, ALint value) { (void)bid; (void)param; (void)value; }
AL_API void AL_APIENTRY alBufferiv(ALuint bid, ALenum param, const ALint *values) { (void)bid; (void)param; (void)values; }

AL_API void AL_APIENTRY alGetBufferi(ALuint bid, ALenum param, ALint *value)
{
    ALBuffer *b = getBuf(bid);
    if (!b || !value)
        return;
    switch (param) {
        case AL_FREQUENCY: *value = b->freq; break;
        case AL_BITS: *value = b->bits; break;
        case AL_CHANNELS: *value = b->channels; break;
        case AL_SIZE: *value = b->size; break;
        default: *value = 0;
    }
}

AL_API void AL_APIENTRY alGetBufferf(ALuint bid, ALenum param, ALfloat *value) { (void)bid; (void)param; (void)value; }

/* ---------- sources ---------- */

AL_API void AL_APIENTRY alGenSources(ALsizei n, ALuint *sources)
{
    if (!sources)
        return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < n; i++) {
        sources[i] = 0;
        for (int s = 0; s < MAX_SOURCES; s++) {
            if (!g_sources[s].used) {
                memset(&g_sources[s], 0, sizeof(ALSource));
                g_sources[s].used = 1;
                g_sources[s].state = AL_INITIAL;
                g_sources[s].gain = 1.0f;
                g_sources[s].pitch = 1.0f;
                sources[i] = (ALuint)(s + 1);
                break;
            }
        }
    }
    pthread_mutex_unlock(&g_lock);
}

static void freeSourceState(ALSource *s)
{
    while (s->qPlay) {
        QBuf *q = s->qPlay;
        s->qPlay = q->next;
        free(q);
    }
    while (s->qDone) {
        QBuf *q = s->qDone;
        s->qDone = q->next;
        free(q);
    }
    s->nQueued = 0;
    s->nProcessed = 0;
}

AL_API void AL_APIENTRY alDeleteSources(ALsizei n, const ALuint *sources)
{
    if (!sources)
        return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < n; i++) {
        ALSource *s = getSrc(sources[i]);
        if (s) {
            freeSourceState(s);
            s->used = 0;
        }
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API ALboolean AL_APIENTRY alIsSource(ALuint sid)
{
    return getSrc(sid) ? AL_TRUE : AL_FALSE;
}

AL_API void AL_APIENTRY alSourcePlay(ALuint sid)
{
    fprintf(stderr, "[openal-shim] Play sid=%u (cb=%d)\n", sid, g_cbCount);
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s && s->state != AL_PAUSED) {
        s->staticOffset = 0;
        if (s->qPlay)
            s->qPlay->offset = 0;
    }
    if (s)
        s->state = AL_PLAYING;
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourcePause(ALuint sid)
{
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s && s->state == AL_PLAYING)
        s->state = AL_PAUSED;
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourceStop(ALuint sid)
{
    fprintf(stderr, "[openal-shim] Stop sid=%u (cb=%d)\n", sid, g_cbCount);
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        s->state = AL_STOPPED;
        s->staticOffset = 0;
        while (s->qPlay) {
            QBuf *q = s->qPlay;
            s->qPlay = q->next;
            q->next = s->qDone;
            s->qDone = q;
            s->nProcessed++;
            s->nQueued--;
        }
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourceRewind(ALuint sid)
{
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        s->staticOffset = 0;
        if (s->qPlay)
            s->qPlay->offset = 0;
        s->state = AL_INITIAL;
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourcef(ALuint sid, ALenum param, ALfloat value)
{
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        if (param == AL_GAIN && value >= 0) s->gain = value;
        else if (param == AL_PITCH && value > 0) s->pitch = value;
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSource3f(ALuint sid, ALenum param, ALfloat v1, ALfloat v2, ALfloat v3)
{
    (void)v2; (void)v3;
    if (param != AL_POSITION)
        return;
    if (v1 < -1.0f) v1 = -1.0f;
    if (v1 > 1.0f) v1 = 1.0f;
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s)
        s->posX = v1;
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourcefv(ALuint sid, ALenum param, const ALfloat *values)
{
    if (!values)
        return;
    if (param == AL_POSITION)
        alSource3f(sid, param, values[0], values[1], values[2]);
}

AL_API void AL_APIENTRY alSourcei(ALuint sid, ALenum param, ALint value)
{
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (!s) {
        pthread_mutex_unlock(&g_lock);
        return;
    }
    if (param == AL_BUFFER) {
        fprintf(stderr, "[openal-shim] BIND sid=%u buf=%d\n", sid, value);
        freeSourceState(s);
        s->staticBuf = value > 0 ? (ALuint)value : 0;
        s->staticOffset = 0;
        s->state = AL_INITIAL;
    } else if (param == AL_LOOPING) {
        fprintf(stderr, "[openal-shim] LOOPING sid=%u val=%d\n", sid, value);
        s->looping = value;
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alGetSourcei(ALuint sid, ALenum param, ALint *value)
{
    if (!value)
        return;
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (!s) {
        *value = 0;
        pthread_mutex_unlock(&g_lock);
        return;
    }
    switch (param) {
        case AL_SOURCE_STATE: *value = s->state; break;
        case AL_BUFFERS_QUEUED: *value = s->nQueued; break;
        case AL_BUFFERS_PROCESSED: *value = s->nProcessed; break;
        case AL_BUFFER: *value = (ALint)s->staticBuf; break;
        default: *value = 0; break;
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alGetSourcef(ALuint sid, ALenum param, ALfloat *value)
{
    if (!value)
        return;
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        if (param == AL_GAIN) *value = s->gain;
        else if (param == AL_PITCH) *value = s->pitch;
        else *value = 0;
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourceQueueBuffers(ALuint sid, ALsizei nb, const ALuint *bids)
{
    if (!bids || nb <= 0)
        return;
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        static int s_qDiag = 0;
        if (s_qDiag++ < 40 || (s_qDiag % 200) == 0)
            fprintf(stderr, "[openal-shim] QUEUE sid=%u nb=%d (q=%d p=%d)\n", sid, nb, s->nQueued, s->nProcessed);
        s->staticBuf = 0;
        for (int i = 0; i < nb; i++) {
            QBuf *q = (QBuf *)calloc(1, sizeof(QBuf));
            q->bid = bids[i];
            if (!s->qPlay) {
                s->qPlay = q;
            } else {
                QBuf *t = s->qPlay;
                while (t->next)
                    t = t->next;
                t->next = q;
            }
            s->nQueued++;
        }
    }
    pthread_mutex_unlock(&g_lock);
}

AL_API void AL_APIENTRY alSourceUnqueueBuffers(ALuint sid, ALsizei nb, ALuint *bids)
{
    if (!bids || nb <= 0)
        return;
    pthread_mutex_lock(&g_lock);
    ALSource *s = getSrc(sid);
    if (s) {
        static int s_uDiag = 0;
        if (s_uDiag++ < 40 || (s_uDiag % 200) == 0)
            fprintf(stderr, "[openal-shim] UNQUEUE sid=%u nb=%d (q=%d p=%d)\n", sid, nb, s->nQueued, s->nProcessed);
        for (int i = 0; i < nb; i++) {
            bids[i] = 0;
            if (s->qDone) {
                QBuf *q = s->qDone;
                s->qDone = q->next;
                bids[i] = q->bid;
                free(q);
                if (s->nProcessed > 0)
                    s->nProcessed--;
            }
        }
    }
    pthread_mutex_unlock(&g_lock);
}
