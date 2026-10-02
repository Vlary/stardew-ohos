/* 最小 OpenAL 类型/调用约定定义（避免依赖 openal 头文件） */
#ifndef AL_SHIM_TYPES_H
#define AL_SHIM_TYPES_H

#define AL_API
#define AL_APIENTRY
#define ALC_API
#define ALC_APIENTRY
#define ALCAPI
#define ALCAPIENTRY

typedef char ALchar;
typedef signed char ALbyte;
typedef unsigned char ALboolean;
typedef short ALshort;
typedef unsigned short ALushort;
typedef int ALint;
typedef unsigned int ALuint;
typedef int ALsizei;
typedef int ALenum;
typedef float ALfloat;
typedef double ALdouble;
typedef void ALvoid;

typedef char ALCchar;
typedef signed char ALCbyte;
typedef unsigned char ALCboolean;
typedef short ALCshort;
typedef unsigned short ALCushort;
typedef int ALCint;
typedef unsigned int ALCuint;
typedef int ALCsizei;
typedef int ALCenum;
typedef float ALCfloat;
typedef double ALCdouble;
typedef void ALCvoid;

struct ALCdevice_struct;
typedef struct ALCdevice_struct ALCdevice;
struct ALCcontext_struct;
typedef struct ALCcontext_struct ALCcontext;

#endif
