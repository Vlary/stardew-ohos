/*
 * Copyright (c) 2023 Huawei Device Co., Ltd.
 * Licensed under the Apache License,Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#include "../../SDL_internal.h"

#ifdef SDL_VIDEO_DRIVER_OHOS
#if SDL_VIDEO_DRIVER_OHOS
#endif

#include "SDL_ohosevents.h"
#include "SDL_events.h"
#include "../SDL_sysvideo.h"
#include "../../events/SDL_events_c.h"

/* Can't include sysaudio "../../audio/ohos/SDL_ohosaudio.h"
 * because of THIS redefinition */

#if !SDL_OHOS_DISABLED && SDL_AUDIO_DRIVER_OHOS
extern void OHOSAUDIO_ResumeDevices(void);
extern void OHOSAUDIO_PauseDevices(void);
#else
static void OHOSAUDIO_ResumeDevices(void) {}
static void OHOSAUDIO_PauseDevices(void) {}
#endif

#if !SDL_AUDIO_DISABLED && SDL_AUDIO_DRIVER_OPENSLES
extern void openslES_ResumeDevices(void);
extern void openslES_PauseDevices(void);
#else
static void openslES_ResumeDevices(void) {}
static void openslES_PauseDevices(void) {}
#endif

/* Number of 'type' events in the event queue */
static int SDL_NumberOfEvents(Uint32 type) { return SDL_PeepEvents(NULL, 0, SDL_PEEKEVENT, type, type); }

static void OHOS_EGL_context_restore(SDL_Window *window)
{
    if (window) {
        SDL_Event event;
        SDL_WindowData *data = (SDL_WindowData *)window->driverdata;
        if (SDL_GL_MakeCurrent(window, (SDL_GLContext)data->egl_context) < 0) {
            /* The context is no longer valid, create a new one */
            data->egl_context = (EGLContext)SDL_GL_CreateContext(window);
            SDL_GL_MakeCurrent(window, (SDL_GLContext)data->egl_context);
            event.type = SDL_RENDER_DEVICE_RESET;
            SDL_PushEvent(&event);
        }
        data->backup_done = 0;
    }
}

static void OHOS_EGL_context_backup(SDL_Window *window)
{
    if (window) {
        /* Keep a copy of the EGL Context so we can try to restore it when we resume */
        SDL_WindowData *data = (SDL_WindowData *)window->driverdata;
        data->egl_context = SDL_GL_GetCurrentContext();
        /* We need to do this so the EGLSurface can be freed */
        SDL_GL_MakeCurrent(window, NULL);
        data->backup_done = 1;
    }
}

void OHOS_PUMPEVENTS_Blocking(SDL_VideoDevice *thisDevice)
{
    SDL_Window *ohosWindow = thisDevice->windows;
    SDL_VideoData *videodata = (SDL_VideoData *)thisDevice->driverdata;
    if (videodata->isPaused) {
        SDL_bool isContextExternal = SDL_IsVideoContextExternal();
        /* Make sure this is the last thing we do before pausing */
        if (!isContextExternal) {
            SDL_LockMutex(g_ohosPageMutex);
            OHOS_EGL_context_backup(ohosWindow);
            SDL_UnlockMutex(g_ohosPageMutex);
        }

        OHOSAUDIO_PauseDevices();
        openslES_PauseDevices();

        if (SDL_SemWait(g_ohosResumeSem) == 0) {
            videodata->isPaused = 0;

            /* g_ohosResumeSem was signaled */
            SDL_SendAppEvent(SDL_APP_WILLENTERFOREGROUND);
            SDL_SendAppEvent(SDL_APP_DIDENTERFOREGROUND);
            SDL_SendWindowEvent(ohosWindow, SDL_WINDOWEVENT_RESTORED, 0, 0);

            OHOSAUDIO_ResumeDevices();
            openslES_ResumeDevices();

            /* Restore the GL Context from here, as this operation is thread dependent */
            if (!isContextExternal && !SDL_HasEvent(SDL_QUIT)) {
                SDL_LockMutex(g_ohosPageMutex);
                OHOS_EGL_context_restore(ohosWindow);
                SDL_UnlockMutex(g_ohosPageMutex);
            }

            /* Make sure SW Keyboard is restored when an app becomes foreground */
            if (SDL_IsTextInputActive()) {
                OHOS_StartTextInput(thisDevice); /* Only showTextInput */
            }
        }
    } else {
        if (videodata->isPausing || SDL_SemTryWait(g_ohosPauseSem) == 0) {
            /* g_ohosPauseSem was signaled */
            if (videodata->isPausing == 0) {
                SDL_SendWindowEvent(ohosWindow, SDL_WINDOWEVENT_MINIMIZED, 0, 0);
                SDL_SendAppEvent(SDL_APP_WILLENTERBACKGROUND);
                SDL_SendAppEvent(SDL_APP_DIDENTERBACKGROUND);
            }
            /* We've been signaled to pause (potentially several times), but before we block ourselves,
             * we need to make sure that the very last event (of the first pause sequence, if several)
             * has reached the app */
            if (SDL_NumberOfEvents(SDL_APP_DIDENTERBACKGROUND) > SDL_SemValue(g_ohosPauseSem)) {
                videodata->isPausing = 1;
            } else {
                videodata->isPausing = 0;
                videodata->isPaused = 1;
            }
        }
    }
}

void OHOS_PUMPEVENTS_NonBlocking(SDL_VideoDevice *thisDevice)
{
    SDL_Window *ohosWindow = thisDevice->windows;
    SDL_VideoData *videodata = (SDL_VideoData *)thisDevice->driverdata;
    static int backup_context = 0;

    if (videodata->isPaused) {
        SDL_bool isContextExternal = SDL_IsVideoContextExternal();
        if (backup_context) {
            if (!isContextExternal) {
                SDL_LockMutex(g_ohosPageMutex);
                OHOS_EGL_context_backup(ohosWindow);
                SDL_UnlockMutex(g_ohosPageMutex);
            }

            OHOSAUDIO_PauseDevices();
            openslES_PauseDevices();

            backup_context = 0;
        }
        if (SDL_SemTryWait(g_ohosResumeSem) == 0) {
            videodata->isPaused = 0;

            /* g_ohosResumeSem was signaled */
            SDL_SendAppEvent(SDL_APP_WILLENTERFOREGROUND);
            SDL_SendAppEvent(SDL_APP_DIDENTERFOREGROUND);
            SDL_SendWindowEvent(ohosWindow, SDL_WINDOWEVENT_RESTORED, 0, 0);

            OHOSAUDIO_ResumeDevices();
            openslES_ResumeDevices();

            /* Restore the GL Context from here, as this operation is thread dependent */
            if (!isContextExternal && !SDL_HasEvent(SDL_QUIT)) {
                SDL_LockMutex(g_ohosPageMutex);
                OHOS_EGL_context_restore(ohosWindow);
                SDL_UnlockMutex(g_ohosPageMutex);
            }

            /* Make sure SW Keyboard is restored when an app becomes foreground */
            if (SDL_IsTextInputActive()) {
                OHOS_StartTextInput(thisDevice->windows); /* Only showTextInput */
            }
        }
    } else {
        if (videodata->isPausing || SDL_SemTryWait(g_ohosPauseSem) == 0) {
            /* g_ohosPauseSem was signaled */
            if (videodata->isPausing == 0) {
                SDL_SendWindowEvent(ohosWindow, SDL_WINDOWEVENT_MINIMIZED, 0, 0);
                SDL_SendAppEvent(SDL_APP_WILLENTERBACKGROUND);
                SDL_SendAppEvent(SDL_APP_DIDENTERBACKGROUND);
            }
            /* We've been signaled to pause (potentially several times), but before we block ourselves,
             * we need to make sure that the very last event (of the first pause sequence, if several)
             * has reached the app */
            if (SDL_NumberOfEvents(SDL_APP_DIDENTERBACKGROUND) > SDL_SemValue(g_ohosPauseSem)) {
                videodata->isPausing = 1;
            } else {
                videodata->isPausing = 0;
                videodata->isPaused = 1;
                backup_context = 1;
            }
        }
    }
}

#endif /* SDL_VIDEO_DRIVER_OHOS */

/* ---- OHOS input bridge: called from libentry.so (napi) to inject
 * real mouse / keyboard events from ArkTS XComponent callbacks.
 * Lives outside the OHOS ifdef-free zone intentionally below the guard
 * so it is only compiled for the OHOS driver build. ---- */
#if SDL_VIDEO_DRIVER_OHOS
#include "../../events/SDL_mouse_c.h"
#include "../../events/SDL_keyboard_c.h"

static float g_ohosDensity = 1.0f;

__attribute__((visibility("default"))) void SDL_OHOS_SetDensity(float density)
{
    if (density > 0.01f && density < 10.0f) {
        g_ohosDensity = density;
    }
}

__attribute__((visibility("default"))) void SDL_OHOS_SendMouseMotion(float x_vp, float y_vp)
{
    SDL_VideoDevice *dev = SDL_GetVideoDevice(); if (!dev || !dev->windows) {
        return;
    }
    SDL_SendMouseMotion(dev->windows, 0, 0,
                        (int)(x_vp * g_ohosDensity), (int)(y_vp * g_ohosDensity));
}

__attribute__((visibility("default"))) void SDL_OHOS_SendMouseButton(int down, int button)
{
    SDL_VideoDevice *dev = SDL_GetVideoDevice();
    SDL_Log("SDV-MOUSE: btn down=%d button=%d", down, button);
    if (!dev || !dev->windows) {
        return;
    }
    SDL_SendMouseButton(dev->windows, 0,
                        down ? SDL_PRESSED : SDL_RELEASED, (Uint8)button);
}

__attribute__((visibility("default"))) void SDL_OHOS_SendMouseWheel(float dx, float dy)
{
    SDL_VideoDevice *dev = SDL_GetVideoDevice(); if (!dev || !dev->windows) {
        return;
    }
    SDL_SendMouseWheel(dev->windows, 0, dx, dy, SDL_MOUSEWHEEL_NORMAL);
}

__attribute__((visibility("default"))) void SDL_OHOS_SendKey(int down, int scancode)
{
    SDL_Log("SDV-KEY t=%u: SendKey down=%d sc=%d", SDL_GetTicks(), down, scancode);
    SDL_SendKeyboardKey(down ? SDL_PRESSED : SDL_RELEASED, (SDL_Scancode)scancode);

    /* 物理键盘文本合成：SDL_TEXTINPUT 事件由平台负责生成（如桌面后端的
     * WM_CHAR），OHOS 后端没有这条链，游戏的文本框（如角色名）收不到字符。
     * mg380 从不调 SDL_StartTextInput，SDL_SendKeyboardText 会被
     * TextInputActive 过滤丢弃，因此这里直接构造事件入队。 */
    if (down) {
        char base = 0, shifted = 0;
        if (scancode >= 4 && scancode <= 29) {          /* a..z */
            base = (char)('a' + (scancode - 4));
            shifted = (char)('A' + (scancode - 4));
        } else if (scancode >= 30 && scancode <= 39) {  /* 1..0 */
            static const char n[] = "1234567890";
            static const char s[] = "!@#$%^&*()";
            base = n[scancode - 30];
            shifted = s[scancode - 30];
        } else {
            switch (scancode) {
            case 44: base = ' '; shifted = ' '; break;
            case 45: base = '-'; shifted = '_'; break;
            case 46: base = '='; shifted = '+'; break;
            case 47: base = '['; shifted = '{'; break;
            case 48: base = ']'; shifted = '}'; break;
            case 49: base = '\\'; shifted = '|'; break;
            case 51: base = ';'; shifted = ':'; break;
            case 52: base = '\''; shifted = '"'; break;
            case 53: base = '`'; shifted = '~'; break;
            case 54: base = ','; shifted = '<'; break;
            case 55: base = '.'; shifted = '>'; break;
            case 56: base = '/'; shifted = '?'; break;
            default: break;   /* 功能键/回车/退格等由 KEYDOWN 语义处理 */
            }
        }
        if (base) {
            SDL_Keymod mod = SDL_GetModState();
            SDL_Event evt;
            SDL_VideoDevice *dev = SDL_GetVideoDevice();
            char ch = base;
            if (mod & KMOD_SHIFT) ch = shifted;
            if ((mod & KMOD_CAPS) && base >= 'a' && base <= 'z') {
                ch = (mod & KMOD_SHIFT) ? base : shifted;
            }
            SDL_zero(evt);
            evt.type = SDL_TEXTINPUT;
            evt.text.timestamp = SDL_GetTicks();
            if (dev && dev->windows) {
                evt.text.windowID = dev->windows->id;
            }
            evt.text.text[0] = ch;
            SDL_Log("SDV-TEXT: push '%c' (sc=%d)", ch, scancode);
            SDL_PushEvent(&evt);
        }
    }
}

__attribute__((visibility("default"))) void SDL_OHOS_SendText(const char *text)
{
    SDL_SendKeyboardText(text);
}
#endif /* SDL_VIDEO_DRIVER_OHOS input bridge */

/* vi: set ts=4 sw=4 expandtab: */
